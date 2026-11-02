// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Windows;

public enum RecordParseError { None, Length, Magic, ReservedNotZero, FlavorUndefined, ProtocolRange, ReasonUndefined, VersionUndefined }

/// <summary>Attestation record of A.8.1: exactly 106 bytes, little-endian fields.</summary>
public sealed record AttestationRecord(
    byte Flavor, byte ProtoMin, byte ProtoMax, Bytes16 Rendezvous, uint WorkerPid, ulong WorkerStartFiletime, uint SessionId, Bytes32 PublicKeyHash, Bytes32 DefinitionSetHash);

/// <summary>Early-failure record of A.8.2: exactly 16 bytes.</summary>
public sealed record EarlyFailureRecord(EarlyFailureReason Reason, byte[] RendezvousPrefix);

/// <summary>Pure encoder and strict decoder of the two record formats.</summary>
public static class AttestationFormat
{
    public const int AttestationSize = ElevationLimits.AttestationRecordBytes;
    public const int EarlyFailureSize = ElevationLimits.EarlyFailureRecordBytes;
    private static ReadOnlySpan<byte> AttMagic => "WEATT1"u8;
    private static ReadOnlySpan<byte> FailMagic => "WEFAIL"u8;

    public static bool TryParseAttestation(ReadOnlySpan<byte> bytes, out AttestationRecord? record, out RecordParseError error)
    {
        record = null;
        if (bytes.Length != AttestationSize) { error = RecordParseError.Length; return false; }
        if (!bytes[..6].SequenceEqual(AttMagic)) { error = RecordParseError.Magic; return false; }
        byte flavor = bytes[6], min = bytes[7], max = bytes[8];
        if (!ElevationFlavors.IsDefined(flavor)) { error = RecordParseError.FlavorUndefined; return false; }
        if (min > max) { error = RecordParseError.ProtocolRange; return false; }
        if (bytes[9] != 0) { error = RecordParseError.ReservedNotZero; return false; }
        record = new AttestationRecord(flavor, min, max, new Bytes16(bytes.Slice(10, 16)), BinaryPrimitives.ReadUInt32LittleEndian(bytes[26..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[30..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[38..]), new Bytes32(bytes.Slice(42, 32)), new Bytes32(bytes.Slice(74, 32)));
        error = RecordParseError.None;
        return true;
    }

    public static byte[] Encode(AttestationRecord r)
    {
        var b = new byte[AttestationSize];
        AttMagic.CopyTo(b);
        b[6] = r.Flavor; b[7] = r.ProtoMin; b[8] = r.ProtoMax; b[9] = 0;
        r.Rendezvous.CopyTo(b.AsSpan(10));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(26), r.WorkerPid);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(30), r.WorkerStartFiletime);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(38), r.SessionId);
        r.PublicKeyHash.CopyTo(b.AsSpan(42));
        r.DefinitionSetHash.CopyTo(b.AsSpan(74));
        return b;
    }

    public static bool TryParseEarlyFailure(ReadOnlySpan<byte> bytes, out EarlyFailureRecord? record, out RecordParseError error)
    {
        record = null;
        if (bytes.Length != EarlyFailureSize) { error = RecordParseError.Length; return false; }
        if (!bytes[..6].SequenceEqual(FailMagic)) { error = RecordParseError.Magic; return false; }
        if (bytes[6] != 1) { error = RecordParseError.VersionUndefined; return false; }
        if (bytes[7] is < 1 or > 12) { error = RecordParseError.ReasonUndefined; return false; }
        record = new EarlyFailureRecord((EarlyFailureReason)bytes[7], bytes.Slice(8, 8).ToArray());
        error = RecordParseError.None;
        return true;
    }

    public static byte[] Encode(EarlyFailureRecord r)
    {
        if (r.RendezvousPrefix.Length != 8) throw new ArgumentException("prefix must be 8 bytes", nameof(r));
        var b = new byte[EarlyFailureSize];
        FailMagic.CopyTo(b);
        b[6] = 1; b[7] = (byte)r.Reason;
        r.RendezvousPrefix.CopyTo(b.AsSpan(8));
        return b;
    }
}

/// <summary>Which acceptance fields differ. The caller maps a non-empty set to its code (for example elevation.server_untrusted).</summary>
[Flags]
public enum AttestationMismatch { None = 0, Rendezvous = 1, Flavor = 2, DefinitionSetHash = 4, SessionId = 8 }

public static class AttestationAcceptance
{
    public static AttestationMismatch Compare(AttestationRecord record, Bytes16 rendezvous, byte flavor, Bytes32 definitionSetHash, uint clientSessionId)
    {
        var m = AttestationMismatch.None;
        if (record.Rendezvous != rendezvous) m |= AttestationMismatch.Rendezvous;
        if (record.Flavor != flavor) m |= AttestationMismatch.Flavor;
        if (record.DefinitionSetHash != definitionSetHash) m |= AttestationMismatch.DefinitionSetHash;
        if (record.SessionId != clientSessionId) m |= AttestationMismatch.SessionId;
        return m;
    }
}

/// <summary>The exact open modes of and 6.4 as constants for the reader here and the writer package.</summary>
public static class AttestationFileModes
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    public const uint CreateNew = 1, OpenExisting = 3;
    public const uint FileFlagDeleteOnClose = 0x04000000, FileFlagOpenReparsePoint = 0x00200000;
    /// <summary>Client reader: GENERIC_READ, share read, write and delete, no reparse following.</summary>
    public const uint ReaderShare = ShareRead | ShareWrite | ShareDelete;
    /// <summary>Worker attestation writer: CREATE_NEW, GENERIC_WRITE, share read only, delete on close.</summary>
    public const uint AttestationWriterShare = ShareRead;
    /// <summary>Worker early-failure writer: CREATE_NEW, GENERIC_WRITE, share read, write and delete, delete on close.</summary>
    public const uint EarlyFailureWriterShare = ShareRead | ShareWrite | ShareDelete;
}

