using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TutorialOverlay;

/// <summary>File helpers shared by settings, the tutorial library and the template store.</summary>
internal static class Files
{
    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay");

    /// <summary>
    /// Writes through a temporary file and a rename, so a crash or a full disk never leaves a half-written file
    /// in place of the old one.
    /// </summary>
    public static void WriteAtomic(string path, string text) => WriteAtomic(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    /// <summary>Deletes the oldest files matching <paramref name="pattern"/>: keeps at most <paramref name="keep"/> and nothing older than <paramref name="maxAge"/>.</summary>
    public static int Prune(string dir, string pattern, int keep, TimeSpan maxAge)
    {
        if (!Directory.Exists(dir)) return 0;
        int removed = 0;
        var files = new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            if (i < keep && DateTime.UtcNow - files[i].LastWriteTimeUtc <= maxAge) continue;
            try { files[i].Delete(); removed++; } catch { }
        }
        return removed;
    }
}
