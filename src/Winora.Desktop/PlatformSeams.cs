// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;

namespace Winora.Desktop;

/// <summary>Standard Windows folder dialog (Microsoft.Win32.OpenFolderDialog). It only returns a path; it creates and changes nothing.</summary>
public sealed class Win32FolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return Task.FromResult(accepted == true && !string.IsNullOrWhiteSpace(dialog.FolderName) ? dialog.FolderName : null);
    }
}

public sealed class Win32ProfileFileAccess : IProfileFileAccess
{
    private const string Filter = "Winora profile (*.winora-profile.json)|*.winora-profile.json|JSON (*.json)|*.json";

    public Task<ProfileFileRead> ReadAsync(string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = Filter, CheckFileExists = true, Multiselect = false };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (accepted != true || string.IsNullOrWhiteSpace(dialog.FileName)) return Task.FromResult(ProfileFileRead.Cancel);
        try
        {
            using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > Winora.Application.ProfileFile.MaxBytes) return Task.FromResult(new ProfileFileRead(null, false, true, false));
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Task.FromResult(new ProfileFileRead(new System.Text.UTF8Encoding(false, true).GetString(bytes), false, false, false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new ProfileFileRead(null, false, false, true));
        }
    }

    public Task<bool?> WriteAsync(string title, string suggestedName, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = Filter, FileName = suggestedName, AddExtension = true, DefaultExt = ".winora-profile.json", OverwritePrompt = true };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (accepted != true || string.IsNullOrWhiteSpace(dialog.FileName)) return Task.FromResult<bool?>(null);
        try
        {
            Winora.Infrastructure.Persistence.ReplaceFile.WriteAllBytes(dialog.FileName, new System.Text.UTF8Encoding(false).GetBytes(text));
            return Task.FromResult<bool?>(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return Task.FromResult<bool?>(false); }
    }
}

public sealed class SystemClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        try { Clipboard.SetDataObject(text, true); return true; }
        catch (System.Runtime.InteropServices.ExternalException ex) { System.Diagnostics.Trace.TraceWarning("Clipboard unavailable: {0}", ex.GetType().Name); return false; }
    }
}

/// <summary>
/// The one place that starts the shell for a link (decision DEC-IW-028): the scheme and host are fixed, and the only variable part is a package family name that must match the Windows
/// format (name, underscore, 13 characters) exactly, so nothing else can reach the shell.
/// </summary>
public sealed partial class ShellStoreLauncher : IStoreLauncher
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.\\-]{1,49}_[a-z0-9]{13}$")]
    private static partial System.Text.RegularExpressions.Regex FamilyName();

    public static string? LinkFor(string packageFamilyName) =>
        packageFamilyName is not null && FamilyName().IsMatch(packageFamilyName) ? "ms-windows-store://pdp/?PFN=" + packageFamilyName : null;

    public bool Open(string packageFamilyName)
    {
        if (LinkFor(packageFamilyName) is not { } link) return false;
        try
        {
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { return false; }
    }
}

/// <summary>The one fixed link <c>ms-settings:windowsupdate</c>: no part of it comes from data, so nothing else can reach the shell (decision DEC-IW-150).</summary>
public sealed class ShellSettingsLauncher : IWindowsSettingsLauncher
{
    public const string WindowsUpdateLink = "ms-settings:windowsupdate";

    public bool OpenWindowsUpdate()
    {
        try
        {
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WindowsUpdateLink) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>The program file name is fixed and lives in the system folder; no argument and no data reaches the shell.</summary>
    public const string SystemProtectionProgram = "SystemPropertiesProtection.exe";

    public bool OpenSystemProtection()
    {
        try
        {
            var path = System.IO.Path.Combine(Environment.SystemDirectory, SystemProtectionProgram);
            if (!System.IO.File.Exists(path)) return false;
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or System.IO.IOException) { return false; }
    }
}

/// <summary>Honours the Windows "show animations" setting. No essential information is ever carried by an animation alone.</summary>
public static class MotionPolicy
{
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;
}

/// <summary>
/// The high-contrast resource path. While Windows high contrast is on, Styles/HighContrast.xaml is merged last so every theme brush
/// follows the user's system colors (no fixed colors, no Mica). It is removed again when high contrast is switched off.
/// </summary>
public static class HighContrastResources
{
    public static readonly Uri Source = new("pack://application:,,,/Winora.Desktop;component/Styles/HighContrast.xaml");
    public static ResourceDictionary Load() => new() { Source = Source };
}

/// <summary>
/// Light and dark overlays (Styles/ThemeLight.xaml, ThemeDark.xaml) merged after the WPF-UI theme: Winora's contrast-checked tokens for
/// UI-component borders and badges. High contrast uses its own dictionary instead.
/// </summary>
public static class ThemeOverlay
{
    private static ResourceDictionary? active;

    public static bool IsHighContrast { get; private set; }
    public static bool IsActive => active is not null;

    public static void Apply(bool dark, bool highContrast)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (active is not null) { merged.Remove(active); active = null; }
        IsHighContrast = highContrast;
        var file = highContrast ? "HighContrast" : dark ? "ThemeDark" : "ThemeLight";
        active = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Winora.Desktop;component/Styles/{file}.xaml") };
        merged.Add(active);
        if (!highContrast) return;
        // WPF-UI writes the accent brushes straight into the application dictionary, which outranks merged dictionaries. In high contrast they are
        // dropped so the system-color versions from the overlay win; the next normal theme apply writes them again.
        var root = System.Windows.Application.Current.Resources;
        foreach (var key in active.Keys.Cast<object>().ToList())
            if (root.Contains(key)) root.Remove(key);
    }
}

/// <summary>Where the "text size" the user chose in Windows Accessibility comes from. Tests replace it so they never read the real settings.</summary>
public interface ITextScaleSource
{
    /// <summary>1.0 for the default size up to 2.25 (225%).</summary>
    double Factor { get; }
    event EventHandler? Changed;
}

public static class TextScale
{
    public const double Minimum = 1.0, Maximum = 2.25;
    /// <summary>Converts the Windows percentage (100 to 225) to a factor; anything missing, non-numeric or out of range is clamped.</summary>
    public static double FromPercent(object? percent) =>
        percent is int value ? Math.Clamp(value / 100d, Minimum, Maximum) : Minimum;
}

/// <summary>Reads (never writes) HKCU\Software\Microsoft\Accessibility\TextScaleFactor, the value behind Settings > Accessibility > Text size.</summary>
public sealed class WindowsTextScaleSource : ITextScaleSource, IDisposable
{
    public event EventHandler? Changed;
    public WindowsTextScaleSource() => Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    public double Factor
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility", writable: false);
                return TextScale.FromPercent(key?.GetValue("TextScaleFactor"));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return TextScale.Minimum;
            }
        }
    }
    private void OnPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) { if (e.Category is Microsoft.Win32.UserPreferenceCategory.Accessibility or Microsoft.Win32.UserPreferenceCategory.General) Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose() => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
