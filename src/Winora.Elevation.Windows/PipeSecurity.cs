// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Windows;

/// <summary>Individual pipe access rights used by section 5 (never generic masks).</summary>
public static class PipeRights
{
    public const uint FileReadData = 0x1, FileWriteData = 0x2, FileCreatePipeInstance = 0x4, FileReadAttributes = 0x80, FileWriteAttributes = 0x100,
        ReadControl = 0x20000, Synchronize = 0x100000;
    /// <summary>Mask granted to the client's logon SID: 0x00120083. Omits 0x4 (create instance) and 0x100 (write attributes).</summary>
    public const uint ClientGranted = FileReadData | FileWriteData | FileReadAttributes | ReadControl | Synchronize;
    /// <summary>Desired access the client must request: 0x00100083 (section 5).</summary>
    public const uint ClientDesired = FileReadData | FileWriteData | FileReadAttributes | Synchronize;
    /// <summary>FILE_ALL_ACCESS, the mask of the SDDL mnemonic FA used by the NETWORK deny entry.</summary>
    public const uint FileAllAccess = 0x1F01FF;
    /// <summary>FILE_GENERIC_WRITE (includes 0x4 and 0x100) which a generic read/write open maps to; refused by design.</summary>
    public const uint FileGenericWrite = 0x120116;
}

public enum PipeAceKind { Deny, Allow }

/// <summary>One DACL entry of the pipe descriptor. <c>Sid</c> is the numeric form; <c>SddlSid</c> is the token written in the SDDL string.</summary>
public sealed record PipeAce(PipeAceKind Kind, uint Mask, string Sid, string SddlSid);

/// <summary>Parsed structure of the pipe descriptor. Equality of the sequence is what <see cref="PipeSecurity.VerifyWithWindows"/> checks against the binary form.</summary>
public sealed record PipeDescriptorStructure(bool DaclProtected, IReadOnlyList<PipeAce> Dacl, bool HasMediumNoWriteUpLabel);

/// <summary>Outcome of the in-memory conversion round trip (no object is created).</summary>
public sealed record PipeSecurityVerification(bool Ok, IReadOnlyList<string> Problems, byte[] Binary, string? RoundTripSddl);

/// <summary>Options that follow the "if X2 shows otherwise" notes of section 5.</summary>
public sealed record PipeSecurityOptions
{
    /// <summary>Add <c>(A;;0x00120083;;;BA)</c>. Default false: the creator needs no ACE (section 5, UNV C-10 X2).</summary>
    public bool IncludeAdministratorsAce { get; init; }
    /// <summary>Include the Medium no-write-up label. Default false: a pipe without a label already denies a Low-integrity client (section 5, C-11 X2).</summary>
    public bool IncludeIntegrityLabel { get; init; }
}

/// <summary>The built descriptor: verified string, parsed structure and the exact client masks.</summary>
public sealed class PipeSecurityDescriptor
{
    internal PipeSecurityDescriptor(string sddl, PipeDescriptorStructure structure, string logonSid)
    { Sddl = sddl; Structure = structure; LogonSid = logonSid; }

    public string Sddl { get; }
    public PipeDescriptorStructure Structure { get; }
    public string LogonSid { get; }
}

/// <summary>
/// Pipe descriptor builder: <c>D:P(D;;FA;;;NU)(A;;0x00120083;;;&lt;LOGON&gt;)</c>.
/// Pure logic plus one in-memory conversion check; it never creates a pipe.
/// </summary>
public static class PipeSecurity
{
    public static PipeSecurityDescriptor Build(string logonSid, PipeSecurityOptions? options = null)
    {
        options ??= new PipeSecurityOptions();
        if (!SidText.IsLogonSid(logonSid)) throw new ArgumentException("not a logon SID of the form S-1-5-5-X-Y", nameof(logonSid));
        var dacl = new List<PipeAce>
        {
            new(PipeAceKind.Deny, PipeRights.FileAllAccess, SidText.Network, "NU"),
            new(PipeAceKind.Allow, PipeRights.ClientGranted, logonSid, logonSid),
        };
        if (options.IncludeAdministratorsAce) dacl.Add(new PipeAce(PipeAceKind.Allow, PipeRights.ClientGranted, SidText.Administrators, "BA"));
        var sb = new System.Text.StringBuilder("D:P", 128);
        foreach (var a in dacl)
        {
            sb.Append('(').Append(a.Kind == PipeAceKind.Deny ? 'D' : 'A').Append(";;");
            sb.Append(a.Kind == PipeAceKind.Deny ? "FA" : "0x" + a.Mask.ToString("X8", CultureInfo.InvariantCulture));
            sb.Append(";;;").Append(a.SddlSid).Append(')');
        }
        if (options.IncludeIntegrityLabel) sb.Append("S:(ML;;NW;;;ME)");
        return new PipeSecurityDescriptor(sb.ToString(), new PipeDescriptorStructure(true, dacl, options.IncludeIntegrityLabel), logonSid);
    }

