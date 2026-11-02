// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;

namespace Winora.Application;

public enum StartupAction { Enable, Disable }

/// <summary>Dismissed: the person closed a record whose outcome was never confirmed. It has no undo.</summary>
public enum StartupChangeState { Applied, Failed, Refused, Dismissed }

/// <summary>Why an entry cannot be changed here. <see cref="ScheduledTask"/> rows are read only: the Scheduled Tasks page is the one place that changes a task. <see cref="Protected"/> still allows enabling an entry that someone disabled, so Windows defaults can be restored.</summary>
public enum StartupBlock { None, RunOnce, Unmanaged, Protected, ScheduledTask }

/// <summary>What is known about the file an entry starts. Every field is read-only metadata; the file is never run.</summary>
public sealed record StartupFileFacts(string? TargetPath, bool? Exists, long? Size, long? LastWriteTicks, bool InWindowsFolder)
{
    public static StartupFileFacts Unknown { get; } = new(null, null, null, null, false);
}

/// <summary>A startup entry with the facts and the rules that decide whether Winora may change it. <see cref="NeedsAdministrator"/> is true for a machine-wide entry while the app has no administrator rights.</summary>
public sealed record StartupEntry(StartupItem Item, StartupFileFacts File, StartupBlock Block, bool NeedsAdministrator)
{
    /// <summary>True when the entry's target file is known to be missing.</summary>
    public bool FileMissing => File.Exists == false;

    /// <summary>True when Winora has this change for the entry, whatever rights the app has now. A protected entry can only be turned on again.</summary>
    public bool CanOffer(StartupAction action) => Block switch
    {
        StartupBlock.None => true,
        StartupBlock.Protected => action == StartupAction.Enable && Item.Approval != StartupApproval.Enabled,
        _ => false,
    };

    public bool Allows(StartupAction action) => !NeedsAdministrator && CanOffer(action);
}

/// <summary>The StartupApproved value of one entry as read. <see cref="Bytes"/> is null when the value is absent (which Windows treats as enabled).</summary>
public sealed record StartupApprovalRead(bool Ok, string Code, byte[]? Bytes)
{
    public static StartupApprovalRead Of(byte[]? bytes) => new(true, "ok", bytes);
    public static StartupApprovalRead Fail(string code) => new(false, code, null);
}

public sealed record StartupResult(bool Ok, string Code)
{
    public static StartupResult Success { get; } = new(true, "ok");
    public static StartupResult Fail(string code) => new(false, code);
}

/// <summary>
/// The Explorer StartupApproved values. Reads work for both hives; a write is a compare-and-set: it changes the value only when it still holds <c>expected</c>
/// (null means absent) and removes the value when <c>value</c> is null. The Run value and the shortcut file are never touched.
/// </summary>
public interface IStartupApprovals
{
    StartupApprovalRead Read(InstallationScope scope, StartupLocation location);
    StartupResult Write(InstallationScope scope, StartupLocation location, byte[]? expected, byte[]? value);
}

/// <summary>Reads metadata of the file a startup command points to. Must tolerate every failure and must never run the file. <c>wow64</c> is true for a command of the 32-bit Run key, whose variables name the 32-bit program folders.</summary>
public interface IStartupFiles
{
    StartupFileFacts Inspect(string command, bool wow64);
}

/// <summary>Opens a file manager window with the file selected.</summary>
public interface IFileLocationOpener
{
    bool Reveal(string path);
}

/// <summary>One change to a startup entry, recorded before it is made. <see cref="BeforeHex"/> and <see cref="AfterHex"/> are the StartupApproved bytes (null = value absent).</summary>
public sealed record StartupChange(Guid Id, string EntryId, string Name, InstallationScope Scope, StartupLocation Location, StartupAction Action, string? BeforeHex, string? AfterHex, Guid? RevertOf);

/// <summary>A journal entry. A record without an end is a change that was interrupted.</summary>
/// <param name="Written">The journal holds the line that says the write was made (after the write, before the end line).</param>
public sealed record StartupChangeRecord(StartupChange Change, DateTimeOffset BeganUtc, DateTimeOffset? EndedUtc, StartupChangeState? State, string Code, bool Written = false);

