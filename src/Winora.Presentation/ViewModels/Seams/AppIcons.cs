// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

public enum AppIconKind { Package, File }

/// <summary>What to show an icon for: the install folder of a Store package, or the program file of a desktop program.</summary>
public readonly record struct AppIconKey(AppIconKind Kind, string Path)
{
    public string CacheKey => $"{Kind}|{Path.ToUpperInvariant()}";
}

/// <summary>Read-only source of app icons, as the view models see it: the image is an opaque value that the view binds to an image control. A failure is a null result, never an exception.</summary>
public interface IAppIconSource
{
    /// <summary>The icon when it is already loaded (also a known failure, as null); false while it is not known yet.</summary>
    bool TryGet(AppIconKey key, out object? icon);

    /// <summary>Loads the icon off the UI thread.</summary>
    Task<object?> GetAsync(AppIconKey key, CancellationToken cancellationToken = default);
}

/// <summary>The icon of one row. Source is null (letter badge) until the icon is loaded, and stays null when there is none.</summary>
public sealed class AppIconLoader : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private object? source;

    public AppIconLoader(IAppIconSource? service, AppIconKey? key)
    {
        if (service is null || key is null) return;
        if (service.TryGet(key.Value, out var ready)) { source = ready; return; }
        _ = LoadAsync(service, key.Value);
    }

    public object? Source { get => source; private set => SetProperty(ref source, value); }

    private async Task LoadAsync(IAppIconSource service, AppIconKey key)
    {
        try { Source = await service.GetAsync(key); }
        catch (Exception) { /* the letter badge stays */ }
    }
}
