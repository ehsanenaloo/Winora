// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Append-only JSON Lines record of program removals: a "begin" line is flushed to disk before the uninstaller starts and an "end" line follows. A begin line without an end line is a removal
/// that was interrupted. Nothing is rewritten and nothing leaves the machine. The file lives in the admin-only folder.
/// </summary>
public sealed class FileAppUninstallJournal(Func<string> pathProvider) : IAppUninstallJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    public FileAppUninstallJournal(string path) : this(() => path) { }

    private sealed record Line(string Type, DateTimeOffset At, AppUninstallChange? Change, Guid? Id, AppUninstallEnd? End);

    public void Begin(AppUninstallChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null));

    public void Complete(Guid id, AppUninstallEnd end, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, end));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    public IReadOnlyList<AppUninstallRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (AppUninstallChange Change, DateTimeOffset At)>();
        var ends = new Dictionary<Guid, Line>();
        var order = new List<Guid>();
        lock (gate)
        {
            string file;
            try { file = pathProvider(); } catch (IOException) { return []; }
            if (!File.Exists(file)) return [];
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } text)
                {
                    if (text.Length == 0 || text.Length > 100_000) continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(text, Json); }
                    catch (JsonException) { continue; }
                    if (line is { Type: "begin", Change: { } change } && begins.TryAdd(change.Id, (change, line.At))) order.Add(change.Id);
                    else if (line is { Type: "end", Id: { } id, End: not null }) ends[id] = line;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        return order.Take(max).Select(id =>
        {
            var (change, at) = begins[id];
            return ends.TryGetValue(id, out var end) ? new AppUninstallRecord(change, at, end.At, end.End) : new AppUninstallRecord(change, at, null, null);
        }).ToList();
    }
}
