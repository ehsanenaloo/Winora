// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using Winora.Domain;
namespace Winora.Application;

/// <summary>One file an evidence source says belongs to an installation, with the kind of proof and where it came from (an installer manifest, an install log, a component record, an exact package identity).</summary>
public sealed record OwnershipClaim(string Path, OwnershipBasis Basis, string Provenance);

/// <summary>Read-only source of ownership claims. A source must not infer ownership from names or publishers; those claims carry a weak basis and never reach Confirmed.</summary>
public interface IOwnershipEvidenceSource
{
    IReadOnlyList<OwnershipClaim> Claims(InstallationIdentity installation, CancellationToken cancellationToken);
}

public enum LeftoverPathClass { Cleanable, Shared, UserData, Protected, Unrecognized }

public interface ILeftoverPathPolicy
{
    /// <param name="canonicalPath">A full, normalized path.</param>
    /// <param name="installLocation">The recorded install folder of the installation, if any; a path inside it counts as a recognized location.</param>
    /// <param name="otherInstallLocations">Install folders of other registered applications; a path inside one is shared.</param>
    LeftoverPathClass Classify(string canonicalPath, string? installLocation, IReadOnlyCollection<string> otherInstallLocations);
}

/// <summary>
/// Where a leftover file may be, by folder class (uninstall.md): the system folders and the roots themselves are protected, shared component folders are preserved,
/// user documents and roaming data need an explicit selection, and a path outside the known install roots and the recorded install folder is not recognized.
/// </summary>
public sealed class LeftoverPathPolicy : ILeftoverPathPolicy
{
    private readonly string windows, programData, userProfile, localAppData;
    private readonly string[] programFiles;
    private readonly string[] protectedRoots;

    public LeftoverPathPolicy(string windowsDirectory, IEnumerable<string> programFilesDirectories, string programDataDirectory, string userProfileDirectory, string localAppDataDirectory)
    {
        windows = Normalize(windowsDirectory);
        programFiles = programFilesDirectories.Select(Normalize).ToArray();
        programData = Normalize(programDataDirectory);
        userProfile = Normalize(userProfileDirectory);
        localAppData = Normalize(localAppDataDirectory);
        protectedRoots = [windows, .. programFiles, programData, userProfile, localAppData, Normalize(Path.Combine(userProfile, "AppData"))];
    }

    public static LeftoverPathPolicy FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public LeftoverPathClass Classify(string canonicalPath, string? installLocation, IReadOnlyCollection<string> otherInstallLocations)
    {
        var path = Normalize(canonicalPath);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || string.Equals(parent.TrimEnd('\\'), Path.GetPathRoot(path)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return LeftoverPathClass.Protected;
        if (Inside(path, windows)) return LeftoverPathClass.Protected;
        if (protectedRoots.Any(r => string.Equals(parent, r, StringComparison.OrdinalIgnoreCase))) return LeftoverPathClass.Protected;

        if (otherInstallLocations.Any(o => Usable(o) && Inside(path, Normalize(o)))) return LeftoverPathClass.Shared;
        if (HasSegment(path, "Common Files") || HasSegment(path, "Microsoft Shared")) return LeftoverPathClass.Shared;

        if (Inside(path, userProfile) && !Inside(path, localAppData)) return LeftoverPathClass.UserData;

        if (programFiles.Any(r => Inside(path, r)) || Inside(path, programData) || Inside(path, localAppData)) return LeftoverPathClass.Cleanable;
        if (Usable(installLocation) && Inside(path, Normalize(installLocation!)) && !protectedRoots.Any(r => string.Equals(Normalize(installLocation!), r, StringComparison.OrdinalIgnoreCase))) return LeftoverPathClass.Cleanable;
        return LeftoverPathClass.Unrecognized;
    }

    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Path.GetDirectoryName(Normalize(path)) is not null;

    internal static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool Inside(string path, string folder) => path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '\\';

    private static bool HasSegment(string path, string name) => path.Split('\\').Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
}

public static class LeftoverReason
{
    public const string None = "";
    public const string InvalidPath = "invalid_path";
    public const string ProtectedPath = "protected_path";
    public const string UnrecognizedLocation = "unrecognized_location";
    public const string Duplicate = "duplicate";
    public const string TooManyClaims = "too_many_claims";
}

/// <summary>The result for one claimed path. <see cref="Candidate"/> is null when the path was skipped; <see cref="Reason"/> says why (a <see cref="LeftoverReason"/> or a <see cref="QuarantineReason"/>).</summary>
public sealed record LeftoverFile(string Path, LeftoverCandidate? Candidate, FileEvidence? Evidence, string Reason);

/// <summary>
/// Read-only scan of the files an installation owns (uninstall.md). A file is only a Confirmed candidate when the claim has a strong basis, the path is in a cleanable
/// location, and the file is still there as a plain regular file whose evidence could be read. The scan hashes files and changes nothing; <see cref="IQuarantine.Move"/>
/// revalidates the evidence again before any move.
/// </summary>
public sealed class LeftoverScanner
{
    public const int MaxClaims = 5000;

