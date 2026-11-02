// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Cleanup;

/// <summary>
/// Append-only JSON Lines record of quarantine changes (move, restore, delete, purge). A "begin" line is flushed to disk before the change touches anything; an "end" line follows.
/// A begin without an end is a change that was interrupted. Nothing is rewritten or removed, and nothing leaves the machine.
/// </summary>
public sealed class FileQuarantineJournal(Func<string> pathProvider) : IQuarantineJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    private readonly object gate = new();

    private sealed record Line(string Type, QuarantineChange? Change, QuarantineChangeEnd? End);

    public void Begin(QuarantineChange change) => Append(new("begin", change, null));

    public void Complete(QuarantineChangeEnd end) => Append(new("end", null, end));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    public IReadOnlyList<QuarantineRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, QuarantineChange>();
        var ends = new Dictionary<Guid, QuarantineChangeEnd>();
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
                    catch (JsonException) { continue; } // a damaged line is skipped, the rest stays readable
                    if (line is { Type: "begin", Change: { } change } && begins.TryAdd(change.Id, change)) order.Add(change.Id);
                    else if (line is { Type: "end", End: { } end }) ends[end.Id] = end;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        return order.Take(max).Select(id => new QuarantineRecord(begins[id], ends.GetValueOrDefault(id))).ToList();
    }
}
