// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using Winora.Application;

namespace Winora.Infrastructure.Windows.RestorePoint;

/// <summary>Append-only JSON Lines record of restore point attempts. A begin line is flushed to disk before the attempt; an end line follows it.</summary>
public sealed class FileRestorePointJournal : IRestorePointJournal
{
    private readonly Func<string> pathProvider;
    private readonly object gate = new();

    public FileRestorePointJournal(string path) : this(() => path) { }

    /// <summary>The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).</summary>
    public FileRestorePointJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private sealed record Line(string Type, DateTimeOffset At, Guid Id, string? Description, bool? Ok, string? Code, long? Sequence, bool? MayBeReused);

    public void Begin(Guid id, string description, DateTimeOffset atUtc) => Append(new("begin", atUtc, id, description, null, null, null, null));

    public void Complete(Guid id, bool ok, string code, long sequence, bool mayBeReused, DateTimeOffset atUtc) => Append(new("end", atUtc, id, null, ok, code, sequence, mayBeReused));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    public IReadOnlyList<RestorePointRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, Line>();
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
                    try { line = JsonSerializer.Deserialize<Line>(text); }
                    catch (JsonException) { continue; }
                    if (line is { Type: "begin" } && begins.TryAdd(line.Id, line)) order.Add(line.Id);
                    else if (line is { Type: "end" }) ends[line.Id] = line;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        return order.Take(max).Select(id =>
        {
            var b = begins[id];
            return ends.TryGetValue(id, out var e)
                ? new RestorePointRecord(id, b.At, b.Description ?? "", e.At, e.Ok, e.Code ?? "", e.Sequence ?? 0, e.MayBeReused ?? false)
                : new RestorePointRecord(id, b.At, b.Description ?? "", null, null, "", 0, false);
        }).ToList();
    }
}