public enum AttestationReadStatus { Ok, NotFound, SharingViolation, AccessDenied, Failed }

/// <summary>Everything the client needs to apply 6.2, as data. Nothing here is a verdict about trust; the caller decides.</summary>
public sealed record AttestationFileRead(
    AttestationReadStatus Status, int Win32Error, byte[] Bytes, bool TooLarge, bool IsRegularFile, bool IsReparsePoint, uint LinkCount,
    bool InsideRunDirectory, string? OwnerSid, IReadOnlyList<AclFinding> RunDirectoryFindings, bool RunDirectoryAclReadable)
{
    public bool RunDirectoryRuleHolds => RunDirectoryAclReadable && RunDirectoryFindings.Count == 0;

    public bool TryParseAttestation(out AttestationRecord? record, out RecordParseError error)
    {
        record = null;
        if (Status != AttestationReadStatus.Ok || TooLarge) { error = RecordParseError.Length; return false; }
        return AttestationFormat.TryParseAttestation(Bytes, out record, out error);
    }

    public bool TryParseEarlyFailure(out EarlyFailureRecord? record, out RecordParseError error)
    {
        record = null;
        if (Status != AttestationReadStatus.Ok || TooLarge) { error = RecordParseError.Length; return false; }
        return AttestationFormat.TryParseEarlyFailure(Bytes, out record, out error);
    }
}

/// <summary>Reads a record file with the client share modes of section 6.3 and reports the facts section 6.2 requires. It never writes.</summary>
public static class AttestationFileReader
{
    private const uint ReadControl = 0x20000, FileReadAttributes = 0x80, FileFlagBackupSemantics = 0x02000000;
    private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;

    public static AttestationFileRead Read(string recordPath, string runDirectory, IAclReader? aclReader = null)
    {
        if (!OperatingSystem.IsWindows()) return Failed(AttestationReadStatus.Failed, 0);
        aclReader ??= new WindowsAclReader();
        using var file = Native.CreateFileW(@"\\?\" + recordPath, AttestationFileModes.GenericRead | ReadControl, AttestationFileModes.ReaderShare, 0,
            AttestationFileModes.OpenExisting, FileFlagBackupSemantics | AttestationFileModes.FileFlagOpenReparsePoint, 0);
        if (file.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            return Failed(err switch { 2 or 3 => AttestationReadStatus.NotFound, 32 => AttestationReadStatus.SharingViolation, 5 => AttestationReadStatus.AccessDenied, _ => AttestationReadStatus.Failed }, err);
        }
        if (!Native.GetFileInformationByHandle(file, out var info)) return Failed(AttestationReadStatus.Failed, Marshal.GetLastPInvokeError());
        bool reparse = (info.FileAttributes & AttributeReparsePoint) != 0;
        bool regular = !reparse && (info.FileAttributes & AttributeDirectory) == 0;

        byte[] bytes = Array.Empty<byte>();
        bool tooLarge = false;
        if (regular)
        {
            var buffer = new byte[ElevationLimits.AttestationRecordBytes + 1];
            int n = RandomAccess.Read(file, buffer, 0);
            if (n > ElevationLimits.AttestationRecordBytes) tooLarge = true;
            else bytes = buffer.AsSpan(0, n).ToArray();
        }

        string? finalPath = FinalPath(file);
        bool inside = finalPath is not null && string.Equals(Path.GetDirectoryName(finalPath), runDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        var owner = aclReader.Read(file, recordPath);
        string? ownerSid = owner.Descriptor?.OwnerSid;

        var dirFindings = new List<AclFinding>();
        bool dirReadable = false;
        using (var dir = Native.CreateFileW(@"\\?\" + runDirectory.TrimEnd('\\'), ReadControl | FileReadAttributes, AttestationFileModes.ReaderShare, 0,
            AttestationFileModes.OpenExisting, FileFlagBackupSemantics | AttestationFileModes.FileFlagOpenReparsePoint, 0))
        {
            if (!dir.IsInvalid && Native.GetFileInformationByHandle(dir, out var dinfo) && (dinfo.FileAttributes & AttributeReparsePoint) == 0)
            {
                var acl = aclReader.Read(dir, runDirectory);
                if (acl.Status == AclReadStatus.Ok && acl.Descriptor is not null) { dirReadable = true; dirFindings.AddRange(AclEvaluator.Evaluate(acl.Descriptor, WriteRightsTier.Full)); }
            }
        }
        return new AttestationFileRead(AttestationReadStatus.Ok, 0, bytes, tooLarge, regular, reparse, info.NumberOfLinks, inside, ownerSid, dirFindings, dirReadable);
    }

    private static AttestationFileRead Failed(AttestationReadStatus status, int error) =>
        new(status, error, Array.Empty<byte>(), false, false, false, 0, false, null, Array.Empty<AclFinding>(), false);

    private static unsafe string? FinalPath(SafeFileHandle h)
    {
        char[] buf = new char[1024];
        fixed (char* p = buf)
        {
            uint n = Native.GetFinalPathNameByHandleW(h, p, (uint)buf.Length, 0);
            if (n == 0 || n >= buf.Length) return null;
            string s = new(p, 0, (int)n);
            return s.StartsWith(@"\\?\", StringComparison.Ordinal) && !s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? s[4..] : null;
        }
    }
}
