// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

/// <summary>One power plan as the power API reports it. A null name means the name could not be read.</summary>
internal sealed record PowerSchemeEntry(Guid Id, string? Name);

internal sealed record PowerSchemeSnapshot(IReadOnlyList<PowerSchemeEntry> Schemes, Guid? Active);

internal interface IPowerSchemeReader
{
    /// <summary>Throws on source-level failure (the list or the active plan cannot be read).</summary>
    PowerSchemeSnapshot Read(CancellationToken cancellationToken);
}

/// <summary>Read-only PowrProf calls: enumerate plans, read each friendly name, read the active plan. No plan is created, changed, activated or deleted.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PowrProfReader : IPowerSchemeReader
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259, ErrorMoreData = 234;
    private const int MaxSchemes = 256;
    private const uint MaxNameBytes = 4096;

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public PowerSchemeSnapshot Read(CancellationToken ct)
    {
        var schemes = new List<PowerSchemeEntry>();
        for (uint index = 0; index < MaxSchemes; index++)
        {
            ct.ThrowIfCancellationRequested();
            var buffer = new byte[16];
            var size = (uint)buffer.Length;
            var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
            if (rc == ErrorNoMoreItems) break;
            if (rc != 0 || size != 16) throw new Win32Exception((int)rc);
            var id = new Guid(buffer);
            schemes.Add(new(id, ReadName(id)));
        }

        var active = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        if (active != 0 || pointer == IntPtr.Zero) throw new Win32Exception((int)active);
        try { return new(schemes, Marshal.PtrToStructure<Guid>(pointer)); }
        finally { LocalFree(pointer); }
    }

    private static string? ReadName(Guid id)
    {
        uint size = 0;
        var rc = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((rc != 0 && rc != ErrorMoreData) || size == 0 || size > MaxNameBytes) return null;
        var buffer = new byte[size];
        rc = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
        if (rc != 0) return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }
}

/// <summary>Read-only inventory of power plans. The built-in plans are recognized by the GUIDs Windows defines; every other plan is custom.</summary>
public sealed class PowerPlanDiscovery : IDiscoverySource<PowerPlanInfo>
{
    // winnt.h: GUID_MAX_POWER_SAVINGS (power saver), GUID_TYPICAL_POWER_SAVINGS (balanced), GUID_MIN_POWER_SAVINGS (high performance).
    private static readonly HashSet<Guid> BuiltIn =
    [
        new("a1841308-3541-4fab-bc81-f71556f20b4a"),
        new("381b4222-f694-41f0-9685-ff5bb260df2e"),
        new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
    ];

    private static readonly string SourceName = nameof(PowerPlanInfo);
    private readonly IPowerSchemeReader? reader;
    private readonly Func<DateTimeOffset> clock;

    public PowerPlanDiscovery() : this(OperatingSystem.IsWindows() ? new PowrProfReader() : null, () => DateTimeOffset.UtcNow) { }

    internal PowerPlanDiscovery(IPowerSchemeReader? reader, Func<DateTimeOffset> clock)
    {
        this.reader = reader; this.clock = clock;
    }

    public Task<DiscoveryResult<PowerPlanInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader is null) return Task.FromResult(DiscoverySupport.Unavailable<PowerPlanInfo>(clock()));
        return Task.Run(() => Collect(reader, cancellationToken), cancellationToken);
    }

    private DiscoveryResult<PowerPlanInfo> Collect(IPowerSchemeReader source, CancellationToken ct)
    {
        PowerSchemeSnapshot snapshot;
        try { snapshot = source.Read(ct); }
        catch (Exception e) when (!DiscoverySupport.IsCancellation(e)) { return new([], [DiscoverySupport.FromException(SourceName, e, "powrprof")], clock()); }

        var items = new List<PowerPlanInfo>();
        var issues = new List<DiscoveryIssue>();
        foreach (var scheme in snapshot.Schemes)
        {
            var id = scheme.Id.ToString("D");
            var name = DiscoverySupport.Cap(scheme.Name is { } n ? new string(n.Where(c => !char.IsControl(c)).ToArray()).Trim() : null, 256);
            if (string.IsNullOrEmpty(name) || name.StartsWith('@'))
            {
                // An unreadable name or an unresolved resource reference is shown as the plan's id, never as raw indirection text.
                issues.Add(DiscoverySupport.Issue(SourceName, ErrorCode.DiscoveryItemUnreadable, id));
                name = id;
            }
            items.Add(new(id, name, snapshot.Active == scheme.Id, BuiltIn.Contains(scheme.Id) ? PowerPlanKind.BuiltIn : PowerPlanKind.Custom));
        }
        return new(items, issues, clock());
    }
}
