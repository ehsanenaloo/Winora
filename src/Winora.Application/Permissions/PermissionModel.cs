// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum ConsentState { Absent, Allow, Deny, Other }

/// <summary>Which consent value a target is. Global is the "Value" of the capability key, Desktop is the "Value" of its NonPackaged key (desktop programs), App is the "Value" of one package's key.</summary>
public enum ConsentScope { Global, Desktop, App }

public enum PermissionChoice { Allow, Deny }

/// <summary>A group of changes that is made, shown in the history and undone as one.</summary>
/// <summary>What a change was. <see cref="Staged"/>: several values picked on the page, each with its own choice, applied after one review.</summary>
public enum PermissionGroup { Single, CameraMicrophone, Lockdown, Staged }

/// <summary><see cref="Partial"/>: the change failed and at least one value it wrote could not be put back; the results name those values and they can still be undone.</summary>
/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum PermissionChangeState { Applied, Failed, Refused, Partial, Dismissed }

/// <summary>A consent value of the current user. <see cref="AppKey"/> is set for <see cref="ConsentScope.App"/> only.</summary>
public sealed record ConsentTarget(string Capability, ConsentScope Scope, string? AppKey = null);

/// <summary>A capability of the Windows consent store. The display name and the text come from the string table (<c>Perm_{Id}_Name</c>, <c>Perm_{Id}_Desc</c>). <see cref="PolicyValue"/> is the name of the DWORD that the matching Group Policy uses; it is only read.</summary>
public sealed record PermissionCapability(string Id, string Section, string? PolicyValue);

public static class PermissionSections
{
    public const string Devices = "Devices", Personal = "Personal", System = "System";
}

/// <summary>
/// The capability names Winora knows, taken from the consent store that the Settings app writes (Privacy and security, App permissions). Nothing else is ever read or written by name.
/// Policy value names are those of the Policy CSP "Privacy" page (Microsoft documentation); the consent store itself is not documented by Microsoft.
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionCapability> All { get; } =
    [
        new("webcam", PermissionSections.Devices, "LetAppsAccessCamera"),
        new("microphone", PermissionSections.Devices, "LetAppsAccessMicrophone"),
        new("location", PermissionSections.Devices, "LetAppsAccessLocation"),
        new("gazeInput", PermissionSections.Devices, null),
        new("radios", PermissionSections.Devices, "LetAppsAccessRadios"),
        new("bluetoothSync", PermissionSections.Devices, "LetAppsSyncWithDevices"),
        new("contacts", PermissionSections.Personal, "LetAppsAccessContacts"),
        new("appointments", PermissionSections.Personal, "LetAppsAccessCalendar"),
        new("phoneCall", PermissionSections.Personal, "LetAppsAccessPhone"),
        new("phoneCallHistory", PermissionSections.Personal, "LetAppsAccessCallHistory"),
        new("email", PermissionSections.Personal, "LetAppsAccessEmail"),
        new("chat", PermissionSections.Personal, "LetAppsAccessMessaging"),
        new("userDataTasks", PermissionSections.Personal, "LetAppsAccessTasks"),
        new("userAccountInformation", PermissionSections.Personal, "LetAppsAccessAccountInfo"),
        new("userNotificationListener", PermissionSections.Personal, "LetAppsAccessNotifications"),
        new("documentsLibrary", PermissionSections.System, null),
        new("picturesLibrary", PermissionSections.System, null),
        new("videosLibrary", PermissionSections.System, null),
        new("musicLibrary", PermissionSections.System, null),
        new("broadFileSystemAccess", PermissionSections.System, null),
        new("appDiagnostics", PermissionSections.System, "LetAppsGetDiagnosticInfo"),
        new("graphicsCaptureProgrammatic", PermissionSections.System, null),
        new("graphicsCaptureWithoutBorder", PermissionSections.System, null),
    ];

    public static string[] Sections { get; } = [PermissionSections.Devices, PermissionSections.Personal, PermissionSections.System];

    /// <summary>Camera and microphone (the quick switch).</summary>
    public static IReadOnlyList<string> CameraMicrophone { get; } = ["webcam", "microphone"];

    /// <summary>The privacy lockdown: camera, microphone and location.</summary>
    public static IReadOnlyList<string> Lockdown { get; } = ["webcam", "microphone", "location"];

    public static PermissionCapability? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public static bool IsKnown(string? id) => Find(id) is not null;
}

