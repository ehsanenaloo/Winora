// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Winora.Domain;
namespace Winora.Infrastructure.Windows;

internal enum RegistryHiveKind { CurrentUser, LocalMachine }

/// <summary>One registry value as read: kind plus boxed data (string, byte[], int, long, string[]). Unreadable values carry Kind None.</summary>
internal sealed record RegistryValueData(RegistryDataKind Kind, object? Data)
{
    public string? AsString => Kind is RegistryDataKind.String or RegistryDataKind.ExpandString ? Data as string : null;
    public long? AsNumber => Kind switch { RegistryDataKind.DWord when Data is int i => unchecked((uint)i), RegistryDataKind.QWord when Data is long l => l, _ => null };
    public byte[]? AsBytes => Kind == RegistryDataKind.Binary ? Data as byte[] : null;
}

/// <summary>Read-only registry seam. Missing keys return null; denied access throws UnauthorizedAccessException or SecurityException.</summary>
internal interface IRegistryReader
{
    IReadOnlyList<string>? GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path);
    /// <summary>Values by name (case-insensitive). Expand strings are returned unexpanded.</summary>
    IReadOnlyDictionary<string, RegistryValueData>? GetValues(RegistryHiveKind hive, RegistryViewKind view, string path);
}

/// <summary>Opens keys with read access only (writable: false).</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryReader : IRegistryReader
{
    private const int MaxSubKeys = 100_000;
    private const int MaxValues = 10_000;

    private static RegistryKey? Open(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        using var root = RegistryKey.OpenBaseKey(hive == RegistryHiveKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
        return root.OpenSubKey(path, false);
    }

    public IReadOnlyList<string>? GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        using var key = Open(hive, view, path);
        return key?.GetSubKeyNames().Take(MaxSubKeys).ToArray();
    }

    public IReadOnlyDictionary<string, RegistryValueData>? GetValues(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        using var key = Open(hive, view, path);
        if (key is null) return null;
        var result = new Dictionary<string, RegistryValueData>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames().Take(MaxValues))
        {
            try
            {
                var kind = key.GetValueKind(name);
                var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                result[name] = new(Map(kind), data);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or InvalidCastException or ArgumentException)
            {
                result[name] = new(RegistryDataKind.None, null); // corrupt or unreadable value: surfaced as "not a usable value" by parsers
            }
        }
        return result;
    }

    private static RegistryDataKind Map(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => RegistryDataKind.String,
        RegistryValueKind.ExpandString => RegistryDataKind.ExpandString,
        RegistryValueKind.Binary => RegistryDataKind.Binary,
        RegistryValueKind.DWord => RegistryDataKind.DWord,
        RegistryValueKind.MultiString => RegistryDataKind.MultiString,
        RegistryValueKind.QWord => RegistryDataKind.QWord,
        _ => RegistryDataKind.None,
    };
}

internal static class HostReaders
{
    // The OperatingSystem.IsWindows() guard keeps these constructions safe (and analyzer-clean) on other platforms.
    public static IRegistryReader? Registry() => OperatingSystem.IsWindows() ? new RegistryReader() : null;
}

/// <summary>Shared limits, sanitizing and error classification for all discovery sources.</summary>
internal static class DiscoverySupport
{
    public const int DefaultMaxItems = 20_000;
    public const int MaxText = 4096;
    public const int MaxDetail = 80;

    public static string? Cap(string? text, int max = MaxText) => text is null ? null : text.Length <= max ? text : text[..max];

    /// <summary>Safe issue detail: control characters removed, length capped. Callers pass key names or ids only.</summary>
    public static string Detail(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.Where(c => !char.IsControl(c)).Take(MaxDetail).ToArray();
        return new string(chars);
    }

    public static DiscoveryIssue Issue(string source, string code, string? detail = null) =>
        new(source, WinoraError.From(code, detail is null ? null : Detail(detail)));

    public static string Classify(Exception e) => e switch
    {
        UnauthorizedAccessException or SecurityException => ErrorCode.AccessDenied,
        COMException c when c.HResult == unchecked((int)0x80070005) => ErrorCode.AccessDenied,
        Win32Exception w when w.NativeErrorCode == 5 => ErrorCode.AccessDenied,
        PlatformNotSupportedException => ErrorCode.DiscoveryUnavailable,
        _ => ErrorCode.DiscoveryItemUnreadable,
    };

    public static DiscoveryIssue FromException(string source, Exception e, string? detail) => Issue(source, Classify(e), detail);

    public static DiscoveryResult<T> Unavailable<T>(DateTimeOffset now) =>
        new([], [Issue(typeof(T).Name, ErrorCode.DiscoveryUnavailable)], now);

    public static bool IsCancellation(Exception e) => e is OperationCanceledException;

    public static string Fold(string text) => text.ToLowerInvariant();
}