    /// <summary>
    /// Converts the SDDL to a binary descriptor with the system converter (in memory only), parses it, compares every entry with the
    /// structure, and converts back to text. Returns the self-relative binary form for the package that makes the pipe.
    /// </summary>
    public static PipeSecurityVerification VerifyWithWindows(PipeSecurityDescriptor descriptor)
    {
        var problems = new List<string>();
        if (!OperatingSystem.IsWindows()) return new PipeSecurityVerification(false, new[] { "platform" }, Array.Empty<byte>(), null);
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor.Sddl, 1, out nint sd, out uint size))
            return new PipeSecurityVerification(false, new[] { "convert_failed_" + System.Runtime.InteropServices.Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) }, Array.Empty<byte>(), null);
        try
        {
            var binary = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(sd, binary, 0, (int)size);
            string? roundTrip = null;
            // DACL | SACL | LABEL information: owner and group are not part of the descriptor (a pipe creator's token supplies them).
            if (Native.ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, 4 | 8 | 16, out nint text, out _))
            {
                try { roundTrip = System.Runtime.InteropServices.Marshal.PtrToStringUni(text); }
                finally { Native.LocalFree(text); }
            }
            else problems.Add("convert_back_failed");

            if (!SecurityDescriptorParser.TryParse(binary, out var info) || info is null) problems.Add("parse_failed");
            else
            {
                if (info.OwnerSid is not null || info.GroupSid is not null) problems.Add("unexpected_owner_or_group");
                if (info.DaclProtected != descriptor.Structure.DaclProtected) problems.Add("dacl_protection");
                var dacl = info.Dacl;
                if (dacl is null || dacl.Count != descriptor.Structure.Dacl.Count) problems.Add("dacl_count");
                else
                    for (int i = 0; i < dacl.Count; i++)
                    {
                        var want = descriptor.Structure.Dacl[i];
                        byte type = want.Kind == PipeAceKind.Deny ? AceTypes.AccessDenied : AceTypes.AccessAllowed;
                        if (dacl[i].Type != type || dacl[i].Mask != want.Mask || dacl[i].Sid != want.Sid || dacl[i].Flags != 0) problems.Add("dace_" + i.ToString(CultureInfo.InvariantCulture));
                    }
                bool label = info.Sacl.Count == 1 && info.Sacl[0].Type == AceTypes.MandatoryLabel && info.Sacl[0].Sid == SidText.MediumIntegrity && info.Sacl[0].Mask == 1;
                if (descriptor.Structure.HasMediumNoWriteUpLabel != label) problems.Add("label");
                if (!descriptor.Structure.HasMediumNoWriteUpLabel && info.Sacl.Count != 0) problems.Add("unexpected_sacl");
            }
            return new PipeSecurityVerification(problems.Count == 0, problems, binary, roundTrip);
        }
        finally { Native.LocalFree(sd); }
    }

    /// <summary>
    /// Pure model of the access check for a pipe client (U-S04 analogue): walks the DACL in order for a token that holds
    /// <paramref name="tokenSids"/>, a deny entry matching a requested bit refuses, allow entries accumulate. When the descriptor carries the
    /// Medium no-write-up label, callers below Medium lose every write bit. Returns true when every requested bit is granted.
    /// </summary>
    public static bool IsAccessGranted(PipeDescriptorStructure structure, IReadOnlySet<string> tokenSids, uint desiredAccess, uint callerIntegrityRid = 0x2000)
    {
        const uint writeBits = PipeRights.FileWriteData | PipeRights.FileCreatePipeInstance | 0x10 | PipeRights.FileWriteAttributes | 0x40000 | 0x80000 | 0x10000;
        uint desired = desiredAccess;
        if (structure.HasMediumNoWriteUpLabel && callerIntegrityRid < 0x2000 && (desired & writeBits) != 0) return false;
        uint granted = 0;
        foreach (var ace in structure.Dacl)
        {
            if (!tokenSids.Contains(ace.Sid)) continue;
            if (ace.Kind == PipeAceKind.Deny) { if ((ace.Mask & desired & ~granted) != 0) return false; }
            else granted |= ace.Mask;
        }
        return (desired & ~granted) == 0;
    }
}

/// <summary>Everything CreateNamedPipe needs, as data (section 5). Another package turns this into the real call.</summary>
public sealed record PipeCreationPlan
{
    public const uint PipeAccessDuplex = 0x3, FileFlagFirstPipeInstance = 0x00080000, FileFlagOverlapped = 0x40000000;
    public const uint PipeTypeByte = 0x0, PipeReadModeByte = 0x0, PipeWait = 0x0, PipeRejectRemoteClients = 0x8;

    public required string Name { get; init; }
    public required uint OpenMode { get; init; }
    public required uint PipeMode { get; init; }
    public required uint MaxInstances { get; init; }
    public required uint OutBufferBytes { get; init; }
    public required uint InBufferBytes { get; init; }
    /// <summary>Default wait timeout; 0 selects the system default of 50 ms (section 5 specifies none).</summary>
    public required uint DefaultTimeoutMs { get; init; }
    public required PipeSecurityDescriptor Security { get; init; }
    /// <summary>The security attributes are never inheritable (section 5).</summary>
    public bool InheritHandle => false;

    /// <summary>Pipe name per section 5: <c>\\.\pipe\Winora.Elev.&lt;R&gt;</c> (VmTrust: <c>Winora.VmElev</c>), R as 32 lowercase hex characters.</summary>
    public static string NameFor(Bytes16 rendezvous, ElevationFlavor flavor) =>
        @"\\.\pipe\" + (flavor == ElevationFlavor.VmTrust ? "Winora.VmElev." : "Winora.Elev.") + rendezvous.ToHex();

    public static PipeCreationPlan For(Bytes16 rendezvous, ElevationFlavor flavor, string logonSid, PipeSecurityOptions? options = null) => new()
    {
        Name = NameFor(rendezvous, flavor),
        OpenMode = PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
        PipeMode = PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
        MaxInstances = 1,
        OutBufferBytes = ElevationLimits.MaxFrameBytes,
        InBufferBytes = ElevationLimits.MaxFrameBytes,
        DefaultTimeoutMs = 0,
        Security = PipeSecurity.Build(logonSid, options),
    };
}
