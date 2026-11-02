// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Infrastructure.Persistence;

/// <summary>Writes a file the person chose without destroying the old one when the write fails: the bytes go to a temporary file in the same folder, then replace the target.</summary>
public static class ReplaceFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the temporary file stays; it holds only the new bytes */ }
            throw;
        }
    }
}
