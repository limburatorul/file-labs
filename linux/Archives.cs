using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace FileExplorer;

/// ZIP and tar (plain, .gz) with .NET's own readers; 7z, rar, xz and zst through the system's 7z or bsdtar when installed.
static class Archives
{
    static readonly string[] Native = { ".zip", ".tar", ".tgz", ".gz" };
    static readonly string[] Other = { ".7z", ".rar", ".xz", ".zst", ".bz2", ".iso" };

    public static bool CanExtract(string path)
    {
        var name = path.ToLowerInvariant();
        if (Native.Any(name.EndsWith)) return true;
        return Other.Any(name.EndsWith) && (Shell.Exists("7z") || Shell.Exists("bsdtar"));
    }

    public static string Stem(string name)
    {
        foreach (var ext in new[] { ".tar.gz", ".tar.xz", ".tar.zst", ".tar.bz2", ".tgz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var c = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    public static void Zip(List<string> paths, string target)
    {
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var root = Path.GetDirectoryName(p.TrimEnd('/'));
                foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    zip.CreateEntryFromFile(f, Path.GetRelativePath(root, f));
            }
            else zip.CreateEntryFromFile(p, Path.GetFileName(p));
        }
    }

    public static void Extract(string archive, string target)
    {
        var name = archive.ToLowerInvariant();
        Directory.CreateDirectory(target);
        if (name.EndsWith(".zip")) { ZipFile.ExtractToDirectory(archive, target); return; }
        if (name.EndsWith(".tar")) { TarFile.ExtractToDirectory(archive, target, overwriteFiles: false); return; }
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
        {
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, target, overwriteFiles: false);
            return;
        }
        var (exe, args) = Shell.Exists("7z") ? ("7z", new[] { "x", "-y", $"-o{target}", archive }) : ("bsdtar", new[] { "-xf", archive, "-C", target });
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException($"{exe} didn't start");
        var err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"{exe}: {err.Trim()}");
    }

    /// What's inside, for Quick View: names and sizes, the first 2,000.
    public static List<(string Name, long Size)> List(string archive)
    {
        var name = archive.ToLowerInvariant();
        var list = new List<(string, long)>();
        if (name.EndsWith(".zip"))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries.Take(2000)) list.Add((e.FullName, e.Length));
        }
        else if (name.EndsWith(".tar") || name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
        {
            using Stream s = name.EndsWith(".tar") ? File.OpenRead(archive) : new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var tar = new TarReader(s);
            TarEntry e;
            while ((e = tar.GetNextEntry()) != null && list.Count < 2000) list.Add((e.Name, e.Length));
        }
        return list;
    }
}
