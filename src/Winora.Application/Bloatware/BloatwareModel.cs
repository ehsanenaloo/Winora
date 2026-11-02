// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

public enum PackageSignature { Unknown, None, Developer, Enterprise, Store, System }

/// <summary>One installed or provisioned Microsoft Store (MSIX) package. <see cref="DependencyFamilies"/> are the package families this package needs.</summary>
public sealed record AppPackage(
    string FullName, string FamilyName, string Name, string Publisher, string PublisherId, string Version, string Architecture, string DisplayName,
    string? InstallLocation, PackageSignature Signature, bool IsFramework, bool IsResource, bool IsBundle, bool IsOptional, bool IsDevelopmentMode,
    bool IsInstalled, bool InstalledForCurrentUser, bool IsProvisioned, IReadOnlyList<string> DependencyFamilies)
{
    /// <summary>True for a package that belongs to Windows itself: signed by the system or installed in the Windows folder. Windows refuses to remove most of these.</summary>
    public bool IsSystemOwned(string windowsFolder) =>
        Signature == PackageSignature.System
        || (InstallLocation is { Length: > 0 } location && windowsFolder.Length > 0 && location.StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
}

/// <summary>The packages of this PC. <see cref="AllUsers"/> is false when only the current user's packages could be read (no administrator rights); <see cref="ProvisionedKnown"/> is false when the provisioned list is not available.</summary>
public sealed record PackageSnapshot(IReadOnlyList<AppPackage> Packages, bool AllUsers, bool ProvisionedKnown, DateTimeOffset AtUtc);

public enum PackageInstallState { Unknown, NotInstalled, Staged, Installed, Paused }

/// <summary>One account that has a package, by security identifier.</summary>
public sealed record PackageUser(string Sid, PackageInstallState State);

/// <summary>The result of one call to the package API. <see cref="Code"/> is a stable code (see <see cref="BloatCode"/>); <see cref="Hresult"/> is the Windows error when there was one.</summary>
public sealed record PackageStepResult(bool Ok, string Code, int Hresult = 0)
{
    public static PackageStepResult Success { get; } = new(true, BloatCode.Ok);
    public static PackageStepResult Fail(string code, int hresult = 0) => new(false, code, hresult);
}

/// <summary>The package API of Windows. Reads work for the current user without rights; reading all users and every change need administrator rights.</summary>
public interface IAppPackages
{
    PackageSnapshot Read(CancellationToken cancellationToken);
    IReadOnlyList<PackageUser> ReadUsers(string fullName);
    /// <summary>The full names of the packages of this family that are installed for at least one user.</summary>
    IReadOnlyList<string> InstalledFullNames(string familyName);
    bool IsProvisioned(string familyName);
    PackageStepResult RemoveForAllUsers(string fullName);
    PackageStepResult Deprovision(string familyName);
    PackageStepResult RegisterStaged(string fullName);
    /// <summary>Closes every process of the package (the documented package debug interface, the same call as the Terminate button of Windows Settings). Only used when Windows says the package is in use.</summary>
    PackageStepResult StopApp(string fullName);
    /// <summary>The family names of the packages that are installed for at least one user, read once.</summary>
    IReadOnlySet<string> InstalledFamilies();
    /// <summary>Size of the package files in bytes, or null when they cannot be read. Never follows links.</summary>
    long? MeasureSize(string installLocation, CancellationToken cancellationToken);
    /// <summary>True when the folder exists and holds the package manifest.</summary>
    bool HasManifest(string installLocation);
    /// <summary>The Windows folder, used to recognise system packages.</summary>
    string WindowsFolder { get; }
}

/// <summary>Stable codes of package changes.</summary>
public static class BloatCode
{
    public const string Ok = "ok";
    public const string NeedsAdmin = "bloat.needs_admin";
    public const string UnknownPackage = "bloat.unknown_package";
    public const string Protected = "bloat.protected";
    public const string Framework = "bloat.framework";
    public const string SystemOwned = "bloat.system_owned";
    public const string HasDependents = "bloat.has_dependents";
    public const string NotInstalled = "bloat.not_installed";
    public const string AccessDenied = "bloat.access_denied";
    public const string InUse = "bloat.in_use";
    public const string RefusedByWindows = "bloat.refused_by_windows";
    public const string NotFound = "bloat.not_found";
    public const string Unsupported = "bloat.unsupported";
    public const string Failed = "bloat.failed";
    public const string VerifyFailed = "bloat.verify_failed";
    public const string JournalFailed = "bloat.journal_failed";
    public const string Dismissed = "bloat.dismissed";
    public const string NotUnconfirmed = "bloat.not_unconfirmed";
    public const string Cancelled = "bloat.cancelled";
    public const string CannotReinstall = "bloat.cannot_reinstall";
    public const string AlreadyInstalled = "bloat.already_installed";
    /// <summary>Windows itself does not allow this app to be removed on this PC, after every supported way was tried. The app is kept.</summary>
    public const string SystemForbids = "bloat.system_forbids";
    public const string StoreNotFinished = "bloat.store_not_finished";
    public const string StoreFailed = "bloat.store_failed";
}
