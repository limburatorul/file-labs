using System.Diagnostics;
using System.Text;

namespace FileExplorer;

/// What the drives are on Windows: the mounted file systems people keep files on (/, /home if it is its own, USB sticks
/// under /run/media, /media and /mnt, network shares), read from /proc/self/mounts.
public sealed record Mount(string Point, string Device, string Type)
{
    public bool Network => Type is "nfs" or "nfs4" or "cifs" or "smb3" or "fuse.sshfs" or "fuse.rclone" or "davfs";
    public bool Removable => Point.StartsWith("/run/media/") || Point.StartsWith("/media/");
    public string Name => Point == "/" ? "File System" : Path.GetFileName(Point) is { Length: > 0 } n ? n : Point;
    public (long Total, long Free) Space
    {
        get { try { var d = new DriveInfo(Point); return (d.TotalSize, d.AvailableFreeSpace); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (0, 0); } }
    }
}

static class Mounts
{
    static readonly HashSet<string> Kept = new() { "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "zfs", "bcachefs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk",
                                                    "nfs", "nfs4", "cifs", "smb3", "fuse.sshfs", "fuse.rclone", "davfs" };

    /// Every mount, longest first, so MountOf finds the innermost.
    public static List<Mount> All()
    {
        var list = new List<Mount>();
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length < 3) continue;
                list.Add(new Mount(Unescape(f[1]), f[0], f[2]));
            }
        }
        catch (IOException) { list.Add(new Mount("/", "", "ext4")); }
        return list.OrderByDescending(m => m.Point.Length).ToList();
    }

    /// The places for the sidebar's Drives section: real file systems where files live, not /boot, /var or the snap loops.
    public static List<Mount> Places()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return All().Where(m => Kept.Contains(m.Type) && (m.Point == "/" || m.Point == "/home" || m.Point == home || m.Removable
                                                           || m.Point.StartsWith("/mnt/") || m.Network)
                                && !m.Point.StartsWith("/boot") && !m.Point.Contains("/.snapshots"))
                    .GroupBy(m => m.Point).Select(g => g.First())
                    .OrderBy(m => m.Point == "/" ? 0 : m.Point.StartsWith("/home") ? 1 : 2).ThenBy(m => m.Point, StringComparer.Ordinal).ToList();
    }

    public static Mount MountOf(string path)
    {
        var p = path.TrimEnd('/') + "/";
        foreach (var m in All())
            if (p.StartsWith(m.Point.TrimEnd('/') + "/", StringComparison.Ordinal)) return m;
        return new Mount("/", "", "");
    }

    /// The mount table writes spaces and a few others as octal: "/run/media/me/My\040Disk".
    static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\\' && IsOctal(s, i + 1)) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8)); i += 3; }
            else sb.Append(s[i]);
        return sb.ToString();
    }
    static bool IsOctal(string s, int i) => i + 3 <= s.Length && s.Substring(i, 3).All(c => c is >= '0' and <= '7');

    /// An SSD, NVMe or network share: small files several at once help there; on a spinning disk the heads only jump.
    public static bool Fast(string path)
    {
        var m = MountOf(path);
        if (m.Network) return true;
        if (!m.Device.StartsWith("/dev/")) return false;
        try
        {
            // /dev/nvme0n1p2 → nvme0n1, /dev/sda3 → sda; a mapper or dm device resolves through /sys/class/block
            var dev = Path.GetFileName(new FileInfo(m.Device).ResolveLinkTarget(true)?.FullName ?? m.Device);
            var sys = $"/sys/class/block/{dev}";
            if (!Directory.Exists(sys)) return false;
            var parent = File.Exists($"{sys}/partition") ? Path.GetFileName(Path.GetDirectoryName(new DirectoryInfo(sys).ResolveLinkTarget(true)?.FullName ?? sys)) : dev;
            var rot = $"/sys/block/{parent}/queue/rotational";
            return File.Exists(rot) && File.ReadAllText(rot).Trim() == "0";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// The freedesktop.org Trash: files/ and info/ in ~/.local/share/Trash for the home file system, $mount/.Trash-$uid for
/// others; each item has a .trashinfo with where it came from and when, so the desktop's own Trash shows and restores it.
static class Trash
{
    static string Uid => Environment.GetEnvironmentVariable("UID") is { Length: > 0 } u ? u : RunId();
    static string uid;
    static string RunId()
    {
        if (uid != null) return uid;
        try { var p = Process.Start(new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true }); uid = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { uid = "1000"; }
        return uid;
    }
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string HomeTrash => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share"), "Trash");

    /// Where an item at path goes: the home Trash when it is on the same file system, else the mount's own.
    public static string For(string path)
    {
        var m = Mounts.MountOf(path);
        return m.Point == Mounts.MountOf(Home).Point ? HomeTrash : Path.Combine(m.Point, $".Trash-{Uid}");
    }

    public static IEnumerable<string> AllTrashes() =>
        new[] { HomeTrash }.Concat(Mounts.Places().Select(m => Path.Combine(m.Point, $".Trash-{Uid}")).Where(Directory.Exists)).Distinct();

    /// Moves it into the Trash. Returns where it went (for Undo), or throws.
    public static string Put(string path)
    {
        var trash = For(path);
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        var name = Path.GetFileName(path.TrimEnd('/'));
        string target = Path.Combine(trash, "files", name), info;
        for (int n = 2; ; n++)
        {
            info = Path.Combine(trash, "info", Path.GetFileName(target) + ".trashinfo");
            try { using var f = new FileStream(info, FileMode.CreateNew); var b = Encoding.UTF8.GetBytes(Info(path)); f.Write(b); break; }
            catch (IOException) when (File.Exists(info)) { target = Path.Combine(trash, "files", $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}"); }
        }
        try
        {
            if (Directory.Exists(path) && !IsLink(path)) Directory.Move(path, target); else File.Move(path, target);
        }
        catch { File.Delete(info); throw; }
        return target;
    }

    static string Info(string path) =>
        $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";

    public static bool IsLink(string path) { try { return new FileInfo(path).LinkTarget != null; } catch (IOException) { return false; } }

    /// Puts an item back where it was deleted from (the .trashinfo says), unless something is there now.
    public static string Restore(string inTrash)
    {
        var trash = Path.GetDirectoryName(Path.GetDirectoryName(inTrash));
        var info = Path.Combine(trash, "info", Path.GetFileName(inTrash) + ".trashinfo");
        var from = OriginalPath(info) ?? throw new IOException("No record of where it came from");
        if (File.Exists(from) || Directory.Exists(from)) throw new IOException($"{Path.GetFileName(from)} is there again");
        Directory.CreateDirectory(Path.GetDirectoryName(from));
        if (Directory.Exists(inTrash) && !IsLink(inTrash)) Directory.Move(inTrash, from); else File.Move(inTrash, from);
        File.Delete(info);
        return from;
    }

    public static string OriginalPath(string info)
    {
        if (!File.Exists(info)) return null;
        foreach (var line in File.ReadLines(info))
            if (line.StartsWith("Path="))
            {
                var p = Uri.UnescapeDataString(line[5..]);
                return p.StartsWith('/') ? p : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(info)), "..", p);
            }
        return null;
    }

    public static DateTime Deleted(string info)
    {
        foreach (var line in File.ReadLines(info))
            if (line.StartsWith("DeletionDate=") && DateTime.TryParse(line[13..], out var d)) return d;
        return File.GetLastWriteTime(info);
    }

    /// Deleted for good: the item and its record.
    public static void Purge(string inTrash)
    {
        var trash = Path.GetDirectoryName(Path.GetDirectoryName(inTrash));
        if (Directory.Exists(inTrash) && !IsLink(inTrash)) Directory.Delete(inTrash, true); else File.Delete(inTrash);
        var info = Path.Combine(trash, "info", Path.GetFileName(inTrash) + ".trashinfo");
        if (File.Exists(info)) File.Delete(info);
    }
}

static class Shell
{
    /// The file's own app (xdg-open asks the desktop which one).
    public static void Open(string path) => Start("xdg-open", path);

    public static void Start(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static bool Exists(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':').Any(d => File.Exists(Path.Combine(d, exe)));

    static readonly (string Exe, string Flag)[] Terminals =
    {
        ("konsole", "--workdir"), ("gnome-terminal", "--working-directory="), ("kgx", "--working-directory="), ("xfce4-terminal", "--working-directory="),
        ("kitty", "--directory"), ("alacritty", "--working-directory"), ("wezterm", "start --cwd"), ("foot", "--working-directory="),
        ("tilix", "--working-directory="), ("terminator", "--working-directory="), ("xterm", null),
    };

    /// The terminal Settings names, else the first installed one, opened in dir.
    public static void Terminal(string dir)
    {
        var t = Terminals.FirstOrDefault(x => x.Exe == Settings.Terminal && Exists(x.Exe));
        if (t.Exe == null) t = Terminals.FirstOrDefault(x => Exists(x.Exe));
        if (t.Exe == null) return;
        try
        {
            var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = false, WorkingDirectory = dir };
            if (t.Flag is { } f)
            {
                if (f.EndsWith('=')) psi.ArgumentList.Add(f + dir);
                else { foreach (var part in f.Split(' ')) psi.ArgumentList.Add(part); psi.ArgumentList.Add(dir); }
            }
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static IEnumerable<string> InstalledTerminals => Terminals.Select(t => t.Exe).Where(Exists);
}