public static class ConsentValues
{
    public const string AllowText = "Allow", DenyText = "Deny";

    public static ConsentState StateOf(string? raw) =>
        raw is null ? ConsentState.Absent
        : raw.Equals(AllowText, StringComparison.OrdinalIgnoreCase) ? ConsentState.Allow
        : raw.Equals(DenyText, StringComparison.OrdinalIgnoreCase) ? ConsentState.Deny
        : ConsentState.Other;

    public static string RawOf(PermissionChoice choice) => choice == PermissionChoice.Allow ? AllowText : DenyText;

    /// <summary>True for the values Winora may write: absent (the value is removed), Allow or Deny.</summary>
    public static bool IsWritable(string? raw) => raw is null || StateOf(raw) is ConsentState.Allow or ConsentState.Deny;
}

/// <summary>The names a consent store sub key gives away. A package key is the package family name; a NonPackaged key is the program path with every backslash written as #.</summary>
public static class ConsentAppNames
{
    public const string DesktopKey = "NonPackaged";

    public static string PackageName(string familyName)
    {
        var i = familyName.IndexOf('_');
        return i > 0 ? familyName[..i] : familyName;
    }

    public static string DesktopPath(string key) => key.Replace('#', '\\');

    public static string DesktopName(string key)
    {
        var path = DesktopPath(key);
        var i = path.LastIndexOf('\\');
        return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
    }

    /// <summary>A sub key name that may be used as a per-package target. The NonPackaged key is not a package.</summary>
    public static bool IsPackageKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= 256 && !key.Equals(DesktopKey, StringComparison.OrdinalIgnoreCase) && !key.Any(c => c is '\\' or '/' || char.IsControl(c));
}

/// <summary>One app below a capability: a package or a desktop program. Times are the last use that Windows recorded, in UTC; <see cref="LastUsedStop"/> is null while no end was recorded.</summary>
public sealed record ConsentApp(string Key, bool IsDesktop, string Name, string? Path, string? Raw, DateTimeOffset? LastUsedStart, DateTimeOffset? LastUsedStop)
{
    public ConsentState State => ConsentValues.StateOf(Raw);
    public DateTimeOffset? LastUsed => LastUsedStop is { } stop && LastUsedStart is { } start ? (stop > start ? stop : start) : LastUsedStart ?? LastUsedStop;

    /// <summary>Only a package has a switch of its own; a desktop program follows the desktop switch of the capability.</summary>
    public bool CanChange => !IsDesktop;
}

/// <summary>Everything read for one capability. <see cref="Exists"/> is false when the key is not in the consent store of this Windows. <see cref="MachineRaw"/> is the device-wide value (read only) and <see cref="PolicyValue"/> the Group Policy DWORD (read only).</summary>
public sealed record ConsentRead(bool Ok, string Code, bool Exists, string? GlobalRaw, bool HasDesktopKey, string? DesktopRaw, string? MachineRaw, int? PolicyValue, IReadOnlyList<ConsentApp> Apps)
{
    public static ConsentRead Fail(string code) => new(false, code, false, null, false, null, null, null, []);
    public ConsentState Global => ConsentValues.StateOf(GlobalRaw);
    public ConsentState Desktop => ConsentValues.StateOf(DesktopRaw);
    public ConsentState Machine => ConsentValues.StateOf(MachineRaw);
}

public sealed record ConsentValueRead(bool Ok, string Code, string? Raw)
{
    public static ConsentValueRead Of(string? raw) => new(true, PermissionCode.Ok, raw);
    public static ConsentValueRead Fail(string code) => new(false, code, null);
}

public sealed record PermissionResult(bool Ok, string Code)
{
    public static PermissionResult Success { get; } = new(true, PermissionCode.Ok);
    public static PermissionResult Fail(string code) => new(false, code);
}

/// <summary>
/// The consent values of the current user. Reads cover the user's values, the device-wide value and the policy DWORD. A write is a compare-and-set of one existing "Value": it changes the value only
/// while it still holds <c>expected</c> (null means absent), writes Allow or Deny, or removes the value when <c>value</c> is null. It never creates a key.
/// </summary>
public interface IConsentStore
{
    ConsentRead Read(string capability);
    ConsentValueRead ReadValue(ConsentTarget target);
    PermissionResult Write(ConsentTarget target, string? expected, string? value);
}

