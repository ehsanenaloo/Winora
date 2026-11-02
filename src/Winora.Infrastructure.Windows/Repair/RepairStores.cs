// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Repair;

/// <summary>The reviewed feature list that ships in the app (definitions/features/FeatureClasses.v1.json).</summary>
public static class EmbeddedFeatureClasses
{
    public const string ResourceName = "Winora.FeatureClasses.v1.json";

    public static FeatureClassCatalog Load()
    {
        using var stream = typeof(EmbeddedFeatureClasses).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("The feature file is missing from the app.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return FeatureClassCatalog.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// Append-only JSON Lines record of repair runs and feature changes: a "begin" line with the exact command is flushed to disk before the program starts and an "end" line follows it. A begin line
/// without an end line is a run that was interrupted. Nothing is rewritten and nothing leaves the machine. The file lives in the admin-only folder.
/// </summary>
public sealed class FileRepairJournal(Func<string> pathProvider) : IRepairJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    public FileRepairJournal(string path) : this(() => path) { }

    private sealed record Line(string Type, DateTimeOffset At, RepairBegin? Begin, Guid? Id, RepairEnd? End);

    public void Begin(RepairBegin begin, DateTimeOffset atUtc) => Append(new("begin", atUtc, begin, null, null));

    public void Complete(Guid id, RepairEnd end, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, end));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<RepairRecord> All)? cache;

    public IReadOnlyList<RepairRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (RepairBegin Begin, DateTimeOffset At)>();
        var ends = new Dictionary<Guid, Line>();
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
                    if (text.Length == 0 || text.Length > 100_000) continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(text, Json); }
                    catch (JsonException) { continue; }
                    if (line is { Type: "begin", Begin: { } begin } && begins.TryAdd(begin.Id, (begin, line.At))) order.Add(begin.Id);
                    else if (line is { Type: "end", Id: { } id, End: not null }) ends[id] = line;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        var all = order.Select(id =>
        {
            var (begin, at) = begins[id];
            return ends.TryGetValue(id, out var end) ? new RepairRecord(begin, at, end.At, end.End) : new RepairRecord(begin, at, null, null);
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}
