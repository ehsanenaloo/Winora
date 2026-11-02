// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Whether System Protection is known to be off. Windows reports it reliably only when a restore point is requested, so a PC with no policy that turns it off reads as Unknown.</summary>
public enum RestoreProtection { Unknown, Disabled }

/// <summary>A restore point that Winora created, from its journal. Windows offers no documented way to list the others without WMI.</summary>
public sealed record RestorePointInfo(long Sequence, string Description, DateTimeOffset CreatedUtc);

/// <summary>One attempt by Winora to create a restore point. A record without an end is an attempt that was interrupted.</summary>
public sealed record RestorePointRecord(Guid Id, DateTimeOffset BeganUtc, string Description, DateTimeOffset? EndedUtc, bool? Ok, string Code, long Sequence, bool MayBeReused);

/// <summary>Append-only record of restore point attempts. <see cref="Begin"/> is durable before it returns.</summary>
public interface IRestorePointJournal
{
    void Begin(Guid id, string description, DateTimeOffset atUtc);
    void Complete(Guid id, bool ok, string code, long sequence, bool mayBeReused, DateTimeOffset atUtc);
    /// <summary>The newest attempts first.</summary>
    IReadOnlyList<RestorePointRecord> History(int max);
}

/// <param name="Called">False when the System Restore library could not be used at all.</param>
/// <param name="Status">The nStatus member of STATEMGRSTATUS (0 is success) or the Win32 error of a failed call.</param>
/// <param name="Sequence">The sequence number Windows reported.</param>
public sealed record RestorePointNativeResult(bool Called, uint Status, long Sequence);

/// <summary>The System Restore functions Winora uses. Only the Windows adapter implements it; tests use a fake and never create a real restore point.</summary>
public interface IRestorePointPort
{
    RestoreProtection ReadProtection();
    /// <summary>The SystemRestorePointCreationFrequency value in minutes, or null when it is not set.</summary>
    int? ReadCreationFrequencyMinutes();
    /// <summary>Begins and ends one system change with the given description (a MODIFY_SETTINGS restore point).</summary>
    RestorePointNativeResult Create(string description);
}

public sealed record RestorePointStatus(
    RestoreProtection Protection, int ThrottleMinutes, bool ThrottleIsDefault, DateTimeOffset? LastCreatedUtc, bool ThrottleLikelyActive, bool CanCreate,
    IReadOnlyList<RestorePointInfo> Points, IReadOnlyList<RestorePointRecord> Attempts);

/// <param name="MayBeReused">Windows reports success and the number of the earlier restore point when it skips a new one under the creation limit, so this result cannot tell the two apart.</param>
public sealed record RestorePointResult(bool Ok, string Code, long Sequence, bool MayBeReused);

/// <summary>Stable codes of restore point creation.</summary>
public static class RestorePointCode
{
    public const string Ok = "ok";
    public const string NeedsAdministrator = "restore.needs_administrator";
    public const string ProtectionOff = "restore.protection_off";
    public const string DiskFull = "restore.disk_full";
    public const string SafeMode = "restore.safe_mode";
    public const string Busy = "restore.busy";
    public const string ServiceUnavailable = "restore.service_unavailable";
    public const string ApiUnavailable = "restore.api_unavailable";
    public const string Failed = "restore.failed";

    /// <summary>Maps an nStatus of STATEMGRSTATUS or a Win32 error to a stable code.</summary>
    public static string FromStatus(uint status) => status switch
    {
        0 => Ok,
        1058 => ProtectionOff,            // ERROR_SERVICE_DISABLED
        112 => DiskFull,                  // ERROR_DISK_FULL
        10 => SafeMode,                   // ERROR_BAD_ENVIRONMENT
        1460 => Busy,                     // ERROR_TIMEOUT
        5 => NeedsAdministrator,          // ERROR_ACCESS_DENIED
        1053 or 1060 or 1062 or 1722 or 1726 or 1753 => ServiceUnavailable, // service did not respond, does not exist, not started, or the RPC server is unavailable
        _ => Failed,
    };
}
