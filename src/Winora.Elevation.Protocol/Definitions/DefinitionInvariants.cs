// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>Identifier grammars of and 7.2, written by hand so the worker needs no regular-expression engine.</summary>
public static class Grammar
{
    private static bool All(string s, int min, int max, Func<char, bool> ok)
    {
        if (s.Length < min || s.Length > max) return false;
        foreach (var c in s) if (!ok(c)) return false;
        return true;
    }

    private static bool Lower(char c) => c is >= 'a' and <= 'z';
    private static bool Digit(char c) => c is >= '0' and <= '9';

    /// <summary><c>machine.[a-z0-9._]{1,56}</c></summary>
    public static bool IsDefinitionId(string s) => s.StartsWith("machine.", StringComparison.Ordinal) && All(s[8..], 1, 56, c => Lower(c) || Digit(c) || c is '.' or '_');
    /// <summary><c>[a-z0-9-]{1,32}</c></summary>
    public static bool IsSetId(string s) => All(s, 1, 32, c => Lower(c) || Digit(c) || c == '-');
    /// <summary><c>[A-Za-z0-9_.-]{1,64}</c></summary>
    public static bool IsServiceName(string s) => All(s, 1, DefinitionLimits.MaxServiceNameChars, c => Lower(c) || c is >= 'A' and <= 'Z' || Digit(c) || c is '_' or '.' or '-');
    /// <summary><c>[a-z0-9_]{1,48}</c></summary>
    public static bool IsChoiceId(string s) => All(s, 1, 48, c => Lower(c) || Digit(c) || c == '_');
    /// <summary><c>[A-Za-z0-9_]{1,64}</c></summary>
    public static bool IsResourceKey(string s) => All(s, 1, 64, c => Lower(c) || c is >= 'A' and <= 'Z' || Digit(c) || c == '_');
    /// <summary><c>SEC-DEF-dddd</c></summary>
    public static bool IsReviewRef(string s) => s.Length == 12 && s.StartsWith("SEC-DEF-", StringComparison.Ordinal) && All(s[8..], 4, 4, Digit);
}

/// <summary>
/// One reviewed exact registry value: a key and ONE value name on it. <see cref="HighOnly"/> entries are valid only in a definition with risk High;
/// <see cref="DeleteOnly"/> entries only in a definition whose choices all delete the value. The id names the reviewed proposal (AL-nn allow entry, DX-nn deny exception).
/// </summary>
public sealed record ExactEntry(string Id, string Key, string ValueName, bool HighOnly = false, bool DeleteOnly = false);

/// <summary>
/// One reviewed value of the driver key of display adapters of one vendor. The key is never part of a definition: code resolves it for each
/// present adapter. <see cref="MinimumRisk"/> is the lowest risk a definition using the entry may declare.
/// </summary>
public sealed record DisplayDriverEntry(string Id, string VendorId, string ValueName, RiskLevel MinimumRisk);

/// <summary>
/// The machine invariants of and 7.3 as code. The worker and the client run the same checks on the verified set; the build runs them before signing.
/// A definition that fails any check makes the whole set unusable. Checks return problems instead of throwing so a test can assert each rule.
/// </summary>
public static class DefinitionInvariants
{
    /// <summary>Allow-prefixes. Each entry is reviewed; adding one needs a documented reason in the pull request.</summary>
    public static readonly IReadOnlyList<string> AllowPrefixes =
    [
        @"SOFTWARE\Policies\Microsoft\Windows\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\",
    ];

    private static ExactEntry[] Names(string id, string key, bool highOnly, params string[] names) => names.Select(n => new ExactEntry(id, key, n, highOnly)).ToArray();

