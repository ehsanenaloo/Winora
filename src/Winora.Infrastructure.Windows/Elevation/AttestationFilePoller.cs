// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>Seam over the file-system read of one record (the D0 reader with its ACL reader), so tests can describe every hostile case as data.</summary>
public interface IAttestationFileReader
{
    EW.AttestationFileRead Read(string recordPath, string runDirectory);
}

/// <summary>The real reader: share modes of 6.3, no reparse following, ACL evaluation of the run directory.</summary>
public sealed class WindowsAttestationFileReader(EW.IAclReader? aclReader = null) : IAttestationFileReader
{
    public EW.AttestationFileRead Read(string recordPath, string runDirectory) => EW.AttestationFileReader.Read(recordPath, runDirectory, aclReader);
}

/// <summary>
/// Reads the admin-written records of section 6 for one rendezvous: <c>&lt;R&gt;.fail</c> first (an early failure is final), then
/// <c>&lt;R&gt;.att</c>. A record is returned to the waiter only when it is a regular, single-link, non-reparse file that really lives in the run
/// directory, whose own ACL satisfies the install rule (6.2). A missing file or a sharing violation (the worker still writing) is "not yet";
/// anything else that is wrong is a rejection, never a retry. Content is parsed and compared by <see cref="AttestationWaiter"/>.
/// </summary>
public sealed class AttestationFilePoller : IAttestationSource
{
    private readonly string runDirectory;
    private readonly IAttestationFileReader reader;

    public AttestationFilePoller(string runDirectory, IAttestationFileReader? reader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        this.runDirectory = runDirectory.TrimEnd('\\', '/');
        this.reader = reader ?? new WindowsAttestationFileReader();
    }

    /// <summary>The fixed run directory of an install root (<c>&lt;InstallRoot&gt;\run</c>).</summary>
    public static string RunDirectoryFor(string installRoot) => Path.Combine(installRoot, ElevationBuild.RunFolder);

    public AttestationPollResult Poll(Bytes16 rendezvous)
    {
        var name = rendezvous.ToHex();
        var failure = Evaluate(reader.Read(Path.Combine(runDirectory, name + ".fail"), runDirectory), early: true);
        return failure.Kind != AttestationPollKind.NotYet ? failure : Evaluate(reader.Read(Path.Combine(runDirectory, name + ".att"), runDirectory), early: false);
    }

    private static AttestationPollResult Evaluate(EW.AttestationFileRead read, bool early)
    {
        switch (read.Status)
        {
            case EW.AttestationReadStatus.NotFound:
            case EW.AttestationReadStatus.SharingViolation:
                return AttestationPollResult.NotYet;
            case EW.AttestationReadStatus.Ok:
                break;
            default:
                return AttestationPollResult.Rejected(ProtocolFailure.ServerUntrusted());
        }
        // The worker creates the file and then writes it; the share mode lets a reader in between. A record shorter than its fixed length is therefore not final yet
        // (checked only for a regular file within the bounds, so every other shape below is still judged as before).
        var expected = early ? ElevationLimits.EarlyFailureRecordBytes : ElevationLimits.AttestationRecordBytes;
        if (read.IsRegularFile && !read.TooLarge && read.Bytes.Length < expected) return AttestationPollResult.NotYet;
        // 6.2: regular file, link count 1, inside run\, not a reparse point, run\ ACL rule holds. The owner SID is logged by callers, never relied on.
        if (read.TooLarge || !read.IsRegularFile || read.IsReparsePoint || read.LinkCount != 1 || !read.InsideRunDirectory || !read.RunDirectoryRuleHolds)
            return AttestationPollResult.Rejected(ProtocolFailure.ServerUntrusted());
        return early ? AttestationPollResult.EarlyFailure(read.Bytes) : AttestationPollResult.Attestation(read.Bytes);
    }
}
