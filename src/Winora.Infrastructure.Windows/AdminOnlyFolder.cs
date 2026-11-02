// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// A folder that only SYSTEM and Administrators can change. The elevated app keeps the records that it later acts on (the cleanup, power and optimize journals)
/// here, so a program running as the user cannot forge a line that makes an undo or a delete act on something the user did not change, and cannot swap a link into the path.
/// The folder is created with a protected ACL; an existing folder is accepted only when its owner and every write-class right belong to a trusted principal and it is not a link.
/// Every folder above it, up to the root of the volume, must also have a trusted owner and must not let anyone else delete, rename or re-permission it or its entries, so the folder cannot be
/// moved away and replaced through a parent. A folder that fails the check is refused (the callers then do nothing), never repaired in place.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AdminOnlyFolder
{
    private const FileSystemRights WriteClass = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // An entry can be swapped through a parent folder only by deleting or renaming it, or by changing who may do that.
    private const FileSystemRights AncestorClass = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const FileSystemRights FileWriteClass = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Generic bits are not mapped by the ACL classes; an entry can still carry them. GENERIC_ALL gives every right, GENERIC_WRITE the write rights of a file.
    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    private static FileSystemRights Effective(FileSystemRights rights)
    {
        var bits = (int)rights;
        if ((bits & GenericAll) != 0) return (FileSystemRights)0x1F01FF;
        if ((bits & GenericWrite) != 0) rights |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes;
        return rights;
    }

    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>The folder for the elevated records of the machine: <c>%ProgramData%\Winora\journals</c>.</summary>
    public static string DefaultPath() => Path.Combine(AppRoot(), "journals");

    /// <summary>The folder the app owns in the machine data folder: <c>%ProgramData%\Winora</c>. Every admin-only folder of the app is below it.</summary>
    public static string AppRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Winora");

    /// <summary>
    /// Runs once at app start, before any record is read. A missing app root is created with the protected ACL. An app root that exists but is not admin-only (a user or a program made it
    /// first, or it is a link) is never used and never repaired in place: it is renamed to <c>Winora.untrusted-&lt;UTC time&gt;</c> in the same parent, so nothing in it is lost, and a new
    /// protected folder is made. When a folder above the root is not protected, nothing is moved or made and the result is refused: a new folder there could be swapped too.
    /// </summary>
    /// <param name="root">The app root. Production passes none (<see cref="AppRoot"/>).</param>
    /// <param name="extraTrusted">Principals treated like Administrators. Production passes none; tests pass the test user.</param>
    public static RootPreparation PrepareRoot(string? root = null, IEnumerable<SecurityIdentifier>? extraTrusted = null, DateTimeOffset? now = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? AppRoot()));
        var trusted = Trusted(extraTrusted);
        try { VerifyAncestors(full, trusted); }
        catch (AdminOnlyFolderException e) { return new(RootOutcome.Refused, full, null, e.Problem); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(RootOutcome.Refused, full, null, AdminOnlyProblem.ParentRights); }
        if (!EntryExists(full)) return Create(full, null, null, extraTrusted);
        AdminOnlyProblem? problem = null;
        try { VerifyOwn(full, trusted); }
        catch (AdminOnlyFolderException e) { problem = e.Problem; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = AdminOnlyProblem.Rights; }
        if (problem is null) return new(RootOutcome.Ready, full, null, null);
        var stamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var aside = full + ".untrusted-" + stamp;
        for (var i = 2; EntryExists(aside); i++) aside = full + ".untrusted-" + stamp + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // A link is renamed itself; its target is not touched.
        try { Directory.Move(full, aside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(RootOutcome.Refused, full, null, AdminOnlyProblem.MoveFailed); }
        return Create(full, aside, problem, extraTrusted);
    }

    private static RootPreparation Create(string full, string? aside, AdminOnlyProblem? problem, IEnumerable<SecurityIdentifier>? extraTrusted)
    {
        // Ensure checks the new folder again, so a folder someone made in between is refused, not used.
        try { Ensure(full, extraTrusted); }
        catch (AdminOnlyFolderException e) { return new(RootOutcome.Refused, full, aside, e.Problem); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(RootOutcome.Refused, full, aside, AdminOnlyProblem.CreateFailed); }
        return new(aside is null ? RootOutcome.Created : RootOutcome.Replaced, full, aside, problem);
    }

    // A dangling link or a plain file is an entry too, although Directory.Exists says no.
    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Creates the folder when it is missing and returns its full path; throws <see cref="IOException"/> when the folder or a folder above it is not protected. Missing folders above it
    /// (such as <c>%ProgramData%\Winora</c>) are created with the same protected ACL, so a folder the app makes is never open to the users who may create entries in the parent.
    /// </summary>
    /// <param name="extraTrusted">Principals treated like Administrators. Production passes none; tests pass the test user so they can clean up.</param>
    public static string Ensure(string path, IEnumerable<SecurityIdentifier>? extraTrusted = null)
    {
        var full = Path.GetFullPath(path);
        var trusted = Trusted(extraTrusted);
        var missing = new Stack<string>();
        for (var folder = full; !string.IsNullOrEmpty(folder) && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder)) missing.Push(folder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in trusted.Where(s => s != TrustedInstaller && s != CreatorOwner))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        while (missing.TryPop(out var folder))
        {
            try { new DirectoryInfo(folder).Create(security); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { throw new AdminOnlyFolderException(AdminOnlyProblem.CreateFailed, "The folder for the records could not be created.", e); }
        }
        Verify(full, trusted);
        return full;
    }

    /// <summary>True when only trusted principals can change the folder, the files directly in it, its own name and the folders above it. Used for the install folder of the app as well.</summary>
    public static bool IsProtected(string path, IEnumerable<SecurityIdentifier>? extraTrusted = null)
    {
        try { Verify(Path.GetFullPath(path), Trusted(extraTrusted)); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// True when only trusted principals own the file and can write, delete or re-permission it. With <paramref name="open"/> the check reads the security of that open handle,
    /// so it is about the file that will be used, not about whatever the path names a moment later.
    /// </summary>
    public static bool IsProtectedFile(string path, FileStream? open = null, IEnumerable<SecurityIdentifier>? extraTrusted = null)
    {
        try
        {
            var trusted = Trusted(extraTrusted);
            var security = open is not null ? open.GetAccessControl() : new FileInfo(Path.GetFullPath(path)).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (Effective(rule.FileSystemRights) & FileWriteClass) == 0) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || !Allowed(sid, trusted)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException or NotSupportedException) { return false; }
    }

    // Package and capability entries (S-1-15-2, S-1-15-3) grant nothing alone: an app container passes the check only when its user or one of its groups is allowed too.
    private static bool Allowed(SecurityIdentifier sid, HashSet<SecurityIdentifier> trusted) =>
        trusted.Contains(sid) || sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-15-3-", StringComparison.Ordinal);

    private static HashSet<SecurityIdentifier> Trusted(IEnumerable<SecurityIdentifier>? extra) =>
        new([System_, Administrators, CreatorOwner, TrustedInstaller, .. extra ?? []]);

    private static void Verify(string full, HashSet<SecurityIdentifier> trusted)
    {
        VerifyOwn(full, trusted);
        VerifyAncestors(full, trusted);
    }

    private static void VerifyOwn(string full, HashSet<SecurityIdentifier> trusted)
    {
        var info = new DirectoryInfo(full);
        if (!info.Exists) throw new AdminOnlyFolderException(AdminOnlyProblem.Missing, "The folder does not exist.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new AdminOnlyFolderException(AdminOnlyProblem.Link, "The folder is a link.");
        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) throw new AdminOnlyFolderException(AdminOnlyProblem.Owner, "The folder has an owner that is not trusted.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (Effective(rule.FileSystemRights) & WriteClass) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !Allowed(sid, trusted)) throw new AdminOnlyFolderException(AdminOnlyProblem.Rights, "The folder can be changed by a principal that is not trusted.");
        }
    }

    /// <summary>Each folder above <paramref name="full"/> up to the volume root: not a link, a trusted owner, and no right of another principal to delete, rename or re-permission it or its entries.</summary>
    private static void VerifyAncestors(string full, HashSet<SecurityIdentifier> trusted)
    {
        for (var folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            // A folder that does not exist yet is made later with the protected ACL and checked then.
            if (!EntryExists(folder)) continue;
            var info = new DirectoryInfo(folder);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new AdminOnlyFolderException(AdminOnlyProblem.ParentLink, "A folder above is a link.");
            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) throw new AdminOnlyFolderException(AdminOnlyProblem.ParentOwner, "A folder above has an owner that is not trusted.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                // An inherit-only entry applies to new children, not to this folder.
                if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) || (Effective(rule.FileSystemRights) & AncestorClass) == 0) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || !Allowed(sid, trusted)) throw new AdminOnlyFolderException(AdminOnlyProblem.ParentRights, "A folder above can be changed by a principal that is not trusted.");
            }
        }
    }

    /// <summary>The profile folder of the user this process runs as, read from the token (the profile list in the machine registry), not from environment variables or the user's own shell folder settings.</summary>
    public static string? ProfileFolderOfToken()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var length = 520u;
            var builder = new StringBuilder((int)length);
            return GetUserProfileDirectory(identity.Token, builder, ref length) ? builder.ToString() : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or InvalidOperationException) { return null; }
    }

    [DllImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder path, ref uint size);
}

