// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Lists the adapters that are up (System.Net.NetworkInformation, which calls the documented GetAdaptersAddresses) and reads their DNS settings with GetInterfaceDnsSettings. A write goes
/// through <see cref="DnsInterfaceSettings"/>. Hardware addresses are not read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDnsAdapters : IDnsAdapters
{
    private static readonly (string Token, string Reason)[] Tokens =
    [
        ("hyper-v", "vm"), ("vethernet", "vm"), ("vmware", "vm"), ("virtualbox", "vm"), ("vbox", "vm"), ("virtual", "vm"), ("wsl", "vm"),
        ("wintun", "vpn"), ("wireguard", "vpn"), ("openvpn", "vpn"), ("tap-windows", "vpn"), ("tap-", "vpn"), ("tailscale", "vpn"), ("zerotier", "vpn"), ("nordlynx", "vpn"),
        ("anyconnect", "vpn"), ("fortinet", "vpn"), ("globalprotect", "vpn"), ("pangp", "vpn"), ("vpn", "vpn"), ("npcap", "vpn"), ("pseudo", "vpn"),
    ];

    public NetworkSnapshot Read(CancellationToken cancellationToken)
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return NetworkSnapshot.Unavailable; }
        var list = new List<NetworkAdapter>();
        var support = DnsSupport.Available;
        foreach (var nic in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!Guid.TryParse(nic.Id, out var id)) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            // Filter drivers and bridges are "up" without an address; they have no DNS settings to show.
            if (props.UnicastAddresses.Count == 0) continue;
            var v4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
            var v6 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList();
            var effective = props.DnsAddresses.Select(a => StripScope(a)).ToList();
            var dhcp = false;
            try { dhcp = props.GetIPv4Properties().IsDhcpEnabled; } catch (NetworkInformationException) { }
            DnsSettings? dns = null; string? code = null;
            var (f4, c4) = DnsInterfaceSettings.Read(id, false);
            var (f6, c6) = DnsInterfaceSettings.Read(id, true);
            if (c4 == NetworkCode.Unsupported && c6 == NetworkCode.Unsupported) support = DnsSupport.Unavailable;
            if (f4 is not null && f6 is not null) dns = new(f4, f6);
            else code = c4 ?? c6;
            list.Add(new(id, nic.Name, nic.Description, KindOf(nic.NetworkInterfaceType), VirtualReason(nic), v4, v6, effective, dhcp, dns, code));
        }
        return new(list, list.Count > 0 && support == DnsSupport.Unavailable ? DnsSupport.Unavailable : DnsSupport.Available);
    }

    private static string StripScope(IPAddress a) { var s = a.ToString(); var i = s.IndexOf('%'); return i < 0 ? s : s[..i]; }

    internal static AdapterKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => AdapterKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Mobile,
        _ => AdapterKind.Other,
    };

    /// <summary>A stable reason code for adapters Winora does not change by default, or null for an ordinary adapter.</summary>
    internal static string? VirtualReason(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return "tunnel";
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp) return "ppp";
        return ReasonFromText(nic.Description + " " + nic.Name);
    }

    internal static string? ReasonFromText(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var (token, reason) in Tokens) if (lower.Contains(token, StringComparison.Ordinal)) return reason;
        return null;
    }

    public DnsWriteResult Write(Guid adapter, DnsSettings target, bool v4, bool v6, DnsSettings? expectedBefore = null)
    {
        if (!v4 && !v6) return DnsWriteResult.Success;
        if (expectedBefore is not null)
        {
            var live = Read(CancellationToken.None).Adapters.FirstOrDefault(a => a.Id == adapter)?.Dns;
            if (live is null || !live.SameAs(expectedBefore)) return DnsWriteResult.Fail(NetworkCode.Stale);
        }
        if (v4)
        {
            var r = DnsInterfaceSettings.Write(adapter, target.V4, false);
            if (!r.Ok) return r;
        }
        if (v6)
        {
            var r = DnsInterfaceSettings.Write(adapter, target.V6, true);
            if (!r.Ok) return r;
        }
        return DnsWriteResult.Success;
    }
}

/// <summary>One lookup of a fixed name through the system resolver, with a short time limit.</summary>
public sealed class SystemConnectivityProbe : IConnectivityProbe
{
    public async Task<bool> CanResolveAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try { return (await Dns.GetHostAddressesAsync(name, cts.Token).ConfigureAwait(false)).Length > 0; }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException) { return false; }
    }
}

/// <summary>The provider list that ships in the app (definitions/network/DnsProviders.v1.json).</summary>
public static class EmbeddedDnsProviders
{
    public const string ResourceName = "Winora.DnsProviders.v1.json";

    public static DnsProviderCatalog Load()
    {
        using var stream = typeof(EmbeddedDnsProviders).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("The DNS provider file is missing from the app.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return DnsProviderCatalog.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// Append-only JSON Lines record of DNS changes: a "begin" line with the full previous settings of every adapter is flushed to disk before a change is made and an "end" line follows it. A begin
/// line without an end line is a change that was interrupted. Nothing is rewritten and nothing leaves the machine.
/// </summary>
public sealed class FileNetworkJournal(Func<string> pathProvider) : INetworkJournal
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();

    public FileNetworkJournal(string path) : this(() => path) { }

    private sealed record Line(string Type, DateTimeOffset At, DnsChange? Change, Guid? Id, NetworkChangeState? State, string? Code, List<NetworkEntryResult>? Results);

    // "written_all": every write of the change was made. The older "written" line was appended after the first write only (see History).
    public void Written(Guid id, DateTimeOffset atUtc) => Append(new("written_all", atUtc, null, id, null, null, null));

    public void Begin(DnsChange change, DateTimeOffset atUtc) => Append(new("begin", atUtc, change, null, null, null, null));

    public void Complete(Guid id, NetworkChangeState state, string code, IReadOnlyList<NetworkEntryResult> results, DateTimeOffset atUtc) => Append(new("end", atUtc, null, id, state, code, results.ToList()));

    private void Append(Line line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
        lock (gate) { JournalFile.Append(pathProvider(), bytes); }
    }

    private (JournalStamp Stamp, IReadOnlyList<DnsChangeRecord> All)? cache;

    public IReadOnlyList<DnsChangeRecord> History(int max)
    {
        if (max <= 0) return [];
        var begins = new Dictionary<Guid, (DnsChange Change, DateTimeOffset At)>();
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
            // A line of the older kind proves only the first write: it counts for a change of one adapter and for nothing more.
            var proven = written.Contains(id) || firstWriteOnly.Contains(id) && change.Entries.Count == 1;
            return ends.TryGetValue(id, out var end) ? new DnsChangeRecord(change, at, end.At, end.State, end.Code ?? "", end.Results ?? [], proven) : new DnsChangeRecord(change, at, null, null, "", [], proven);
        }).ToList();
        lock (gate) { cache = (stamp, all); }
        return all.Take(max).ToList();
    }
}
