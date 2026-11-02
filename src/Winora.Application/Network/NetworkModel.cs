// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winora.Application;

public enum AdapterKind { Ethernet, WiFi, Mobile, Other }

/// <summary>DNS over HTTPS for one server: off, on with a fall back to plain DNS, or on without a fall back.</summary>
public enum DohMode { Off, OnWithFallback, OnWithoutFallback }

/// <summary>The DNS_DOH_SERVER_SETTINGS flag bits of the Windows API (netioapi.h).</summary>
public static class DohFlag
{
    public const ulong Auto = 0x1;
    public const ulong Enable = 0x2;
    public const ulong FallbackToUdp = 0x4;
    public const ulong AutoUpgrade = 0x8;

    public static ulong For(DohMode mode) => mode switch
    {
        DohMode.OnWithFallback => Enable | FallbackToUdp,
        DohMode.OnWithoutFallback => Enable,
        _ => 0,
    };
}

/// <summary>One DNS server of an adapter with its DoH setting. <see cref="DohFlags"/> keeps the raw bits so an undo can write back exactly what was there.</summary>
public sealed record DnsServer(string Address, string? DohTemplate, ulong DohFlags)
{
    public DohMode Mode => (DohFlags & (DohFlag.Enable | DohFlag.Auto)) == 0 ? DohMode.Off : (DohFlags & DohFlag.FallbackToUdp) != 0 ? DohMode.OnWithFallback : DohMode.OnWithoutFallback;

    private static bool Same(string? a, string? b) => string.Equals(string.IsNullOrEmpty(a) ? null : a, string.IsNullOrEmpty(b) ? null : b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact match, including the raw flag bits. Used for compare-and-set.</summary>
    public bool SameAs(DnsServer other) => Same(Address, other.Address) && Same(DohTemplate, other.DohTemplate) && DohFlags == other.DohFlags;

    /// <summary>Same server and the same effect. Used for the read-back, because Windows may set bits that Winora did not ask for.</summary>
    public bool EquivalentTo(DnsServer other) => Same(Address, other.Address) && Mode == other.Mode && (Mode == DohMode.Off || Same(DohTemplate, other.DohTemplate));
}

/// <summary>The static servers of one IP family on one adapter. No servers means the adapter takes its DNS servers from the network (DHCP or router advertisements).</summary>
public sealed record DnsFamily(IReadOnlyList<DnsServer> Servers)
{
    public static DnsFamily Automatic { get; } = new([]);
    public bool IsAutomatic => Servers.Count == 0;

