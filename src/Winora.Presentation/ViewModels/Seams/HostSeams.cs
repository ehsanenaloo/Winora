// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

// What the view models ask of the shell. The implementations are in Winora.Desktop (PlatformSeams.cs, WindowsSession.cs, WpfHost.cs); tests use fakes.

/// <summary>Seam for choosing a destination folder. The real implementation is the standard Win32 folder dialog; tests use a fake.</summary>
public interface IFolderPicker
{
    /// <summary>The chosen folder, or null when the user cancelled.</summary>
    Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken);
}

/// <summary>What the file dialog gave back when a profile file was opened.</summary>
public sealed record ProfileFileRead(string? Text, bool Cancelled, bool TooLarge, bool Failed)
{
    public static ProfileFileRead Cancel { get; } = new(null, true, false, false);
}

/// <summary>
/// Seam for the two file dialogs of profile export and import. The user picks the file in a standard Windows dialog; the text of a profile file is the only thing that is read or written.
/// Tests use a fake so no dialog opens and no file is touched.
/// </summary>
public interface IProfileFileAccess
{
    /// <summary>Opens a file dialog and reads the chosen file (at most <see cref="Winora.Application.ProfileFile.MaxBytes"/> bytes).</summary>
    Task<ProfileFileRead> ReadAsync(string title, CancellationToken cancellationToken);
    /// <summary>Opens a save dialog and writes the text. True when written, false when it failed, null when the user cancelled.</summary>
    Task<bool?> WriteAsync(string title, string suggestedName, string text, CancellationToken cancellationToken);
}

/// <summary>Seam for the OS clipboard so view-model tests never touch the shared clipboard.</summary>
public interface IClipboardService
{
    /// <summary>False when the clipboard is held by another process.</summary>
    bool TrySetText(string text);
}

/// <summary>Opens the Microsoft Store page of an app. The real implementation hands one fixed link to the shell; tests use a fake.</summary>
public interface IStoreLauncher : Winora.Application.IStoreInstaller
{
    /// <summary>False when the family name is not a package family name or the shell could not open the link.</summary>
    bool Open(string packageFamilyName);

    bool Winora.Application.IStoreInstaller.OpenAppPage(string packageFamilyName) => Open(packageFamilyName);
}

/// <summary>Opens the Windows Update page of Windows Settings. The real implementation hands one fixed link to the shell; tests use a fake.</summary>
public interface IWindowsSettingsLauncher
{
    /// <summary>False when the shell could not open the page.</summary>
    bool OpenWindowsUpdate();
    /// <summary>Opens the System Protection tab of the system properties (where a restore point can be turned on). False when the shell could not open it.</summary>
    bool OpenSystemProtection();
}

/// <summary>
/// The only session action Winora has: end the current user's session so sign-in-time settings take effect. A fixed action, never a
/// generic command. Callers must show an explicit confirmation first; tests use a fake and never call the real one.
/// </summary>
public interface IWindowsSession
{
    Task<bool> SignOutAsync();
}