    /// <summary>
    /// Exact allow entries: a key plus one value name each. A value on such a key passes the allow test only when its name is listed; every other
    /// value name on the same key stays outside the allow list. Deny fragments and value-name denials still apply. AL-14 and AL-16 list only the Edge and Chrome value names that ship. Not included yet: AL-17 (Brave list),
    /// AL-31 (autologger sessions), AL-33 and AL-34 (per-interface keys).
    /// </summary>
    public static readonly IReadOnlyList<ExactEntry> AllowedExactValues =
    [
        .. Names("AL-01", @"SYSTEM\CurrentControlSet\Control\FileSystem", false, "LongPathsEnabled", "NtfsDisableLastAccessUpdate", "NtfsDisable8dot3NameCreation"),
        .. Names("AL-02", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", false, "DisableUserPresenceQos", "PowerThrottlingOff"),
        .. Names("AL-03", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", false, "HwSchMode", "TdrDelay", "DisableOverlays"),
        .. Names("AL-04", @"SOFTWARE\Microsoft\Windows\Dwm", false, "OverlayTestMode"),
        .. Names("AL-05", @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", false, "RealTimeIsUniversal"),
        .. Names("AL-06", @"SYSTEM\CurrentControlSet\Control", false, "WaitToKillServiceTimeout", "SvcHostSplitThresholdInKB"),
        .. Names("AL-07", @"SYSTEM\CurrentControlSet\Control\Power", true, "PlatformAoAcOverride"),
        .. Names("AL-08", @"SYSTEM\CurrentControlSet\Control\CrashControl", false, "DisplayParameters", "DisableEmoticon"),
        .. Names("AL-09", @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters", false, "FileSizeLimitInBytes"),
        .. Names("AL-10", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", false, "SystemResponsiveness", "NetworkThrottlingIndex"),
        .. Names("AL-11", @"SYSTEM\CurrentControlSet\Control\PriorityControl", false, "Win32PrioritySeparation"),
        .. Names("AL-12", @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", false, "Disabled", "DontShowUI", "DontSendAdditionalData"),
        .. Names("AL-13", @"SOFTWARE\Policies\Microsoft\Dsh", false, "AllowNewsAndInterests"),
        // AL-14 holds only the value names of the Edge policy rows that ship (Microsoft Edge policy reference); the other Edge and Brave names are not listed.
        .. Names("AL-14", @"SOFTWARE\Policies\Microsoft\Edge", false, "HideFirstRunExperience", "ShowRecommendationsEnabled", "NewTabPageContentEnabled", "EdgeShoppingAssistantEnabled",
            "PersonalizationReportingEnabled", "DiagnosticData", "StartupBoostEnabled", "BackgroundModeEnabled", "SyncDisabled", "BrowserSignin",
            "UserFeedbackAllowed", "AlternateErrorPagesEnabled", "SpotlightExperiencesAndRecommendationsEnabled", "HubsSidebarEnabled", "ShowMicrosoftRewards",
            "NewTabPageHideDefaultTopSites", "ConfigureDoNotTrack"),
        // AL-16 holds only the Chrome policy value names that ship (Chromium policy definitions); every other Chrome policy stays outside the allow list.
        .. Names("AL-16", @"SOFTWARE\Policies\Google\Chrome", false, "MetricsReportingEnabled", "UrlKeyedAnonymizedDataCollectionEnabled", "BackgroundModeEnabled",
            "ShoppingListEnabled", "UserFeedbackAllowed", "PromotionsEnabled", "AlternateErrorPagesEnabled"),
        .. Names("AL-15", @"SOFTWARE\Policies\Microsoft\EdgeUpdate", true, "UpdateDefault", "AutoUpdateCheckPeriodMinutes"),
        .. Names("AL-18", @"SOFTWARE\Policies\Microsoft\FindMyDevice", false, "AllowFindMyDevice"),
        .. Names("AL-19", @"SOFTWARE\Policies\WindowsNotepad", false, "DisableAIFeatures"),
        .. Names("AL-20", @"SOFTWARE\Policies\Microsoft\PushToInstall", false, "DisablePushToInstall"),
        .. Names("AL-21", @"SOFTWARE\Policies\Microsoft\Teams", false, "DisableInstallation"),
        .. Names("AL-22", @"SOFTWARE\Policies\Microsoft\MRT", true, "DontOfferThroughWUAU"),
        .. Names("AL-23", @"SOFTWARE\Policies\Microsoft\WMDRM", false, "DisableOnline"),
        .. Names("AL-24", @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", false, "DisableSmartNameResolution", "EnableMulticast"),
        .. Names("AL-25", @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", false, "ACSettingIndex", "DCSettingIndex"),
        .. Names("AL-26", @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client", true, "RedirectionWarningDialogVersion"),
        .. Names("AL-27", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", false, "DisabledComponents"),
        .. Names("AL-28", @"SYSTEM\CurrentControlSet\Control\BitLocker", false, "PreventDeviceEncryption"),
        .. Names("AL-29", @"SYSTEM\CurrentControlSet\Control\Remote Assistance", false, "fAllowToGetHelp", "fAllowFullControl"),
        .. Names("AL-30", @"SYSTEM\Setup\MoSetup", true, "AllowUpgradesWithUnsupportedTPMOrCPU"),
        .. Names("AL-32", @"SOFTWARE\NVIDIA Corporation\Global\NGXCore", false, "ShowDlssIndicator"),
        .. Names("AL-32", @"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", false, "OptInOrOutPreference"),
        .. Names("AL-32", @"SOFTWARE\NVIDIA Corporation\Global\FTS", false, "EnableRID44231", "EnableRID64640", "EnableRID66610"),
        // AL-36: SMB server and client hardening values on the Microsoft SMB pages (the SMB 1 server switch and the two "always sign" security options).
        .. Names("AL-36", @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", false, "SMB1", "RequireSecuritySignature"),
        .. Names("AL-36", @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", false, "RequireSecuritySignature"),
        // AL-37: Point and Print driver installation limited to administrators (KB5005652).
        .. Names("AL-37", @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint", false, "RestrictDriverInstallationToAdministrators"),
    ];

    /// <summary>PCI vendor ids a display driver definition may name: NVIDIA, AMD and Intel. Virtual and generic adapters have other ids and are never targets.</summary>
    public static readonly IReadOnlyList<string> DisplayVendors = ["10DE", "1002", "8086"];

    /// <summary>
    /// Reviewed display driver values (AL-35). A value on the driver key of an adapter of the named vendor passes the allow test only when vendor and name are listed here. The
    /// vendors do not document these values; both entries are class C and carry a minimum risk that forces the warning text and, for High, the advanced section.
    /// </summary>
    public static readonly IReadOnlyList<DisplayDriverEntry> AllowedDisplayDriverValues =
    [
        new("AL-35", "1002", "EnableUlps", RiskLevel.Medium),
        new("AL-35", "10DE", "DisableDynamicPstate", RiskLevel.High),
    ];

    /// <summary>
    /// Deny exceptions: an exact key and value name that may be used although the key matches a deny fragment. Each one is also an exact allow entry, so it
    /// does not need an allow-prefix. The rest of the key stays denied. DX-08 to DX-10 were approved by the owner on 2026-10-17; DX-11 (Defender policy values) is not added.
    /// </summary>
    public static readonly IReadOnlyList<ExactEntry> DenyExceptions =
    [
        .. Names("DX-01", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", false, "ExcludeWUDriversInQualityUpdate", "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays",
            "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays", "TargetReleaseVersion", "TargetReleaseVersionInfo", "ProductVersion"),
        .. Names("DX-01", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", true, "SetDisableUXWUAccess"),
        .. Names("DX-01", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", false, "NoAutoUpdate", "AUOptions", "AUPowerManagement", "NoAutoRebootWithLoggedOnUsers"),
        .. Names("DX-02", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", false, "IsContinuousInnovationOptedIn"),
        .. Names("DX-02", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", true, "FlightSettingsMaxPauseDays"),
        .. new[] { "BranchReadinessLevel", "DeferFeatureUpdatesPeriodInDays", "DeferQualityUpdatesPeriodInDays" }
            .Select(n => new ExactEntry("DX-02", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", n, DeleteOnly: true)),
        .. Names("DX-03", @"SYSTEM\CurrentControlSet\Control\Session Manager", false, "DisableWpbtExecution"),
        .. Names("DX-04", @"SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager", false, "EnablePeriodicBackup"),
        .. Names("DX-05", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", false, "ClearPageFileAtShutdown"),
        .. Names("DX-05", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true, "FeatureSettingsOverride", "FeatureSettingsOverrideMask"),
        .. Names("DX-06", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", false, "HiberbootEnabled"),
        .. Names("DX-07", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", false, "VerboseStatus"),
        .. Names("DX-07", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true, "ConsentPromptBehaviorAdmin", "PromptOnSecureDesktop"),
        // DX-08: memory integrity and virtualization-based security.
        .. Names("DX-08", @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", true, "Enabled"),
        .. Names("DX-08", @"SYSTEM\CurrentControlSet\Control\DeviceGuard", true, "EnableVirtualizationBasedSecurity"),
        .. Names("DX-08", @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard", true, "EnableVirtualizationBasedSecurity"),
        // DX-09: legacy transport security protocols, ciphers and hashes (High only). Exact named entries; nothing else under SCHANNEL passes.
        .. SchannelEntries(),
        // DX-10: SmartScreen switches (High only). These value names are also in DenyValueNames; an exact High-only entry overrides that list for this value only.
        .. Names("DX-10", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", true, "SmartScreenEnabled"),
        .. Names("DX-10", @"SOFTWARE\Policies\Microsoft\Windows\System", true, "EnableSmartScreen"),
        .. Names("DX-10", @"SOFTWARE\Policies\Microsoft\Windows Defender\SmartScreen", true, "ConfigureAppInstallControlEnabled"),
    ];

    private static IEnumerable<ExactEntry> SchannelEntries()
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\";
        foreach (var protocol in new[] { "SSL 2.0", "SSL 3.0", "TLS 1.0", "TLS 1.1", "DTLS 1.0" })
            foreach (var role in new[] { "Client", "Server" })
                foreach (var name in new[] { "Enabled", "DisabledByDefault" })
                    yield return new ExactEntry("DX-09", root + @"Protocols\" + protocol + "\\" + role, name, HighOnly: true);
        foreach (var cipher in new[] { "RC4 128/128", "RC4 56/128", "RC4 40/128", "DES 56/56", "Triple DES 168", "NULL" })
            yield return new ExactEntry("DX-09", root + @"Ciphers\" + cipher, "Enabled", HighOnly: true);
        yield return new ExactEntry("DX-09", root + @"Hashes\MD5", "Enabled", HighOnly: true);
    }

    /// <summary>
    /// Extension point for deny exceptions the owner has not decided. DX-11 (Defender policy values) stays out: Tamper Protection ignores them.
    /// It is empty on purpose and is NOT part of <see cref="DenyExceptions"/>; an entry added here takes effect only after the owner approves it and it is moved into the
    /// reviewed list in the same change as its decision record.
    /// </summary>
    public static readonly IReadOnlyList<ExactEntry> OwnerPendingDenyExceptions = [];

    /// <summary>Deny fragments, compared case-insensitively on the canonical path with leading and trailing backslashes.</summary>
    public static readonly IReadOnlyList<string> DenyFragments =
    [
        @"\SAM\", @"\SECURITY\", @"\SYSTEM\CurrentControlSet\Control\Lsa\", @"\SecurityProviders\", @"\Session Manager\", @"\Winlogon\",
        @"\Image File Execution Options\", @"\SafeBoot\", @"\Run\", @"\RunOnce\", @"\RunOnceEx\", @"\RunServices\", @"\RunServicesOnce\", @"\AppInit_DLLs\",
        @"\Environment\", @"\Windows Defender", @"\Windows Advanced Threat Protection\", @"\WindowsFirewall\", @"\SharedAccess\",
        @"\CurrentVersion\Policies\System\", @"\Winora\Trust\", @"\Winora\Install\",
        @"\DeviceGuard\", @"\EventLog\", @"\Authentication\", @"\Installer\", @"\CredentialsDelegation\", @"\AppHost\", @"\SmartScreen",
        // Code-injection and logging locations found by the independent read of 2026-10-11: shell extension hooks, update and software-restriction policy, script logging.
        @"\Explorer\ShellExecuteHooks\", @"\Browser Helper Objects\", @"\ShellIconOverlayIdentifiers\", @"\ShellServiceObjectDelayLoad\", @"\Policies\Microsoft\Windows\Safer\",
        @"\Policies\Microsoft\Windows\WindowsUpdate\", @"\WindowsUpdate\", @"\PowerShell\", @"\ScriptBlockLogging\", @"\ModuleLogging\", @"\Transcription\",
        @"\AppCompatFlags\", @"\Policies\Microsoft\Windows\AppLocker\", @"\SrpV2\", @"\Policies\Microsoft\Windows\Appx\",
    ];

    /// <summary>
    /// Services whose start type a definition may never change: security, update, networking, logging, storage, identity and boot-critical services,
    /// compared case-insensitively. Every other service still needs a Microsoft source and the review of 7.4.
    /// </summary>
    public static readonly IReadOnlyList<string> DenyServices =
    [
        "WinDefend", "WdNisSvc", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "MpsSvc", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "DoSvc",
        "TrustedInstaller", "msiserver", "CryptSvc", "KeyIso", "SamSs", "VaultSvc", "Netlogon", "LSM", "RpcSs", "RpcEptMapper", "DcomLaunch", "EventLog", "EventSystem", "Winmgmt",
        "Schedule", "gpsvc", "ProfSvc", "UserManager", "SENS", "Dhcp", "Dnscache", "NlaSvc", "netprofm", "nsi", "LanmanWorkstation", "LanmanServer", "W32Time", "TermService",
        "AppIDSvc", "AppInfo", "BrokerInfrastructure", "StateRepository", "TokenBroker", "WpnService", "CoreMessagingRegistrar", "PlugPlay", "Power", "StorSvc", "vds", "swprv", "VSS",
        "sppsvc", "WlanSvc", "Wcmsvc", "WinHttpAutoProxySvc", "iphlpsvc", "mrxsmb", "bowser", "tdx", "Tcpip", "NetBT", "afd", "ksecdd", "CNG", "VBS", "HvHost", "vmicheartbeat",
        // Additions of (BACKLOG 4.4): browser patching, account and Store licensing, Windows Hello, networking brokers, diagnostics base, Remote Desktop stack, services
        // Microsoft says not to disable, push notifications and virtual machine guest integration.
        "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService", "wlidsvc", "LicenseManager", "ClipSVC", "NgcSvc", "NgcCtnrSvc", "NcbService", "Wecsvc", "WerSvc",
        "DPS", "WdiServiceHost", "WdiSystemHost", "diagsvc", "SessionEnv", "UmRdpService", "SstpSvc", "TapiSrv", "MSiSCSI", "smphost", "SystemEventsBroker", "TimeBrokerSvc",
        "tiledatamodelsvc", "Themes", "WpnUserService", "vmicguestinterface", "vmickvpexchange", "vmicrdv", "vmicshutdown", "vmictimesync", "vmicvmsession", "vmicvss",
        "VMTools", "VGAuthService", "vm3dservice", "vmvss",
    ];

    /// <summary>
    /// The service allow list SA-01: the only services a definition may name. Every name has evidence of class A or B in research/ and a verdict other than
    /// EXCLUDE. Left out on purpose: names with class C evidence only (RetailDemo, WMPNetworkSvc, WSAIFabricSvc, WebClient, SysMain, WSearch, CDPSvc, uhssvc), names whose research
    /// verdict is EXCLUDE (tzautoupdate, SharedAccess), and the per-user templates OneSyncSvc and BcastDVRUserService (template handling is not built). The names must not be on
    /// <see cref="DenyServices"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ServiceAllowList =
    [
        "MapsBroker", "lltdsvc", "AxInstSV", "DiagTrack", "lfsvc", "XblGameSave", "XblAuthManager", "SSDPSRV", "upnphost", "CscService", "wisvc", "PhoneSvc", "bthserv",
        "PcaSvc", "RemoteRegistry", "Spooler",
    ];

    /// <summary>The one service allowed to run from a non-Microsoft image (SA-02, BACKLOG 4.4). Not part of the first allow list: no definition names it yet.</summary>
    public static readonly IReadOnlyList<string> NonMicrosoftImageExceptions = [];

    /// <summary>Value names that weaken a protection even under an allowed prefix.</summary>
    public static readonly IReadOnlyList<string> DenyValueNames = ["EnableSmartScreen", "SmartScreenEnabled", "ShellSmartScreenLevel", "EnableWebContentEvaluation"];

    /// <summary>Hosts of Microsoft documentation (class A).</summary>
    public static readonly IReadOnlyList<string> MicrosoftHosts = ["learn.microsoft.com", "support.microsoft.com", "www.microsoft.com"];

    /// <summary>Hosts accepted for the extra sources of classes B and C: the Microsoft hosts, project hosting, the three hardware vendors and four reference sites. No other host.</summary>
    public static readonly IReadOnlyList<string> CommunityHosts =
    [
        "learn.microsoft.com", "support.microsoft.com", "www.microsoft.com", "devblogs.microsoft.com", "github.com",
        "nvidia.com", "www.nvidia.com", "developer.nvidia.com", "amd.com", "www.amd.com", "intel.com", "www.intel.com",
        // Reference pages that document one registry value each and have no equivalent on the hosts above (BACKLOG 4.1): NTFS flag table (winaero.com), Group Policy value
        // reference (gp.kapilarya.com), shortcut overlay how-to (windowsreport.com), hardware clock in UTC (wiki.archlinux.org).
        "winaero.com", "gp.kapilarya.com", "windowsreport.com", "wiki.archlinux.org",
        // Graphics driver tweaks (display driver values, MPO): the AMD community forum thread on ULPS, the Blur Busters threads on the NVIDIA fixed performance state and the NVIDIA support article that
        // publishes the MPO switch. Owner approval pending.
        "community.amd.com", "forums.blurbusters.com", "nvidia.custhelp.com",
    ];

    private static readonly string[] Editions = ["Home", "Pro", "Enterprise", "Education"];

    /// <summary>Canonical form of a registry path for the allow and deny tests: separators single, <c>ControlSet00x</c> folded to <c>CurrentControlSet</c>.</summary>
    public static string Canonicalize(string subKey)
    {
        var parts = subKey.Split('\\', StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 13 && parts[i].StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase) && parts[i][10..].All(char.IsAsciiDigit)) parts[i] = "CurrentControlSet";
        return string.Join('\\', parts);
    }

    public static IReadOnlyList<string> Validate(DefinitionSet set)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in set.Revoked) if (!Grammar.IsDefinitionId(r.Id)) problems.Add($"revoked {r.Id}: id out of grammar");
        foreach (var d in set.Definitions)
        {
            if (!ids.Add(d.Id)) problems.Add($"{d.Id}: duplicate id");
            if (set.RevocationOf(d.Id, d.Version) is not null) problems.Add($"{d.Id}: version {d.Version} is below its own revocation");
            problems.AddRange(Validate(d).Select(p => $"{d.Id}: {p}"));
        }
        return problems;
    }

    /// <summary>
    /// Checks that hold across the whole shipped set: every review reference is used once and no two definitions own the same target (same key and value name, or same service,
    /// with bit fields compared by mask). Two definitions that write one value would fight over it.
    /// </summary>
    public static IReadOnlyList<string> ValidateUniqueness(DefinitionSet set)
    {
        var problems = new List<string>();
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in set.Definitions)
        {
            if (d.ReviewRef.Length > 0 && !refs.TryAdd(d.ReviewRef, d.Id)) problems.Add($"{d.Id}: review reference {d.ReviewRef} is already used by {refs[d.ReviewRef]}");
            var target = d.Registry is { } r
                ? $"reg|{d.Registry.View}|{Canonicalize(r.SubKey)}|{r.ValueName}|{r.Bits?.Mask}"
                : d.DisplayDriver is { } g ? $"drv|{g.VendorId}|{g.ValueName}"
                : $"svc|{d.Service?.ServiceName}";
            if (!targets.TryAdd(target, d.Id)) problems.Add($"{d.Id}: writes the same target as {targets[target]}");
        }
        return problems;
    }

    public static IReadOnlyList<string> Validate(MachineSettingDefinition d)
    {
        var p = new List<string>();
        if (!Grammar.IsDefinitionId(d.Id)) p.Add("id out of grammar");
        if (d.Version < 1) p.Add("version below 1");
        if (!Grammar.IsResourceKey(d.TitleKey) || !Grammar.IsResourceKey(d.DescriptionKey)) p.Add("title and description must be resource keys");
        if (d.MinBuild < DefinitionLimits.MinSupportedBuild) p.Add($"minBuild below {DefinitionLimits.MinSupportedBuild}");
        if (d.MaxBuild is { } max && max < d.MinBuild) p.Add("maxBuild below minBuild");
        // The review reference is optional; when present it must follow the grammar.
        if (d.ReviewRef.Length > 0 && !Grammar.IsReviewRef(d.ReviewRef)) p.Add("reviewRef out of grammar");
        p.AddRange(ValidateSources(d));
        p.AddRange(ValidateEditions(d.Editions));
        // A definition whose target lives under a Policies key must declare the policy channel (the value itself); other targets may have no policy channel.
        if (d.ManagedBy.Count == 0 && IsPolicyKey(d.Registry?.SubKey)) p.Add("a target under a Policies key must name its policy channel in managedBy");
        foreach (var m in d.ManagedBy)
            if (m.Location.Length is 0 or > DefinitionLimits.MaxKeyPathChars || !IsAscii(m.Location) || m.Location.Contains('%') || m.Location.Contains('*'))
                p.Add("managedBy location out of grammar");
        foreach (var v in d.RestoreCompatible) if (v >= d.Version) p.Add("restoreCompatible must list earlier versions only");
        if (d.RestoreCompatible.Distinct().Count() != d.RestoreCompatible.Count) p.Add("restoreCompatible has duplicates");

        // One target, matching the kind.
        if (new object?[] { d.Registry, d.Service, d.DisplayDriver }.Count(t => t is not null) != 1) p.Add("exactly one of registry, service and display driver target");
        else if ((d.TargetKind == MachineTargetKind.RegistryValue) != (d.Registry is not null) || (d.TargetKind == MachineTargetKind.DisplayDriverValue) != (d.DisplayDriver is not null)
            || (d.TargetKind == MachineTargetKind.ServiceStartType) != (d.Service is not null)) p.Add("target kind does not match the target");
        if (d.DisplayDriver is not null) p.AddRange(ValidateDisplayDriver(d, d.DisplayDriver));
        if (d.TargetKind == MachineTargetKind.Selftest) p.Add("self-test targets are never part of a definition set");
        if (d.Registry is not null) p.AddRange(ValidateRegistry(d.Registry, d.Risk));
        if (d.Registry is not null && FindException(d.Registry, DenyExceptions) is { DeleteOnly: true } && d.Choices.Any(c => c.Value is null || c.Value.Kind != MachineValueKind.Absent))
            p.Add("a delete-only exception allows only choices that delete the value");
        if (d.Registry?.Bits is { } bits) p.AddRange(ValidateBits(d, bits));
        if (d.Service is not null && !Grammar.IsServiceName(d.Service.ServiceName)) p.Add("service name out of grammar");
        else if (d.Service is not null && DenyServices.Contains(d.Service.ServiceName, StringComparer.OrdinalIgnoreCase)) p.Add($"service {d.Service.ServiceName} is on the deny list");
        else if (d.Service is not null && !ServiceAllowList.Contains(d.Service.ServiceName, StringComparer.OrdinalIgnoreCase)) p.Add($"service {d.Service.ServiceName} is not on the service allow list");

        // Choices and the restore domain.
        if (d.Choices.Count == 0) p.Add("no choices");
        if (d.Choices.Select(c => c.Id).Distinct().Count() != d.Choices.Count) p.Add("duplicate choice ids");
        foreach (var c in d.Choices)
        {
            if (!Grammar.IsChoiceId(c.Id) || !Grammar.IsResourceKey(c.LabelKey)) p.Add($"choice {c.Id}: id or label out of grammar");
            if (d.TargetKind is MachineTargetKind.RegistryValue or MachineTargetKind.DisplayDriverValue && (c.Value is null || c.Start is not null)) p.Add($"choice {c.Id}: a registry choice needs a value and no start type");
            if (d.TargetKind == MachineTargetKind.ServiceStartType && (c.Start is null || c.Value is not null)) p.Add($"choice {c.Id}: a service choice needs a start type and no value");
            if (c.Value is not null && !d.RestoreDomain.Contains(c.Value)) p.Add($"choice {c.Id}: value is outside the restore domain");
            if (c.Value is { Kind: MachineValueKind.String } text && text.Text!.Contains('%') && d.Registry?.AllowPercentInText != true) p.Add($"choice {c.Id}: text contains a percent sign and the target does not allow it");
        }
        if (d.Registry?.AllowPercentInText == true && d.Registry.Bits is null && !d.RestoreDomain.Any(v => v.Kind == MachineValueKind.String)) p.Add("allowPercent needs a text value in the restore domain");
        if (d.TargetKind == MachineTargetKind.ServiceStartType && d.RestoreDomain.Count != 0) p.Add("a service target has no value restore domain");
        if (d.TargetKind is MachineTargetKind.RegistryValue or MachineTargetKind.DisplayDriverValue && d.RestoreDomain.Count == 0) p.Add("restore domain is empty");
        if (d.RestoreDomain.Distinct().Count() != d.RestoreDomain.Count) p.Add("restore domain has duplicates");
        if (d.Choices.All(c => c.Id != d.DefaultChoiceId)) p.Add("default choice is not a choice");
        if (d.RecommendedChoiceId is not null && d.Choices.All(c => c.Id != d.RecommendedChoiceId)) p.Add("recommended choice is not a choice");
        foreach (var v in d.RestoreDomain.Where(v => v.Kind == MachineValueKind.String))
        {
            if (v.Text!.Length > DefinitionLimits.MaxStringValueChars) p.Add("restore string value too long");
            if (v.Text.Contains('%') && d.Registry?.AllowPercentInText != true) p.Add("restore text contains a percent sign and the target does not allow it");
        }
        return p;
    }

    /// <summary>
    /// The rules for a display driver target: a reviewed vendor, a reviewed value on that vendor, a risk not below the entry's minimum, numbers or text only (no bit field, no percent
    /// sign) and no policy channel, because the target is not a policy value.
    /// </summary>
    private static IEnumerable<string> ValidateDisplayDriver(MachineSettingDefinition d, MachineDisplayDriverTarget t)
    {
        if (!DisplayVendors.Contains(t.VendorId, StringComparer.Ordinal)) yield return "display vendor id is not on the reviewed list";
        if (t.ValueName.Length is 0 or > DefinitionLimits.MaxValueNameChars || !IsAscii(t.ValueName) || t.ValueName.IndexOfAny(['\\', '*', '?', '%', '/']) >= 0) yield return "value name out of grammar";
        var entry = AllowedDisplayDriverValues.FirstOrDefault(e => e.VendorId == t.VendorId && string.Equals(e.ValueName, t.ValueName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) yield return $"{t.ValueName} is not on the display driver allow list for vendor {t.VendorId}";
        else if (d.Risk < entry.MinimumRisk) yield return $"{entry.Id}: this display driver value needs risk {entry.MinimumRisk} or higher";
        if (d.ManagedBy.Count != 0) yield return "a display driver target has no policy channel";
        if (d.RestoreDomain.Any(v => v.Kind is MachineValueKind.QWord)) yield return "a display driver value is a number or text";
    }

    private static bool IsPolicyKey(string? subKey) => subKey is not null && ("\\" + subKey + "\\").Contains("\\Policies\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bit-field rules: a non-zero mask, DWORD choices and restore values that touch only the mask, and no choice that deletes the value.</summary>
    private static IEnumerable<string> ValidateBits(MachineSettingDefinition d, MachineBitField bits)
    {
        if (bits.Mask == 0) yield return "bit mask is zero";
        foreach (var v in d.Choices.Select(c => c.Value).Concat(d.RestoreDomain))
        {
            if (v is null) continue;
            if (v.Kind != MachineValueKind.DWord) yield return "bit-field values must be numbers (the owned bits)";
            else if ((v.Number!.Value & ~(ulong)bits.Mask) != 0) yield return "a bit-field value sets bits outside the mask";
        }
    }

    private static IEnumerable<string> ValidateSources(MachineSettingDefinition d)
    {
        if (d.SourceRefs.Count == 0 && d.SourceClass == SourceClass.A) yield return "no source reference";
        foreach (var s in d.SourceRefs) if (!IsDocumentedSource(s)) yield return $"source reference is not an https Microsoft documentation address: {s}";
        foreach (var s in d.CommunitySources) if (!IsCommunitySource(s)) yield return $"community source is not a numbered reference label or an https address on the allowed host list: {s}";
        if (d.CommunitySources.Count != d.CommunitySources.Distinct().Count()) yield return "community sources have duplicates";
        if (d.SourceClass != SourceClass.A && d.CommunitySources.Count == 0) yield return "a class B or C definition needs at least one community source";
        if (d.SourceClass == SourceClass.A && d.CommunitySources.Count != 0) yield return "a class A definition is documented by Microsoft only; use class B or C for other sources";
    }

    private static IEnumerable<string> ValidateEditions(IReadOnlyList<string> editions)
    {
        if (editions.Count > Editions.Length || editions.Distinct(StringComparer.Ordinal).Count() != editions.Count) yield return "editions has too many or duplicate entries";
        foreach (var e in editions) if (!Editions.Contains(e, StringComparer.Ordinal)) yield return $"unknown edition {e}";
    }

    /// <summary>The exact entry of <paramref name="list"/> that names this key and value (case-insensitive, aliases folded), or null.</summary>
    public static ExactEntry? FindException(MachineRegistryTarget t, IReadOnlyList<ExactEntry> list)
    {
        var key = Canonicalize(t.SubKey);
        return list.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(e.ValueName, t.ValueName, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ValidateRegistry(MachineRegistryTarget t) => ValidateRegistry(t, RiskLevel.Low);

    /// <summary>The path policy of for one target. <paramref name="risk"/> decides whether an entry marked High-only is valid.</summary>
    public static IReadOnlyList<string> ValidateRegistry(MachineRegistryTarget t, RiskLevel risk)
    {
        var p = new List<string>();
        var key = t.SubKey;
        if (key.Length is 0 or > DefinitionLimits.MaxKeyPathChars) p.Add("key path length out of range");
        if (!IsAscii(key)) p.Add("key path is not printable ASCII");
        if (key.IndexOfAny(['*', '?', '%']) >= 0) p.Add("key path contains a wildcard or an environment reference");
        if (key.Contains("..", StringComparison.Ordinal)) p.Add("key path contains ..");
        if (key.StartsWith('\\') || key.EndsWith('\\') || key.Contains("\\\\", StringComparison.Ordinal)) p.Add("key path has an empty segment");
        if (t.ValueName.Length is 0 or > DefinitionLimits.MaxValueNameChars || !IsAscii(t.ValueName) || t.ValueName.IndexOfAny(['\\', '*', '?', '%']) >= 0)
            p.Add("value name out of grammar");
        var exception = FindException(t, DenyExceptions);
        if (DenyValueNames.Contains(t.ValueName, StringComparer.OrdinalIgnoreCase) && exception is not { HighOnly: true }) p.Add("value name weakens a protection");
        if (p.Count > 0) return p;

        var canonical = "\\" + Canonicalize(key) + "\\";
        var exact = exception ?? FindException(t, AllowedExactValues);
        if (exact is { HighOnly: true } && risk != RiskLevel.High) p.Add($"{exact.Id}: this exact value is valid only in a definition with risk High");
        if (exception is not null) return p;   // a deny exception is an exact allow entry that overrides the deny fragments for this value only
        if (exact is null)
        {
            if (AllowPrefixes.Any(a => string.Equals(a.TrimEnd('\\'), Canonicalize(key), StringComparison.OrdinalIgnoreCase)))
                p.Add("a value directly under an allow-prefix is not admissible; name a documented subkey");
            if (!AllowPrefixes.Any(a => canonical.StartsWith("\\" + a, StringComparison.OrdinalIgnoreCase))) p.Add("key path is outside the allow-prefix list");
        }
        foreach (var fragment in DenyFragments)
            if (canonical.Contains(fragment, StringComparison.OrdinalIgnoreCase)) p.Add($"key path matches the deny list ({fragment.Trim('\\')})");
        return p;
    }

    private static bool IsReferenceLabel(string s)
    {
        const string prefix = "community source ";
        if (!s.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var number = s.AsSpan(prefix.Length);
        return number.Length is 1 or 2 && number[0] is >= '1' and <= '9' && (number.Length == 1 || number[1] is >= '0' and <= '9');
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    private static bool IsDocumentedSource(string s)
    {
        if (s.Length is 0 or > 256 || !IsAscii(s) || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && MicrosoftHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// A numbered neutral label ("community source 3"): the source is described in the sources document without a link or a name. Or an https address on the fixed host list of
    /// <see cref="CommunityHosts"/>, with no user information and no explicit port.
    /// </summary>
    public static bool IsCommunitySource(string s)
    {
        if (IsReferenceLabel(s)) return true;
        if (s.Length is 0 or > 256 || !IsAscii(s) || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && CommunityHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(uri.UserInfo);
    }
}
