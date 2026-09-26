using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace FileExplorer;

// Folder sizes, remembered between sessions: walking a full 12 TB drive takes minutes, and the
// answer rarely changes. Every folder a scan passes through is kept, so opening any folder under a
// scanned one shows its sizes at once, and a scan cut short resumes from what it had finished.
// An entry is trusted while the folder's own timestamp is unchanged — Windows updates it whenever a
// direct child is added, removed or renamed. When one is found changed, it and every total above it
// are dropped. A change deep inside a folder nobody opens can still lag; Ctrl+R clears everything.
public static class SizeCache
{
    record Entry(long Size, long Stamp, long UsedAt); // Stamp = folder's LastWriteTimeUtc ticks

    static readonly ConcurrentDictionary<string, Entry> map = new(StringComparer.OrdinalIgnoreCase);
    static readonly string FilePath = Path.Combine(Settings.Dir, "folder-sizes.txt");
    const int MaxEntries = 1_000_000; // ~100 MB on disk at most; the least recently used go first

    // Loaded in the background at startup (a big cache is a second of parsing); callers are all off the
    // UI thread and wait for it, so nothing is measured twice for having asked too early.
    static Task loading = Task.CompletedTask;
    public static void StartLoading() => loading = Task.Run(Load);
    static void Ready() { if (!loading.IsCompleted) loading.Wait(); }

    /// The remembered size, or -1 when unknown or the folder has changed since. Call off the UI thread.
    public static long Get(string path, DateTime lastWriteUtc)
    {
        Ready();
        if (!map.TryGetValue(path, out var e)) return -1;
        if (e.Stamp != lastWriteUtc.Ticks) { Forget(path); return -1; }
        map[path] = e with { UsedAt = DateTime.UtcNow.Ticks };
        return e.Size;
    }

    /// A folder changed: its size is stale, and so is the total of every folder that contains it.
    static void Forget(string path)
    {
        for (var p = path; p != null; p = Path.GetDirectoryName(p)) map.TryRemove(p, out _); // up to the drive root, "D:\"
        changed = true;
    }

    public static void Set(string path, DateTime lastWriteUtc, long size)
    {
        map[path] = new Entry(size, lastWriteUtc.Ticks, DateTime.UtcNow.Ticks);
        changed = true;
    }

    public static void Clear() { Ready(); map.Clear(); changed = true; }

    static volatile bool changed; // since the last save

    /// Written every few minutes, not only on exit: a crash or a power cut lost a whole session's
    /// measuring (a full drive is minutes of walking).
    public static void SaveIfChanged()
    {
        if (!changed) return;
        changed = false;
        Save();
    }

    public static void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            foreach (var line in File.ReadLines(FilePath))
            {
                var p = line.Split('|', 4);
                if (p.Length == 4 && long.TryParse(p[0], out var size) && long.TryParse(p[1], out var stamp) && long.TryParse(p[2], out var used))
                    map[p[3]] = new Entry(size, stamp, used);
            }
        }
        catch (IOException) { } // unreadable cache is not worth a message; it just rebuilds
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var keep = map.OrderByDescending(kv => kv.Value.UsedAt).Take(MaxEntries);
            Settings.WriteAllLines(FilePath, keep.Select(kv =>
                string.Create(CultureInfo.InvariantCulture, $"{kv.Value.Size}|{kv.Value.Stamp}|{kv.Value.UsedAt}|{kv.Key}")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
