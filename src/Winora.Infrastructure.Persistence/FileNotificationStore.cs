// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using Winora.Application.Activity;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// The notification list as one JSON file named "notifications.json". The folder comes from a provider so the desktop app can pass its admin-only folder. A link or a file larger than the
/// limit is not read; a write goes to a temporary file first so a crash never leaves a half-written list.
/// </summary>
public sealed class FileNotificationStore : INotificationStore
{
    public const string FileName = "notifications.json";
    private readonly Func<string> folderProvider;

    public FileNotificationStore(Func<string> folderProvider) { this.folderProvider = folderProvider; }

    private static bool IsLink(FileSystemInfo info) => info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    public string? Read()
    {
        try
        {
            var file = new FileInfo(Path.Combine(folderProvider(), FileName));
            if (!file.Exists || IsLink(file) || file.Length > NotificationFile.MaxBytes) return null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > NotificationFile.MaxBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public void Write(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > NotificationFile.MaxBytes) throw new IOException("The notification list is too large.");
        var folder = new DirectoryInfo(folderProvider());
        if (IsLink(folder)) throw new IOException("The data folder is a link.");
        Directory.CreateDirectory(folder.FullName);
        var target = Path.Combine(folder.FullName, FileName);
        if (IsLink(new FileInfo(target))) throw new IOException("The notification file is a link.");
        var temp = Path.Combine(folder.FullName, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