/// <summary>One value of a change with the text it had before and the text it holds after. A null raw means the value is absent.</summary>
public sealed record PermissionEntry(ConsentTarget Target, string? BeforeRaw, string? AfterRaw);

public sealed record PermissionChange(Guid Id, PermissionGroup Group, IReadOnlyList<PermissionEntry> Entries, Guid? RevertOf);

public sealed record PermissionEntryResult(ConsentTarget Target, string Code);

/// <param name="Written">The journal holds the line that says every write of the change was made (after the last write, before the end line).</param>
public sealed record PermissionChangeRecord(PermissionChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, PermissionChangeState? State, string Code, IReadOnlyList<PermissionEntryResult> Results, bool Written = false);

/// <summary>Append-only record of permission changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IPermissionJournal
{
    void Begin(PermissionChange change, DateTimeOffset atUtc);
    void Complete(Guid id, PermissionChangeState state, string code, IReadOnlyList<PermissionEntryResult> results, DateTimeOffset atUtc);
    /// <summary>Records that every write of the change was made. Called after the last write returned and before the end line; never after only some of the writes.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<PermissionChangeRecord> History(int max);
}

/// <summary>What limits a user value: a device-wide Deny or a Group Policy that forces a state.</summary>
public enum PermissionLimit { None, MachineDeny, PolicyForceDeny, PolicyForceAllow }

public sealed record PermissionRowData(PermissionCapability Capability, ConsentRead Read)
{
    public PermissionLimit Limit =>
        Read.PolicyValue == 2 ? PermissionLimit.PolicyForceDeny
        : Read.PolicyValue == 1 ? PermissionLimit.PolicyForceAllow
        : Read.Machine == ConsentState.Deny ? PermissionLimit.MachineDeny
        : PermissionLimit.None;
}

public sealed record PermissionSnapshot(IReadOnlyList<PermissionRowData> Rows)
{
    public PermissionRowData? Find(string id) => Rows.FirstOrDefault(r => r.Capability.Id == id);
}

/// <summary>
/// The values to change. Every target gets <see cref="Choice"/>, unless <see cref="Choices"/> is set: then target i gets choice i (a staged set where some values are allowed and others blocked).
/// </summary>
public sealed record PermissionRequest(PermissionGroup Group, IReadOnlyList<ConsentTarget> Targets, PermissionChoice Choice, IReadOnlyList<PermissionChoice>? Choices = null)
{
    public PermissionChoice ChoiceAt(int index) => Choices is { } list && index < list.Count ? list[index] : Choice;
}

public sealed record PermissionPreviewEntry(ConsentTarget Target, string? BeforeRaw, string? AfterRaw, bool WillChange, string? RefusalCode, PermissionLimit Limit);

public sealed record PermissionPreview(PermissionRequest Request, IReadOnlyList<PermissionPreviewEntry> Entries, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
    public IReadOnlyList<PermissionPreviewEntry> Changes => Entries.Where(e => e.WillChange).ToList();
}

public sealed record PermissionApplyResult(PermissionChangeState State, string Code, PermissionChange? Change, IReadOnlyList<PermissionEntryResult> Results, bool RecordLost = false)
{
    public bool Ok => State == PermissionChangeState.Applied;
}

/// <summary>Stable codes of permission changes.</summary>
public static class PermissionCode
{
    public const string Ok = "ok";
    public const string Reconciled = "permission.reconciled";
    public const string InterruptedUnchanged = "permission.interrupted_unchanged";
    public const string Dismissed = "permission.dismissed";
    public const string ResolvedRestored = "permission.resolved_restored";
    public const string NotUnconfirmed = "permission.not_unconfirmed";
    public const string Unknown = "perm.unknown";
    public const string NotFound = "perm.not_found";
    public const string Unusual = "perm.unusual";
    public const string AlreadySet = "perm.already_set";
    public const string Stale = "perm.stale";
    public const string Unreadable = "perm.unreadable";
    public const string VerifyFailed = "perm.verify_failed";
    public const string CannotRevert = "perm.cannot_revert";
    public const string AccessDenied = "perm.access_denied";
    public const string Failed = "perm.failed";
    public const string JournalFailed = "perm.journal_failed";
    public const string RolledBack = "perm.rolled_back";
    public const string RollbackFailed = "perm.rollback_failed";
    public const string NothingToDo = "perm.nothing";
}