    public bool SameAs(DnsFamily other) => Servers.Count == other.Servers.Count && Servers.Zip(other.Servers).All(p => p.First.SameAs(p.Second));
    public bool EquivalentTo(DnsFamily other) => Servers.Count == other.Servers.Count && Servers.Zip(other.Servers).All(p => p.First.EquivalentTo(p.Second));
}

public sealed record DnsSettings(DnsFamily V4, DnsFamily V6)
{
    public static DnsSettings Automatic { get; } = new(DnsFamily.Automatic, DnsFamily.Automatic);
    public bool SameAs(DnsSettings other) => V4.SameAs(other.V4) && V6.SameAs(other.V6);
    public bool EquivalentTo(DnsSettings other) => V4.EquivalentTo(other.V4) && V6.EquivalentTo(other.V6);
    public IEnumerable<DnsServer> All => V4.Servers.Concat(V6.Servers);
}

public enum EncryptionState { FromNetwork, NotEncrypted, Partial, EncryptedWithFallback, Encrypted }

public static class DnsEncryption
{
    /// <summary>Whether the DNS servers set on the adapter use DoH. Servers that come from the network have no DoH setting that Winora can see.</summary>
    public static EncryptionState Summarize(DnsSettings settings)
    {
        var servers = settings.All.ToList();
        if (servers.Count == 0) return EncryptionState.FromNetwork;
        var on = servers.Count(s => s.Mode != DohMode.Off);
        if (on == 0) return EncryptionState.NotEncrypted;
        if (on < servers.Count) return EncryptionState.Partial;
        return servers.Any(s => s.Mode == DohMode.OnWithFallback) ? EncryptionState.EncryptedWithFallback : EncryptionState.Encrypted;
    }
}

/// <summary>An adapter that is up. <see cref="VirtualReason"/> is set for adapters Winora does not change unless the user adds them on purpose (virtual, VPN, tunnel).</summary>
public sealed record NetworkAdapter(Guid Id, string Name, string Description, AdapterKind Kind, string? VirtualReason, IReadOnlyList<string> Ipv4Addresses, IReadOnlyList<string> Ipv6Addresses,
    IReadOnlyList<string> EffectiveDns, bool Dhcpv4, DnsSettings? Dns, string? DnsReadCode)
{
    public bool IsVirtual => VirtualReason is not null;
    public bool HasIpv6 => Ipv6Addresses.Count > 0;
}

public enum DnsSupport { Available, Unavailable }

public sealed record NetworkSnapshot(IReadOnlyList<NetworkAdapter> Adapters, DnsSupport Support)
{
    public static NetworkSnapshot Unavailable { get; } = new([], DnsSupport.Unavailable);
}

public sealed record DnsWriteResult(bool Ok, string Code)
{
    public static DnsWriteResult Success { get; } = new(true, NetworkCode.Ok);
    public static DnsWriteResult Fail(string code) => new(false, code);
}

/// <summary>Reads the adapters and their DNS settings and writes the DNS settings of one adapter. The only writer lives in one reviewed Windows file.</summary>
public interface IDnsAdapters
{
    NetworkSnapshot Read(CancellationToken cancellationToken);
    /// <summary>Sets the families named by <paramref name="v4"/> and <paramref name="v6"/> to the values in <paramref name="target"/>. A family that is not named is not touched.</summary>
    /// <param name="expectedBefore">The settings the adapter had when the change was planned; another state at the moment of the write is refused as stale.</param>
    DnsWriteResult Write(Guid adapter, DnsSettings target, bool v4, bool v6, DnsSettings? expectedBefore = null);
}

/// <summary>One DNS lookup of a fixed name through the system resolver.</summary>
public interface IConnectivityProbe
{
    Task<bool> CanResolveAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Stable codes of network changes.</summary>
public static class NetworkCode
{
    public const string Ok = "ok";
    public const string NeedsAdmin = "network.needs_admin";
    public const string Unavailable = "network.unavailable";
    public const string UnknownProvider = "network.unknown_provider";
    public const string DohUnavailable = "network.doh_unavailable";
    public const string NoAdapter = "network.no_adapter";
    public const string UnknownAdapter = "network.unknown_adapter";
    public const string VirtualAdapter = "network.virtual_adapter";
    public const string ReadFailed = "network.read_failed";
    public const string InvalidAddress = "network.invalid_address";
    public const string NoServers = "network.no_servers";
    public const string TooManyServers = "network.too_many_servers";
    public const string AlreadySet = "network.already_set";
    public const string Stale = "network.stale";
    /// <summary>One family was written and the other failed: the written one stays and an undo can restore it.</summary>
    public const string PartialWrite = "network.partial_write";
    public const string Conflict = "network.conflict";
    public const string CannotRevert = "network.cannot_revert";
    public const string VerifyFailed = "network.verify_failed";
    public const string AccessDenied = "network.access_denied";
    public const string Unsupported = "network.unsupported";
    public const string Failed = "network.failed";
    public const string JournalFailed = "network.journal_failed";
    public const string Reconciled = "network.reconciled";
    public const string InterruptedUnchanged = "network.interrupted_unchanged";
    public const string Dismissed = "network.dismissed";
    public const string ResolvedRestored = "network.resolved_restored";
    public const string NotUnconfirmed = "network.not_unconfirmed";
    public const string NoteIpv6Unchanged = "network.note.ipv6_unchanged";
    public const string NoteDohOff = "network.note.doh_off";
}

/// <summary>A public DNS provider with fixed addresses, from the reviewed data file.</summary>
public sealed record DnsProvider(string Id, string Name, IReadOnlyList<string> Ipv4, IReadOnlyList<string> Ipv6, string? DohTemplate, IReadOnlyList<string> Sources, string Verified)
{
    public bool SupportsDoh => DohTemplate is not null;
}

public static class DnsAddress
{
    public const int MaxPerFamily = 4;
    private static readonly Regex V4 = new(@"^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$", RegexOptions.CultureInvariant);

