// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// Append-only JSON Lines record of permission changes: a "begin" line is flushed to disk before the first value is written and an "end" line follows it. A begin line without an end line is a
/// change that was interrupted. Nothing is rewritten and nothing leaves the machine. The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).
/// </summary>
public sealed class FilePermissionJournal(Func<string> pathProvider) : IPermissionJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    public FilePermissionJournal(string path) : this(() => path) { }

    private sealed record Line(string Type, DateTimeOffset At, PermissionChange? Change, Guid? Id, PermissionChangeState? State, string? Code, IReadOnlyList<PermissionEntryResult>? Results);

    public void Begin(PermissionChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null, null));

    public void Complete(Guid id, PermissionChangeState state, string code, IReadOnlyList<PermissionEntryResult> results, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, code, results));

    // "written_all": every write of the change was made. The older "written" line was appended after the first write only (see History).
    public void Written(Guid id, DateTimeOffset atUtc) => Append(new("written_all", atUtc, null, id, null, null, null));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<PermissionChangeRecord> All)? cache;

    public IReadOnlyList<PermissionChangeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (PermissionChange Change, DateTimeOffset At)>();
        var ends = new Dictionary<Guid, Line>();
        var written = new HashSet<Guid>();
        var firstWriteOnly = new HashSet<Guid>();
        var order = new List<Guid>();
        JournalStamp stamp = default;
        lock (gate)
        {
            string file;
            try { file = pathProvider(); } catch (IOException) { return []; }
            if (!File.Exists(file)) return [];
            stamp = JournalStamp.Of(file);
            if (cache is { } cached && cached.Stamp == stamp) return cached.All.Take(max).ToList();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } text)
                {
                    if (text.Length == 0 || text.Length > 200_000) continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(text, Json); }
                    catch (JsonException) { continue; }
                    if (line is { Type: "begin", Change: { } change } && begins.TryAdd(change.Id, (change, line.At))) order.Add(change.Id);
                    else if (line is { Type: "end", Id: { } id }) ends[id] = line;
                    else if (line is { Type: "written_all", Id: { } writtenId }) written.Add(writtenId);
                    else if (line is { Type: "written", Id: { } firstId }) firstWriteOnly.Add(firstId);
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        var all = order.Select(id =>
        {
            var (change, at) = begins[id];
            // A line of the older kind proves only the first write: it counts for a change of one value and for nothing more.
            var proven = written.Contains(id) || firstWriteOnly.Contains(id) && change.Entries.Count == 1;
            return ends.TryGetValue(id, out var end)
                ? new PermissionChangeRecord(change, at, end.At, end.State, end.Code ?? "", end.Results ?? [], proven)
                : new PermissionChangeRecord(change, at, null, null, "", [], proven);
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}
