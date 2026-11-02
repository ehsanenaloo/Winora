// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// The only code that writes the power configuration (a source scan test enforces it): the documented PowrProf functions to read and write setting values, to apply a plan, to copy a plan and to
/// delete one. Plan and setting identifiers come from the closed catalog in <see cref="PowerSettings"/> or from the plans Windows lists; nothing takes a path, a command or free text except the
/// name of a copy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowrProfPlans : IPowerPlans
{
    private const uint ErrorAccessDenied = 5, ErrorFileNotFound = 2, ErrorNotSupported = 50, ErrorInvalidParameter = 87, ErrorNoMoreItems = 259;
    private static readonly HashSet<Guid> BuiltIn =
    [
        new("a1841308-3541-4fab-bc81-f71556f20b4a"),
        new("381b4222-f694-41f0-9685-ff5bb260df2e"),
        new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
    ];

    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRoot, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, out IntPtr destination);
    [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public PowerSnapshot Read(CancellationToken cancellationToken)
    {
        var raw = new PowrProfReader().Read(cancellationToken);
        var plans = raw.Schemes.Select(s => new PowerPlanRef(s.Id, string.IsNullOrWhiteSpace(s.Name) || s.Name.StartsWith('@') ? s.Id.ToString("D") : s.Name.Trim(), raw.Active == s.Id, BuiltIn.Contains(s.Id))).ToList();
        return new(plans);
    }

    public uint? ReadValue(Guid plan, PowerSettingDefinition setting, PowerSource source)
    {
        var subgroup = setting.Subgroup; var id = setting.Setting;
        var error = source == PowerSource.Ac ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref id, out var value) : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref id, out value);
        return error == 0 ? value : null;
    }

    public PowerResult WriteValue(Guid plan, PowerSettingDefinition setting, PowerSource source, uint value, uint? expectedBefore = null)
    {
        if (expectedBefore is { } expected && ReadValue(plan, setting, source) != expected) return PowerResult.Fail(PowerCode.Stale);
        var subgroup = setting.Subgroup; var id = setting.Setting;
        var error = source == PowerSource.Ac ? PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref id, value) : PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref id, value);
        return Map(error);
    }

    public PowerResult SetActive(Guid plan) => Map(PowerSetActiveScheme(IntPtr.Zero, ref plan));

    public PowerResult Duplicate(Guid source, string name, out Guid created)
    {
        created = Guid.Empty;
        var error = PowerDuplicateScheme(IntPtr.Zero, ref source, out var pointer);
        if (error != 0 || pointer == IntPtr.Zero) return Map(error == 0 ? ErrorInvalidParameter : error);
        try { created = Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
        var bytes = Encoding.Unicode.GetBytes(name + "\0");
        var named = PowerWriteFriendlyName(IntPtr.Zero, ref created, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length);
        if (named == 0) return PowerResult.Success;
        // A copy without its name would be a plan nobody owns: remove it again before reporting the failure.
        var orphan = created;
        PowerDeleteScheme(IntPtr.Zero, ref orphan);
        created = Guid.Empty;
        return Map(named);
    }

    public PowerResult Delete(Guid plan) => Map(PowerDeleteScheme(IntPtr.Zero, ref plan));

    private static PowerResult Map(uint error) => error switch
    {
        0 => PowerResult.Success,
        ErrorAccessDenied => PowerResult.Fail(PowerCode.AccessDenied),
        ErrorFileNotFound or ErrorNotSupported or ErrorNoMoreItems => PowerResult.Fail(PowerCode.Unsupported),
        _ => PowerResult.Fail(PowerCode.Failed),
    };
}

/// <summary>
/// Hibernation file and battery facts. The file is created or removed with CallNtPowerInformation, information level SystemReserveHiberFile (10), the same effect as turning hibernation on or
/// off in Power Options; the state is read with level SystemPowerCapabilities (4). The battery flag comes from GetSystemPowerStatus. Neither call takes a path or free text.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerSystem : IPowerSystem
{
    private const int SystemPowerCapabilities = 4, SystemReserveHiberFile = 10, CapabilitiesSize = 160, HiberFilePresentOffset = 8;
    private const uint StatusAccessDenied = 0xC0000022;

    [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, byte[]? output, uint outputSize);
    [DllImport("powrprof.dll", EntryPoint = "CallNtPowerInformation")] private static extern uint CallNtPowerInformationSet(int level, ref byte input, uint inputSize, IntPtr output, uint outputSize);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    public bool HasBattery() => GetSystemPowerStatus(out var s) && s.BatteryFlag is not (128 or 255);

    public bool? HibernationEnabled()
    {
        var buffer = new byte[CapabilitiesSize];
        return CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0 ? buffer[HiberFilePresentOffset] != 0 : null;
    }

    public PowerResult SetHibernation(bool enabled)
    {
        var flag = (byte)(enabled ? 1 : 0);
        var status = CallNtPowerInformationSet(SystemReserveHiberFile, ref flag, 1, IntPtr.Zero, 0);
        return status switch { 0 => PowerResult.Success, StatusAccessDenied => PowerResult.Fail(PowerCode.AccessDenied), _ => PowerResult.Fail(PowerCode.Failed) };
    }
}

/// <summary>
/// Append-only JSON Lines record of power changes in the user's own application data folder: a "begin" line is flushed to disk before a change is made and an "end" line follows it. A begin
/// line without an end line is a change that was interrupted. Nothing is rewritten and nothing leaves the machine.
/// </summary>
public sealed class FilePowerJournal : IPowerJournal
{
    private readonly Func<string> pathProvider;

    public FilePowerJournal(string path) : this(() => path) { }

    /// <summary>The path is asked for on every use, so a folder that stops being trustworthy is refused each time (see <see cref="AdminOnlyFolder"/>).</summary>
    public FilePowerJournal(Func<string> pathProvider) { this.pathProvider = pathProvider; }

    private string path => pathProvider();

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    private sealed record Line(string Type, DateTimeOffset At, PowerChange? Change, Guid? Id, PowerChangeState? State, string? Code, Guid? Other);

    public void Begin(PowerChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null, null));

    public void Complete(Guid id, PowerChangeState state, string code, Guid? otherPlan, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, code, otherPlan));

    public void Written(Guid id, DateTimeOffset atUtc) => Append(new("written", atUtc, null, id, null, null, null));

    public void NoteCreated(Guid id, Guid plan, DateTimeOffset atUtc) => Append(new("note", atUtc, null, id, null, null, plan));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(path, bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<PowerChangeRecord> All)? cache;

    public IReadOnlyList<PowerChangeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (PowerChange Change, DateTimeOffset At)>();
        var ends = new Dictionary<Guid, Line>();
        var notes = new Dictionary<Guid, Guid>();
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
                    else if (line is { Type: "note", Id: { } noteId, Other: { } created }) notes[noteId] = created;
                    else if (line is { Type: "written", Id: { } writtenId }) written.Add(writtenId);
                }
            }
            catch (IOException) { return []; }
        }
        order.Reverse();
        var all = order.Select(id =>
        {
            var (change, at) = begins[id];
            if (!ends.TryGetValue(id, out var end)) return new PowerChangeRecord(notes.TryGetValue(id, out var noted) ? change with { OtherPlan = noted } : change, at, null, null, "", written.Contains(id));
            return new PowerChangeRecord(end.Other is { } other ? change with { OtherPlan = other } : change, at, end.At, end.State, end.Code ?? "", written.Contains(id));
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}