/// <summary>Why an admin-only folder is refused. The Parent values are about a folder above it.</summary>
public enum AdminOnlyProblem { Missing, Link, Owner, Rights, ParentLink, ParentOwner, ParentRights, CreateFailed, MoveFailed }

/// <summary>An admin-only folder that is refused, with the reason. It is an <see cref="IOException"/>, so callers that only know that type still do nothing.</summary>
public sealed class AdminOnlyFolderException(AdminOnlyProblem problem, string message, Exception? inner = null) : IOException(message, inner)
{
    public AdminOnlyProblem Problem { get; } = problem;
}

public enum RootOutcome { Ready, Created, Replaced, Refused }

/// <summary>What <see cref="AdminOnlyFolder.PrepareRoot"/> did. <see cref="MovedTo"/> is the new name of a root that was not admin-only; <see cref="Problem"/> is why it was moved or refused.</summary>
public sealed record RootPreparation(RootOutcome Outcome, string Root, string? MovedTo, AdminOnlyProblem? Problem)
{
    public bool Usable => Outcome != RootOutcome.Refused;

    /// <summary>True when a folder above the root is the reason. The app never changes such a folder.</summary>
    public bool ParentProblem => Problem is AdminOnlyProblem.ParentLink or AdminOnlyProblem.ParentOwner or AdminOnlyProblem.ParentRights;
}
