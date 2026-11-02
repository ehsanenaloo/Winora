// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Domain.Elevation;
using ProtocolAttestationRecord = Winora.Elevation.Protocol.AttestationRecord;
using ProtocolEarlyFailureRecord = Winora.Elevation.Protocol.EarlyFailureRecord;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>Verdict of the run-directory rule. <see cref="Detail"/> is a short closed token for diagnostics only.</summary>
public sealed record RunDirectoryVerdict(bool Trusted, string Detail, IReadOnlyList<AclFinding> Findings);

/// <summary>
/// The worker's own check of <c>&lt;InstallRoot&gt;\run</c> before it creates the lock or the attestation (14.1 step 7): the directory exists, is a real
/// directory (no reparse point), and no principal other than SYSTEM, Administrators and TrustedInstaller can write, append, delete, create
/// children or change the DACL or owner; the owner is one of those. The directory path is a parameter, so tests use temporary directories with a fake
/// <see cref="IAclReader"/>.
/// </summary>
public static class RunDirectoryGuard
{
    private const uint ReadControl = 0x20000, FileReadAttributes = 0x80, FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400, ShareAll = 7, OpenExisting = 3;

    public static RunDirectoryVerdict Check(string runDirectory, IAclReader? aclReader = null)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "platform", []);
        aclReader ??= new WindowsAclReader();
        using var dir = Native.CreateFileW(@"\\?\" + runDirectory.TrimEnd('\\'), ReadControl | FileReadAttributes, ShareAll, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (dir.IsInvalid) return new(false, "open", []);
        if (!Native.GetFileInformationByHandle(dir, out var info)) return new(false, "info", []);
        if ((info.FileAttributes & AttributeReparsePoint) != 0) return new(false, "reparse", []);
        if ((info.FileAttributes & AttributeDirectory) == 0) return new(false, "not_directory", []);
        var acl = aclReader.Read(dir, runDirectory);
        if (acl.Status != AclReadStatus.Ok || acl.Descriptor is null) return new(false, "acl_unreadable", []);
        var findings = AclEvaluator.Evaluate(acl.Descriptor, WriteRightsTier.Full);
        return findings.Count == 0 ? new(true, "ok", findings) : new(false, "acl", findings);
    }
}

public enum RunFileStatus { Created, AlreadyExists, SharingViolation, Failed }

/// <summary>
/// A file the worker created in <c>run\</c> and keeps open. Disposing the holder closes the handle; the file was opened delete-on-close, so it
/// disappears when the last handle (this one and the client's brief read handle) is closed.
/// </summary>
public sealed class RunFileHolder : IDisposable
{
    private SafeFileHandle? handle;
    internal RunFileHolder(SafeFileHandle handle, string path) { this.handle = handle; Path = path; }
    public string Path { get; }
    internal SafeFileHandle? Handle => handle;
    public bool IsOpen => handle is { IsClosed: false };
    public void Dispose() { handle?.Dispose(); handle = null; }
}

public sealed record RunFileResult(RunFileStatus Status, int Win32Error, RunFileHolder? Holder)
{
    public bool IsCreated => Status == RunFileStatus.Created;
}

/// <summary>Shared creation primitive for the three files the worker puts in <c>run\</c>. Every open is by full path under the directory given, no reparse following.</summary>
internal static class RunFiles
{
    private const uint FileFlagDeleteOnClose = AttestationFileModes.FileFlagDeleteOnClose, DeleteAccess = 0x10000, FileAttributeNormal = 0x80;
    private const uint CreateAlways = 2;
    private const int ErrorFileExists = 80, ErrorAlreadyExists = 183, ErrorSharingViolation = 32;

    /// <summary>CREATE_NEW or CREATE_ALWAYS with GENERIC_WRITE and DELETE (delete-on-close needs DELETE), the given share mode and no reparse following.</summary>
    internal static RunFileResult Create(string runDirectory, string fileName, uint share, uint disposition)
    {
        if (!OperatingSystem.IsWindows()) return new(RunFileStatus.Failed, 0, null);
        string path = runDirectory.TrimEnd('\\') + "\\" + fileName;
        var handle = Native.CreateFileW(@"\\?\" + path, AttestationFileModes.GenericWrite | DeleteAccess, share, 0, disposition,
            FileAttributeNormal | FileFlagDeleteOnClose | AttestationFileModes.FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return new(err is ErrorFileExists or ErrorAlreadyExists ? RunFileStatus.AlreadyExists : err == ErrorSharingViolation ? RunFileStatus.SharingViolation : RunFileStatus.Failed, err, null);
        }
        return new(RunFileStatus.Created, 0, new RunFileHolder(handle, path));
    }

