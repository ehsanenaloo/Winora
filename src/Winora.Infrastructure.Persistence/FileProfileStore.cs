// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using Winora.Application;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Custom profiles as one JSON file each in the "profiles" folder of the application data folder. The file name is derived from a validated profile id and never from the user's text. Files and folders
/// that are links are ignored, a file larger than the profile limit is not read, and a write goes to a temporary file first so a crash never leaves a half-written profile.
/// </summary>
public sealed class FileProfileStore : IProfileStore
{
    public const string FolderName = "profiles";
    private const int MaxFiles = 200;
    private readonly Func<string> folderProvider;

    public FileProfileStore(AppDataPaths paths) : this(() => Path.Combine(paths.Root, FolderName)) { }
    public FileProfileStore(Func<string> folderProvider) { this.folderProvider = folderProvider; }

    private static bool IsLink(FileSystemInfo info) => info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private string FileFor(string id)
    {
        if (!ProfileFile.IsCustomId(id)) throw new ArgumentException("The profile id is not valid.", nameof(id));
        return Path.Combine(folderProvider(), id + ".json");
    }

    public IReadOnlyList<string> List()
    {
        var folder = new DirectoryInfo(folderProvider());
        if (!folder.Exists || IsLink(folder)) return [];
        return folder.EnumerateFiles("custom.*.json").Take(MaxFiles).Where(f => !IsLink(f))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name)).Where(ProfileFile.IsCustomId).Order(StringComparer.Ordinal).ToList();
    }

    public string? Read(string id)
    {
        var file = new FileInfo(FileFor(id));
        if (!file.Exists || IsLink(file) || file.Length > ProfileFile.MaxBytes) return null;
        try
        {
            using var stream = VerifiedFile.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > ProfileFile.MaxBytes) return null; // the size is read again from the open file: the first check was on the path
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public void Write(string id, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > ProfileFile.MaxBytes) throw new IOException("The profile is too large.");
        var target = FileFor(id);
        var folder = new DirectoryInfo(folderProvider());
        if (IsLink(folder)) throw new IOException("The profiles folder is a link.");
        Directory.CreateDirectory(folder.FullName);
        using var pin = VerifiedFile.Pin(folder.FullName); // held until the file is in place
        var temp = Path.Combine(folder.FullName, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = VerifiedFile.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void Delete(string id)
    {
        var file = new FileInfo(FileFor(id));
        if (file.Exists && !IsLink(file)) file.Delete();
    }
}
