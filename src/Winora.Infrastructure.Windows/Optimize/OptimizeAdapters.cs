// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using Winora.Elevation.Protocol.Definitions;

namespace Winora.Infrastructure.Windows.Optimize;

/// <summary>
/// The definition set that ships inside the app: every file definitions/machine/MachineDefinitions.v1*.json, embedded at build time as <c>Winora.&lt;file name&gt;</c>. The set is the
/// union of the files, read in ordinal order of the resource name. It goes through the same strict parser and the same admission invariants as a signed set, plus the cross-file
/// checks (one id, one review reference and one target per definition); it needs no signature because it is part of the signed app itself.
/// </summary>
public sealed class EmbeddedMachineDefinitions : IMachineDefinitionSource
{
    public const string ResourceName = "Winora.MachineDefinitions.v1.json";
    public const string ResourcePrefix = "Winora.MachineDefinitions.";
    private readonly Lazy<DefinitionSetResult> result;

    public EmbeddedMachineDefinitions() : this(ReadResources) { }

    internal EmbeddedMachineDefinitions(Func<byte[]?> bytes) : this(() => bytes() is { } one ? [one] : []) { }

    internal EmbeddedMachineDefinitions(Func<IReadOnlyList<byte[]>> files) => result = new(() => Load(files()));

    public DefinitionSetResult Get() => result.Value;

    private static IReadOnlyList<byte[]> ReadResources()
    {
        var assembly = typeof(EmbeddedMachineDefinitions).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var files = new List<byte[]>();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files.Add(memory.ToArray());
        }
        return files;
    }

    private static DefinitionSetResult Load(IReadOnlyList<byte[]> files)
    {
        var merged = DefinitionSetMerger.Merge(files);
        if (!merged.Ok) return new(null, ErrorCode.DefinitionSignatureInvalid);
        var set = merged.Set!;
        return new(new VerifiedDefinitionSet(set.Serial, DefinitionSetHash.ComputeMany(files).ToHex(), set.Definitions, set.Revoked.Select(r => new RevokedEntry(r.Id, r.BelowVersion, r.AllowRestore)).ToList()), null);
    }
}

/// <summary>Append-only JSON Lines record of machine setting changes in the user's application data folder. A begin line is flushed to disk before a change; an end line follows it.</summary>
public sealed class FileOptimizeJournal : IOptimizeJournal
{
    private readonly Func<string> pathProvider;

    public FileOptimizeJournal(string path) : this(() => path) { }

    /// <summary>The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).</summary>
    public FileOptimizeJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private string path => pathProvider();

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    private sealed record Line(string Type, DateTimeOffset At, OptimizeChange? Change, Guid? Id, OptimizeState? State, string? Code);

    public void Begin(OptimizeChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null));

    public void Complete(Guid id, OptimizeState state, string code, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, code));

    public void Written(Guid id, DateTimeOffset atUtc) => Append(new("written", atUtc, null, id, null, null));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(path, bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<OptimizeRecord> All)? cache;

    public IReadOnlyList<OptimizeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (OptimizeChange Change, DateTimeOffset At)>();
        var ends = new Dictionary<Guid, Line>();
        var written = new HashSet<Guid>();
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
                    if (line is { Type: "begin", Change: { } change } && begins.TryAdd(change.Id, (change, line.At))) order.Add(change.Id);
                    else if (line is { Type: "end", Id: { } id }) ends[id] = line;
                    else if (line is { Type: "written", Id: { } writtenId }) written.Add(writtenId);
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        var all = order.Select(id =>
        {
            var (change, at) = begins[id];
            return ends.TryGetValue(id, out var end) ? new OptimizeRecord(change, at, end.At, end.State, end.Code ?? "", written.Contains(id)) : new OptimizeRecord(change, at, null, null, "", written.Contains(id));
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}
