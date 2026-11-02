// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Bloatware;

/// <summary>
/// Append-only JSON Lines record of package changes. A "begin" line, with everything that describes the package, is flushed to disk before the first step; an "end" line
/// follows. A begin line without an end line is a change that was interrupted. Nothing is rewritten and nothing leaves the machine.
/// </summary>
public sealed class FileBloatJournal : IBloatJournal
{
    private readonly Func<string> pathProvider;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    public FileBloatJournal(string path) : this(() => path) { }

    /// <summary>The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).</summary>
    public FileBloatJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private sealed record Line(string Type, DateTimeOffset At, BloatChange? Change, Guid? Id, BloatState? State, IReadOnlyList<BloatStepResult>? Steps);

    public void Begin(BloatChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null));

    public void Complete(Guid id, BloatState state, IReadOnlyList<BloatStepResult> steps, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, steps));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    public IReadOnlyList<BloatChangeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (BloatChange Change, DateTimeOffset At)>();
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
                    if (text.Length == 0 || text.Length > 200_000) continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(text, Json); }
                    catch (JsonException) { continue; }
                    if (line is { Type: "begin", Change: { } change } && begins.TryAdd(change.Id, (change, line.At))) order.Add(change.Id);
                    else if (line is { Type: "end", Id: { } id }) ends[id] = line;
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        return order.Take(max).Select(id =>
        {
            var (change, at) = begins[id];
            return ends.TryGetValue(id, out var end) ? new BloatChangeRecord(change, at, end.At, end.State, end.Steps ?? []) : new BloatChangeRecord(change, at, null, null, []);
        }).ToList();
    }
}

/// <summary>The rule list that ships in the app (definitions/bloatware/BloatwareRules.v1.json). It goes through the strict parser; a file that fails is an error, never a partial list.</summary>
public static class EmbeddedBloatwareRules
{
    public const string ResourceName = "Winora.BloatwareRules.v1.json";

    public static BloatRuleSet Load()
    {
        using var stream = typeof(EmbeddedBloatwareRules).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("The bloatware rule file is missing from the app.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return BloatRuleSet.Parse(reader.ReadToEnd());
    }
}
