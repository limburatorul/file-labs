using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace FileExplorer;

// Folder sizes, remembered between sessions: walking a full 12 TB drive takes minutes, and the
// answer rarely changes. An entry is trusted while the folder's own timestamp is unchanged —
// Windows updates it whenever a direct child is added, removed or renamed. A change deep inside a
// subtree does not touch it, so the number can lag; Ctrl+R (or any copy/move/delete) clears it.
public static class SizeCache
{
    record Entry(long Size, long Stamp, long UsedAt); // Stamp = folder's LastWriteTimeUtc ticks

    static readonly ConcurrentDictionary<string, Entry> map = new(StringComparer.OrdinalIgnoreCase);
    static readonly string FilePath = Path.Combine(Settings.Dir, "folder-sizes.txt");
    const int MaxEntries = 50_000; // ~3 MB on disk; oldest-used dropped first

    /// The remembered size, or -1 when unknown or the folder has changed since.
    public static long Get(string path, DateTime lastWriteUtc)
    {
        if (!map.TryGetValue(path, out var e) || e.Stamp != lastWriteUtc.Ticks) return -1;
        map[path] = e with { UsedAt = DateTime.UtcNow.Ticks };
        return e.Size;
    }

    public static void Set(string path, DateTime lastWriteUtc, long size) =>
        map[path] = new Entry(size, lastWriteUtc.Ticks, DateTime.UtcNow.Ticks);

    public static void Clear() => map.Clear();

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
            File.WriteAllLines(FilePath, keep.Select(kv =>
                string.Create(CultureInfo.InvariantCulture, $"{kv.Value.Size}|{kv.Value.Stamp}|{kv.Value.UsedAt}|{kv.Key}")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
