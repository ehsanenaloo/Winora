// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;
namespace Winora.Infrastructure.Windows;

/// <summary>
/// Append-only JSON Lines record of scheduled task changes: a "begin" line is flushed to disk before a change is made and an "end" line follows it. A begin line without an end line is a
/// change that was interrupted. Nothing is rewritten and nothing leaves the machine. The path is asked for on every use, so a folder that stops being trustworthy is refused each time
/// (see <see cref="AdminOnlyFolder"/>).
/// </summary>
public sealed class FileScheduledTaskJournal : IScheduledTaskJournal
{
    private readonly Func<string> pathProvider;

    public FileScheduledTaskJournal(string path) : this(() => path) { }

    public FileScheduledTaskJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    private sealed record Line(string Type, DateTimeOffset At, ScheduledTaskChange? Change, Guid? Id, ScheduledTaskChangeState? State, string? Code, string? After);

    public void Written(Guid id, DateTimeOffset atUtc) => Append(new("written", atUtc, null, id, null, null, null));

    public void Begin(ScheduledTaskChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null, null));

    public void Complete(Guid id, ScheduledTaskChangeState state, string code, string? afterFingerprint, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, code, afterFingerprint));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<ScheduledTaskChangeRecord> All)? cache;

    public IReadOnlyList<ScheduledTaskChangeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (ScheduledTaskChange Change, DateTimeOffset At)>();
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
            if (!ends.TryGetValue(id, out var end)) return new ScheduledTaskChangeRecord(change, at, null, null, "", written.Contains(id));
            return new ScheduledTaskChangeRecord(end.After is not null ? change with { AfterFingerprint = end.After } : change, at, end.At, end.State, end.Code ?? "", written.Contains(id));
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}

/// <summary>
/// Copies of the task definitions that Winora deleted, one file per copy in a folder that only administrators can change. The file name is a random id, never a part of the task path or name,
/// so a task name cannot steer where a file goes. A copy is written once, flushed to disk and never overwritten or removed by Winora.
/// </summary>
public sealed class ScheduledTaskBackupStore : IScheduledTaskBackups
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly Func<string> folderProvider;

    public ScheduledTaskBackupStore(Func<string> folderProvider) { this.folderProvider = folderProvider; }

    /// <summary>The folder for the copies: a protected sub folder of the journal folder.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string DefaultFolder() => AdminOnlyFolder.Ensure(Path.Combine(AdminOnlyFolder.Ensure(AdminOnlyFolder.DefaultPath()), "task-backups"));

    public string Save(string xml)
    {
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        if (bytes.Length is 0 or > MaxBytes) throw new IOException("The task definition has an unsupported size.");
        var folder = folderProvider();
        var id = Guid.NewGuid().ToString("N");
        using var stream = new FileStream(FilePath(folder, id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return id;
    }

    public string? Load(string id)
    {
        if (!IsId(id)) return null;
        var path = FilePath(folderProvider(), id);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is 0 or > MaxBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string FilePath(string folder, string id) => Path.Combine(folder, id + ".xml");
}
