// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Winora.Desktop;

/// <summary>The icon source with the WPF image type. A failure is a null result, never an exception: the row keeps its letter badge.</summary>
public interface IAppIconService : IAppIconSource
{
    /// <summary>The icon when it is already loaded (also a known failure, as null); false while it is not known yet.</summary>
    bool TryGet(AppIconKey key, out ImageSource? icon);

    /// <summary>Loads the icon off the UI thread. The result is frozen, so any thread may use it.</summary>
    new Task<ImageSource?> GetAsync(AppIconKey key, CancellationToken cancellationToken = default);

    bool IAppIconSource.TryGet(AppIconKey key, out object? icon)
    {
        var known = TryGet(key, out ImageSource? image);
        icon = image;
        return known;
    }

    async Task<object?> IAppIconSource.GetAsync(AppIconKey key, CancellationToken cancellationToken) => await GetAsync(key, cancellationToken).ConfigureAwait(false);
}

/// <summary>The icon of a program file, as encoded PNG bytes. Seam for tests and for the Windows shell.</summary>
public interface IExecutableIconSource
{
    byte[]? ReadPng(string path);
}

/// <summary>
/// Reads icons without starting anything. A Store package: the logo named in the manifest of its install folder, the best file variant that exists, a PNG only, never outside the folder.
/// A desktop program: the icon resource of an existing file. Results, also failures, are cached by key.
/// </summary>
public sealed class AppIconService(IExecutableIconSource executables) : IAppIconService
{
    public const int DecodePixels = 64;
    internal const long MaxManifestBytes = 1024 * 1024;
    internal const long MaxImageBytes = 4 * 1024 * 1024;
    private const int TargetPixels = 64;

    private readonly ConcurrentDictionary<string, Task<ImageSource?>> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(4);

    public bool TryGet(AppIconKey key, out ImageSource? icon)
    {
        if (cache.TryGetValue(key.CacheKey, out var task) && task.IsCompletedSuccessfully) { icon = task.Result; return true; }
        icon = null; return false;
    }

    public Task<ImageSource?> GetAsync(AppIconKey key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key.Path)) return Task.FromResult<ImageSource?>(null);
        return cache.GetOrAdd(key.CacheKey, _ => Task.Run(() => LoadGuarded(key), CancellationToken.None));
    }

    private async Task<ImageSource?> LoadGuarded(AppIconKey key)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return key.Kind == AppIconKind.Package ? LoadPackage(key.Path) : LoadFile(key.Path); }
        catch (Exception) { return null; }
        finally { gate.Release(); }
    }

    private ImageSource? LoadFile(string path)
    {
        if (!IsLocalAbsolute(path) || !File.Exists(path)) return null;
        var png = executables.ReadPng(path);
        return png is { Length: > 0 } ? Decode(png) : null;
    }

    private static ImageSource? LoadPackage(string installFolder)
    {
        if (!IsLocalAbsolute(installFolder) || !Directory.Exists(installFolder)) return null;
        var root = Path.GetFullPath(installFolder);
        var manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > MaxManifestBytes) return null;
        var logo = LogoPath(manifest);
        if (logo is null) return null;
        var file = BestVariant(root, logo);
        if (file is null || new FileInfo(file).Length > MaxImageBytes) return null;
        return Decode(File.ReadAllBytes(file));
    }

    /// <summary>The logo path of the manifest: Square44x44Logo of the first application, else the package Logo. Null when the manifest is not readable.</summary>
    public static string? LogoPath(string manifestFile)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxManifestBytes };
            using var reader = XmlReader.Create(manifestFile, settings);
            var document = XDocument.Load(reader);
            string? Attribute(string element, string attribute) => document.Descendants().Where(e => e.Name.LocalName == element).Select(e => (string?)e.Attribute(attribute)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var small = Attribute("VisualElements", "Square44x44Logo");
            if (small is not null) return small;
            return document.Descendants().Where(e => e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The PNG to load for a manifest logo path: the exact file or a scale or targetsize variant of it, whichever is closest above the wanted size.
    /// Every candidate must stay inside the install folder. Variants for an alternate form (unplated, light theme) are used only when nothing else exists.
    /// </summary>
    public static string? BestVariant(string root, string logoPath)
    {
        var relative = logoPath.Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!IsInside(root, full)) return null;
        var directory = Path.GetDirectoryName(full);
        var baseName = Path.GetFileNameWithoutExtension(full);
        if (directory is null || !Directory.Exists(directory)) return null;

        var candidates = new List<(string File, double Pixels, bool Alt)>();
        foreach (var file in Directory.EnumerateFiles(directory, baseName + "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.Equals(baseName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsInside(root, Path.GetFullPath(file)) || IsReparsePoint(file)) continue;
            var (pixels, alt) = Describe(name[baseName.Length..]);
            candidates.Add((file, pixels, alt));
        }
        var pool = candidates.Any(c => !c.Alt) ? candidates.Where(c => !c.Alt).ToList() : candidates;
        if (pool.Count == 0) return null;
        var enough = pool.Where(c => c.Pixels >= TargetPixels).OrderBy(c => c.Pixels).ToList();
        return (enough.Count > 0 ? enough[0] : pool.OrderByDescending(c => c.Pixels).First()).File;
    }

    /// <summary>The pixel size a variant suffix stands for (".scale-200" is 200 percent of 44, ".targetsize-48" is 48) and whether it is an alternate form.</summary>
    public static (double Pixels, bool Alt) Describe(string suffix)
    {
        var alt = suffix.Contains("altform", StringComparison.OrdinalIgnoreCase);
        double pixels = 44;
        foreach (var part in suffix.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("scale-", StringComparison.OrdinalIgnoreCase) && double.TryParse(part[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var scale)) pixels = 44 * scale / 100;
            else if (part.StartsWith("targetsize-", StringComparison.OrdinalIgnoreCase) && double.TryParse(part[11..], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) pixels = size;
        }
        return (pixels, alt);
    }

    public static bool IsInside(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string file) => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0;

    private static bool IsLocalAbsolute(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static ImageSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = DecodePixels;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>
/// Reads the icon of a program with the shell icon extraction (System.Drawing), the same call Explorer-style lists use. Starts nothing; the file is only read.
/// </summary>
public sealed class ShellExecutableIcons : IExecutableIconSource
{
    public byte[]? ReadPng(string path)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
}