/// <summary>Append-only record of startup changes. <see cref="Begin"/> must be durable before it returns.</summary>
public interface IStartupJournal
{
    void Begin(StartupChange change, DateTimeOffset atUtc);
    void Complete(Guid id, StartupChangeState state, string code, DateTimeOffset atUtc);
    /// <summary>Records that the write of the change was made. Called after the write returned and before the end line.</summary>
    void Written(Guid id, DateTimeOffset atUtc) { }
    /// <summary>The newest changes first.</summary>
    IReadOnlyList<StartupChangeRecord> History(int max);
}

/// <summary>What the user asks for. When <see cref="CheckExpected"/> is set, nothing is written unless the value still equals <see cref="ExpectedBeforeHex"/> (null = absent), the state the review showed.</summary>
public sealed record StartupRequest(string EntryId, StartupAction Action, string? ExpectedBeforeHex = null, bool CheckExpected = false);

/// <summary>What would happen. <see cref="RefusalCode"/> is set when the change cannot be made.</summary>
public sealed record StartupPreview(StartupRequest Request, StartupEntry? Entry, StartupApproval Before, string? BeforeHex, StartupApproval After, bool WillChange, string? RefusalCode)
{
    public bool IsRefused => RefusalCode is not null;
}

public sealed record StartupApplyResult(StartupChangeState State, string Code, StartupChange? Change, bool RecordLost = false)
{
    public bool Ok => State == StartupChangeState.Applied;
}

/// <summary>Stable codes of startup changes.</summary>
public static class StartupCode
{
    public const string Ok = "ok";
    public const string NotFound = "startup.not_found";
    public const string RunOnce = "startup.run_once";
    public const string Unmanaged = "startup.unmanaged";
    public const string Protected = "startup.protected";
    public const string TaskManaged = "startup.task_managed";
    public const string NeedsAdministrator = "startup.needs_admin";
    public const string AlreadySet = "startup.already_set";
    public const string Stale = "startup.stale";
    public const string InterruptedUnchanged = "startup.interrupted_unchanged";
    public const string Reconciled = "startup.reconciled";
    public const string Dismissed = "startup.dismissed";
    public const string ResolvedRestored = "startup.resolved_restored";
    public const string NotUnconfirmed = "startup.not_unconfirmed";
    public const string Unreadable = "startup.unreadable";
    public const string VerifyFailed = "startup.verify_failed";
    public const string CannotRevert = "startup.cannot_revert";
    public const string AccessDenied = "startup.access_denied";
    public const string Failed = "startup.failed";
    public const string JournalFailed = "startup.journal_failed";
}

/// <summary>
/// The StartupApproved value format used by Task Manager and the Settings app. Observed on Windows 11 and Windows 10 machines, not a published contract: 12 bytes,
/// first byte 0x02 or 0x06 enabled, 0x03 or 0x07 disabled, bytes 4 to 11 the FILETIME of the change (zero for enabled). A missing value means enabled.
/// </summary>
public static class StartupApprovalFormat
{
    public const int Length = 12;

    public static StartupApproval Decode(byte[]? bytes)
    {
        if (bytes is null) return StartupApproval.Enabled;
        if (bytes.Length == 0) return StartupApproval.Unknown;
        return bytes[0] switch { 0x02 or 0x06 => StartupApproval.Enabled, 0x03 or 0x07 => StartupApproval.Disabled, _ => StartupApproval.Unknown };
    }

    public static byte[] Encode(StartupAction action, DateTimeOffset now)
    {
        var bytes = new byte[Length];
        if (action == StartupAction.Enable) { bytes[0] = 0x02; return bytes; }
        bytes[0] = 0x03;
        BitConverter.TryWriteBytes(bytes.AsSpan(4), now.ToFileTime());
        return bytes;
    }

    public static string? ToHex(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(bytes);
    public static byte[]? FromHex(string? hex) => hex is null ? null : Convert.FromHexString(hex);
}

/// <summary>The group a startup entry belongs to on the Startup page, by the place that starts it.</summary>
public enum StartupCategory { Registry, StartupFolder, ScheduledTask }

public static class StartupCategories
{
    /// <summary>The groups in the order the page shows them.</summary>
    public static IReadOnlyList<StartupCategory> All { get; } = [StartupCategory.Registry, StartupCategory.StartupFolder, StartupCategory.ScheduledTask];

    public static StartupCategory Of(StartupItem item) => item.Source switch
    {
        StartupSource.StartupFolder => StartupCategory.StartupFolder,
        StartupSource.ScheduledTask => StartupCategory.ScheduledTask,
        _ => StartupCategory.Registry,
    };
}