    internal static bool WriteAll(RunFileHolder holder, byte[] bytes)
    {
        var h = holder.Handle;
        if (h is null) return false;
        try { RandomAccess.Write(h, bytes, 0); return true; }
        catch (IOException) { return false; }
    }

    internal static uint CreateAlwaysDisposition => CreateAlways;
}

/// <summary>
/// Writer for <c>&lt;R&gt;.att</c>: CREATE_NEW, GENERIC_WRITE, share FILE_SHARE_READ only, delete-on-close; the caller
/// keeps the holder for the worker's lifetime. The 106 bytes are the Protocol library's encoding of the record the session published.
/// </summary>
public static class AttestationWriter
{
    public static string FileNameFor(Bytes16 rendezvous) => rendezvous.ToHex() + ".att";

    public static RunFileResult TryWrite(string runDirectory, ProtocolAttestationRecord record)
    {
        var created = RunFiles.Create(runDirectory, FileNameFor(record.Rendezvous), AttestationFileModes.AttestationWriterShare, AttestationFileModes.CreateNew);
        if (!created.IsCreated) return created;
        if (!RunFiles.WriteAll(created.Holder!, WireCodec.Encode(record)))
        {
            created.Holder!.Dispose();
            return new(RunFileStatus.Failed, 0, null);
        }
        return created;
    }
}

/// <summary>
/// Writer for <c>&lt;R&gt;.fail</c>: CREATE_NEW, GENERIC_WRITE, share read, write and delete, delete-on-close, and the handle is
/// HELD for a grace period so a live client can read it. The record carries a closed reason and the first 8 bytes of R and nothing else. It is written
/// even when the run-directory rule failed (the <c>run_dir</c> reason cannot be reported otherwise); the client trusts it only after its own ACL check.
/// </summary>
public static class EarlyFailureWriter
{
    public static string FileNameFor(Bytes16 rendezvous) => rendezvous.ToHex() + ".fail";

    public static RunFileResult TryWrite(string runDirectory, EarlyFailureReason reason, Bytes16 rendezvous)
    {
        var created = RunFiles.Create(runDirectory, FileNameFor(rendezvous), AttestationFileModes.EarlyFailureWriterShare, AttestationFileModes.CreateNew);
        if (!created.IsCreated) return created;
        if (!RunFiles.WriteAll(created.Holder!, WireCodec.Encode(ProtocolEarlyFailureRecord.For(reason, rendezvous))))
        {
            created.Holder!.Dispose();
            return new(RunFileStatus.Failed, 0, null);
        }
        return created;
    }

    /// <summary>Writes the record, holds it for <paramref name="grace"/> on the injected clock, then closes it. A failed write is silent (the client then sees a timeout, the documented residual).</summary>
    public static async Task WriteAndHoldAsync(string runDirectory, EarlyFailureReason reason, Bytes16 rendezvous, TimeSpan grace, TimeProvider time, CancellationToken ct = default)
    {
        var result = TryWrite(runDirectory, reason, rendezvous);
        if (!result.IsCreated) return;
        using (result.Holder)
        {
            try { await Task.Delay(grace, time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}

public enum WorkerLockStatus { Acquired, Busy, Failed }

/// <summary>The single-instance probe of 13.1: <c>run\worker.lock</c>, CREATE_ALWAYS, share none, delete-on-close. A second worker sees a sharing violation.</summary>
public sealed class WorkerLock : IDisposable
{
    public const string FileName = "worker.lock";
    private readonly RunFileHolder? holder;
    private WorkerLock(WorkerLockStatus status, RunFileHolder? holder) { Status = status; this.holder = holder; }
    public WorkerLockStatus Status { get; }
    public bool IsAcquired => Status == WorkerLockStatus.Acquired;

    public static WorkerLock TryAcquire(string runDirectory)
    {
        var r = RunFiles.Create(runDirectory, FileName, 0, RunFiles.CreateAlwaysDisposition);
        return r.Status switch
        {
            RunFileStatus.Created => new(WorkerLockStatus.Acquired, r.Holder),
            RunFileStatus.SharingViolation => new(WorkerLockStatus.Busy, null),
            _ => new(WorkerLockStatus.Failed, null),
        };
    }

    public void Dispose() => holder?.Dispose();
}