    /// <summary>Accepts only an IPv4 dotted quad or an IPv6 literal that is a usable unicast server address. No names, ports, scopes, masks or shortened IPv4 forms.</summary>
    public static bool TryNormalize(string? text, out string normalized, out AddressFamily family)
    {
        normalized = ""; family = AddressFamily.Unspecified;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 45 || s.Any(c => c is '%' or '/' or '[' or ']' or ' ' or ',' or ';')) return false;
        if (!IPAddress.TryParse(s, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!V4.IsMatch(s)) return false;
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 127 || b[0] >= 224) return false;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6 || IPAddress.IPv6Any.Equals(ip) || IPAddress.IPv6Loopback.Equals(ip) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
        }
        else return false;
        normalized = ip.ToString(); family = ip.AddressFamily;
        return true;
    }

    /// <summary>Splits a user entry at commas, semicolons and spaces and validates every part.</summary>
    public static bool TryParseList(string? text, out List<string> v4, out List<string> v6, out string? errorCode)
    {
        v4 = []; v6 = []; errorCode = null;
        foreach (var part in (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryNormalize(part, out var n, out var family)) { errorCode = NetworkCode.InvalidAddress; return false; }
            var list = family == AddressFamily.InterNetwork ? v4 : v6;
            if (!list.Contains(n)) list.Add(n);
        }
        if (v4.Count > MaxPerFamily || v6.Count > MaxPerFamily) { errorCode = NetworkCode.TooManyServers; return false; }
        return true;
    }
}

/// <summary>The provider list that ships in the app (definitions/network/DnsProviders.v1.json). A file that fails the strict parser is an error, never a partial list.</summary>
public sealed class DnsProviderCatalog
{
    public const string AutomaticId = "automatic";
    public const string CustomId = "custom";
    private const int MaxProviders = 64;
    private static readonly Regex Id = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    public IReadOnlyList<DnsProvider> Providers { get; }

    public DnsProviderCatalog(IReadOnlyList<DnsProvider> providers) { Providers = providers; }

    public DnsProvider? Find(string id) => Providers.FirstOrDefault(p => p.Id == id);

    public static DnsProviderCatalog Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException e) { throw new FormatException("The DNS provider file is not valid JSON.", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) throw new FormatException("Unsupported DNS provider file version.");
            if (!root.TryGetProperty("providers", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is 0 or > MaxProviders) throw new FormatException("The provider list is missing or too long.");
            var list = new List<DnsProvider>();
            foreach (var e in arr.EnumerateArray())
            {
                var id = Str(e, "id");
                if (!Id.IsMatch(id) || id is AutomaticId or CustomId || list.Any(p => p.Id == id)) throw new FormatException($"Invalid or duplicate provider id '{id}'.");
                var name = Str(e, "name");
                if (name.Length is 0 or > 80) throw new FormatException($"Invalid provider name for '{id}'.");
                var v4 = Addrs(e, "ipv4", System.Net.Sockets.AddressFamily.InterNetwork);
                var v6 = Addrs(e, "ipv6", System.Net.Sockets.AddressFamily.InterNetworkV6);
                if (v4.Count == 0) throw new FormatException($"Provider '{id}' has no IPv4 server.");
                string? template = null;
                if (e.TryGetProperty("dohTemplate", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    template = t.GetString();
                    if (!Uri.TryCreate(template, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.HostNameType != UriHostNameType.Dns || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || template!.Length > 200)
                        throw new FormatException($"Invalid DoH template for '{id}'.");
                }
                var sources = new List<string>();
                if (!e.TryGetProperty("sources", out var s) || s.ValueKind != JsonValueKind.Array || s.GetArrayLength() == 0) throw new FormatException($"Provider '{id}' has no source.");
                foreach (var item in s.EnumerateArray())
                {
                    var url = item.GetString();
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") throw new FormatException($"Invalid source for '{id}'.");
                    sources.Add(url!);
                }
                var verified = Str(e, "verified");
                if (!DateOnly.TryParseExact(verified, "yyyy-MM-dd", out _)) throw new FormatException($"Invalid verified date for '{id}'.");
                list.Add(new(id, name, v4, v6, template, sources, verified));
            }
            return new(list);
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : throw new FormatException($"Missing '{name}'.");

    private static List<string> Addrs(JsonElement e, string name, AddressFamily family)
    {
        var result = new List<string>();
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) throw new FormatException($"Missing '{name}'.");
        foreach (var item in p.EnumerateArray())
        {
            if (!DnsAddress.TryNormalize(item.GetString(), out var n, out var f) || f != family || n != item.GetString()) throw new FormatException($"Invalid address in '{name}'.");
            result.Add(n);
        }
        if (result.Count > DnsAddress.MaxPerFamily) throw new FormatException($"Too many addresses in '{name}'.");
        return result;
    }
}
