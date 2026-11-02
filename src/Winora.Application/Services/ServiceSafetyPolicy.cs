// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;
using Winora.Domain;

namespace Winora.Application;

public enum ServicePolicyClass
{
    /// <summary>A service of another vendor.</summary>
    ThirdParty,
    /// <summary>A Windows service that is on no list. It may be changed; the review shows risk Medium.</summary>
    Windows,
    /// <summary>A service of the reviewed protected list. Locked.</summary>
    Protected,
    /// <summary>A kernel or file system driver. Locked.</summary>
    Driver,
    /// <summary>Starts at boot or with the system. Locked.</summary>
    BootCritical,
    /// <summary>A per-user or special service the manager cannot change in a lasting way. Locked.</summary>
    NotControllable,
}

/// <summary><see cref="Reason"/> is the reason key of a locked service (resource key V2SvReason_{Reason}).</summary>
public sealed record ServicePolicyInfo(ServicePolicyClass Class, string? Reason)
{
    public bool IsLocked => Class is ServicePolicyClass.Protected or ServicePolicyClass.Driver or ServicePolicyClass.BootCritical or ServicePolicyClass.NotControllable;
}

public sealed record ProtectedServiceRule(string Pattern, string Reason, string SourceClass, string Source)
{
    public bool IsPrefix => Pattern.EndsWith('*');
    public bool Matches(string name) => IsPrefix
        ? name.StartsWith(Pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(name, Pattern, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which services Winora may start, stop, restart or reconfigure. The protected list comes from the embedded data file <c>definitions/services/ServicePolicy.v1.json</c>;
/// every group has a reason and a source note. Drivers, services that start at boot or with the system, and per-user or special services are always locked. Everything else is changeable,
/// with risk Medium for a Windows service and Low for a service of another vendor. A service that Windows starts as a protected process (security software) is High whoever made it.
/// </summary>
public sealed class ServiceSafetyPolicy
{
    private const string ResourceName = "Winora.ServicePolicy.v1.json";
    private static readonly Lazy<ServiceSafetyPolicy> DefaultPolicy = new(LoadEmbedded);
    private static readonly Regex WindowsImage = new(@"^\s*""?(?:[a-z]:\\windows\\|%systemroot%\\|%windir%\\|\\systemroot\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "core", "security", "update", "network", "time", "storage", "identity", "licensing", "logging", "remote_access", "virtual_guest", "winora",
    };

    private readonly IReadOnlyList<ProtectedServiceRule> rules;
    private readonly Dictionary<string, ProtectedServiceRule> exact;
    private readonly ProtectedServiceRule[] prefixes;

    public ServiceSafetyPolicy(IReadOnlyList<ProtectedServiceRule> rules)
    {
        this.rules = rules;
        exact = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern == "*" || rule.Pattern.IndexOf('*') != rule.Pattern.Length - 1 && rule.Pattern.Contains('*')) throw new FormatException("Bad service pattern: " + rule.Pattern);
            if (!Reasons.Contains(rule.Reason)) throw new FormatException("Unknown reason for " + rule.Pattern);
            if (rule.SourceClass is not ("observed" or "microsoft") || string.IsNullOrWhiteSpace(rule.Source)) throw new FormatException("Missing source for " + rule.Pattern);
            if (!rule.IsPrefix && !exact.TryAdd(rule.Pattern, rule)) throw new FormatException("Service is listed twice: " + rule.Pattern);
        }
        prefixes = rules.Where(r => r.IsPrefix).ToArray();
    }

    public static ServiceSafetyPolicy Default => DefaultPolicy.Value;

    public IReadOnlyList<ProtectedServiceRule> Rules => rules;

    public static ServiceSafetyPolicy Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "winora.service-policy/1") throw new FormatException("Unknown service policy format.");
        List<ProtectedServiceRule> rules = [];
        foreach (var group in root.GetProperty("protected").EnumerateArray())
        {
            var reason = group.GetProperty("reason").GetString()!;
            var sourceClass = group.GetProperty("sourceClass").GetString()!;
            var source = group.GetProperty("source").GetString()!;
            foreach (var name in group.GetProperty("names").EnumerateArray()) rules.Add(new(name.GetString()!, reason, sourceClass, source));
        }
        return new ServiceSafetyPolicy(rules);
    }

    private static ServiceSafetyPolicy LoadEmbedded()
    {
        using var stream = typeof(ServiceSafetyPolicy).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("The service policy is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public ProtectedServiceRule? Find(string name)
    {
        if (exact.TryGetValue(name, out var rule)) return rule;
        return prefixes.FirstOrDefault(p => p.Matches(name));
    }

    /// <summary>The verdict for a listed service, from the list data alone (no call to the service manager).</summary>
    public ServicePolicyInfo Classify(ServiceInfo service) => Classify(service.StableId, service.Kind, service.StartMode, service.ImagePath);

    /// <summary>The verdict for a service by its short name and the facts the list or an observation knows.</summary>
    public ServicePolicyInfo Classify(string nameOrStableId, ServiceKind kind, ServiceStartMode start, string? imagePath, bool? imageTrusted = null)
    {
        var name = ShortName(nameOrStableId);
        if (kind is ServiceKind.KernelDriver or ServiceKind.FileSystemDriver) return new(ServicePolicyClass.Driver, "driver");
        if (kind is not (ServiceKind.Win32Own or ServiceKind.Win32Shared)) return new(ServicePolicyClass.NotControllable, "special");
        if (start is ServiceStartMode.Boot or ServiceStartMode.System) return new(ServicePolicyClass.BootCritical, "boot");
        if (Find(name) is { } rule) return new(ServicePolicyClass.Protected, rule.Reason);
        var windows = imageTrusted ?? (imagePath is not null && WindowsImage.IsMatch(imagePath));
        return new(windows ? ServicePolicyClass.Windows : ServicePolicyClass.ThirdParty, null);
    }

    /// <summary>The service name of a stable id: the list gives it as <c>service:</c> plus the lower-case name.</summary>
    public static string ShortName(string stableIdOrName) => stableIdOrName.StartsWith("service:", StringComparison.Ordinal) ? stableIdOrName["service:".Length..] : stableIdOrName;

    /// <summary>The refusal code a locked service gives for every action, or null when the service may be changed.</summary>
    public static string? LockedCode(ServicePolicyInfo info) => info.Class switch
    {
        ServicePolicyClass.Protected => ServiceCode.Protected,
        ServicePolicyClass.Driver => ServiceCode.Driver,
        ServicePolicyClass.BootCritical => ServiceCode.BootCritical,
        ServicePolicyClass.NotControllable => ServiceCode.NotControllable,
        _ => null,
    };

    /// <summary>The risk the review shows: High for a launch-protected service, Medium for a Windows service, Low otherwise.</summary>
    public static RiskLevel Risk(ServicePolicyInfo info, bool launchProtected = false) =>
        launchProtected ? RiskLevel.High : info.Class == ServicePolicyClass.Windows ? RiskLevel.Medium : RiskLevel.Low;
}