    private readonly IOwnershipEvidenceSource source;
    private readonly ILeftoverPathPolicy policy;
    private readonly IQuarantine quarantine;

    public LeftoverScanner(IOwnershipEvidenceSource source, ILeftoverPathPolicy policy, IQuarantine quarantine)
    {
        this.source = source; this.policy = policy; this.quarantine = quarantine;
    }

    public IReadOnlyList<LeftoverFile> Scan(InstallationIdentity installation, string? installLocation, IReadOnlyCollection<string> otherInstallLocations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var claims = source.Claims(installation, cancellationToken);
        var results = new List<LeftoverFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = installation.Scope == InstallationScope.Machine ? "machine" : "user";

        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= MaxClaims) { results.Add(new(claim.Path, null, null, LeftoverReason.TooManyClaims)); break; }
            if (!TryCanonical(claim.Path, out var path)) { results.Add(new(claim.Path, null, null, LeftoverReason.InvalidPath)); continue; }
            if (!seen.Add(path)) { results.Add(new(path, null, null, LeftoverReason.Duplicate)); continue; }

            var pathClass = policy.Classify(path, installLocation, otherInstallLocations);
            if (pathClass == LeftoverPathClass.Protected) { results.Add(new(path, null, null, LeftoverReason.ProtectedPath)); continue; }
            if (pathClass == LeftoverPathClass.Unrecognized) { results.Add(new(path, null, null, LeftoverReason.UnrecognizedLocation)); continue; }

            var (result, evidence) = quarantine.Inspect(path, cancellationToken);
            if (result.Reason == QuarantineReason.NotFound) { results.Add(new(path, null, null, QuarantineReason.NotFound)); continue; }

            var reparse = result.Reason is QuarantineReason.ReparsePoint or QuarantineReason.PathIndirection;
            var readable = evidence is not null;
            var strong = claim.Basis is OwnershipBasis.InstallationManifest or OwnershipBasis.InstallationLog or OwnershipBasis.InstallerComponent or OwnershipBasis.ExactPackageIdentity;
            var confidence = strong && readable && !reparse && !string.IsNullOrWhiteSpace(claim.Provenance) ? OwnershipConfidence.Confirmed
                : claim.Basis == OwnershipBasis.Unknown ? OwnershipConfidence.Unknown : OwnershipConfidence.Low;
            var candidate = new LeftoverCandidate(installation, new ResourceIdentity("file", scope, path.Replace('\\', '/')), confidence, claim.Provenance ?? "",
                Shared: pathClass == LeftoverPathClass.Shared, UserData: pathClass == LeftoverPathClass.UserData, ReparsePoint: reparse, claim.Basis);
            results.Add(new(path, candidate, evidence, readable ? LeftoverReason.None : result.Reason));
        }
        return results;
    }

    /// <summary>Fingerprint of the reviewed evidence, stored in <see cref="CleanupReview"/> so a later change of the set is detected.</summary>
    public static string Fingerprint(IEnumerable<LeftoverFile> files)
    {
        var lines = files.Where(f => f.Evidence is not null)
            .Select(f => $"{f.Evidence!.CanonicalPath.ToUpperInvariant()}|{f.Evidence.Fingerprint.VolumeSerial}|{f.Evidence.Fingerprint.FileIndex}|{f.Evidence.Fingerprint.Size}|{f.Evidence.Sha256.ToUpperInvariant()}")
            .OrderBy(l => l, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static bool TryCanonical(string? raw, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024 || raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal)) return false;
        if (raw.Any(char.IsControl) || !Path.IsPathFullyQualified(raw) || raw.IndexOf(':', 2) >= 0 || raw.EndsWith('\\') || raw.EndsWith('/')) return false;
        if (raw.Split('\\', '/').Any(s => s is ".." or "." || s.EndsWith('.') || s.EndsWith(' '))) return false;
        path = LeftoverPathPolicy.Normalize(raw);
        return true;
    }
}
