using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FileExplorer;

// Port of Ferry's transfer engine (Ferry: src-tauri/src/engine):
// scan first (totals + all conflicts in one pass), one conflict decision per job,
// CopyFileExW per file with pause/cancel in the progress callback, same-volume
// moves as renames, and one lane per destination volume.
// Optional xxHash3 verification after copying, as in Ferry (verify.rs).

public enum Phase { Queued, Scanning, Conflicts, Copying, Verifying, Done, Cancelled }
public enum Choice { Skip, Overwrite, Rename, Newer }

public record Conflict(int File, string Name, long SourceSize, DateTime SourceModified, long TargetSize, DateTime TargetModified)
{
    public string Detail => $"{MainWindow.Fmt(SourceSize)}, {SourceModified:yyyy-MM-dd HH:mm}  →  existing {MainWindow.Fmt(TargetSize)}, {TargetModified:yyyy-MM-dd HH:mm}";
}

public class Job : INotifyPropertyChanged
{
    public List<string> Sources { get; init; }
    public string Destination { get; init; }
    public bool IsMove { get; init; }
    public bool Verify { get; init; }

    // 0 running, 1 paused, 2 cancelled — read from the CopyFileExW callback, so kept lock-free.
    volatile int state;
    public bool Paused => state == 1;
    public bool IsCancelled => state == 2;
    public void Pause() { if (state == 0) state = 1; Refresh(); }
    public void Resume() { if (state == 1) state = 0; Refresh(); }
    public void Cancel() { state = 2; resolved.TrySetResult(null); Refresh(); }

    public Phase Phase { get; internal set; }
    public long TotalBytes, DoneBytes, CurrentSize, CurrentDone;
    public int TotalFiles, DoneFiles;
    public string Current = "";
    public List<Conflict> Conflicts { get; internal set; } = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new(); // written by the worker, read by the UI
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Notes = new();

    readonly TaskCompletionSource<Choice?> resolved = new();
    public void Resolve(Choice c) => resolved.TrySetResult(c);
    internal Choice? WaitForResolution() => resolved.Task.Result;

    // Speed reading, Shelf-style: age-weighted (time constant, not a sample window), so it neither
    // lurches when an old sample drops out nor freezes through thousands of small files.
    const double Tau = 1.5; // seconds
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastAt, lastPointAt = -1, rate, peak;
    internal void AddBytes(long delta)
    {
        Interlocked.Add(ref DoneBytes, delta);
        Interlocked.Add(ref CurrentDone, delta);
        double now = clock.Elapsed.TotalSeconds, dt = now - lastAt;
        if (dt <= 0) return;
        double instant = delta / dt;
        rate += (1 - Math.Exp(-dt / Tau)) * (instant - rate);
        lastAt = now;
        // chart: a point per report, throttled to ten a second, kept for ninety seconds
        if (now - lastPointAt < 0.1) return;
        lastPointAt = now;
        peak = Math.Max(peak, rate);
        lock (speeds)
        {
            speeds.Add((now * 1000, rate));
            while (speeds.Count > 900 || speeds[0].at < now * 1000 - 90000) speeds.RemoveAt(0);
        }
    }

    // ---- view ----
    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));

    string Verb => IsMove ? "Moving" : "Copying";
    public string Title => Phase switch
    {
        Phase.Done => $"{(IsMove ? "Moved" : "Copied")} {TotalFiles} file(s) to {Destination}",
        Phase.Cancelled => $"Cancelled — {DoneFiles} of {TotalFiles} file(s) to {Destination}",
        Phase.Scanning => $"Scanning {Sources.Count} item(s)…",
        Phase.Verifying => $"Verifying {TotalFiles} file(s) in {Destination}",
        _ => $"{Verb} {TotalFiles} file(s) to {Destination}",
    } + (Paused ? "  (paused)" : "");
    public double Fraction => TotalBytes > 0 ? (double)DoneBytes / TotalBytes : Phase == Phase.Done ? 1 : 0;
    public double CurrentFraction => CurrentSize > 0 ? (double)CurrentDone / CurrentSize : 0;
    public string Stats
    {
        get
        {
            var s = $"{DoneFiles}/{TotalFiles} files · {MainWindow.Fmt(DoneBytes)} of {MainWindow.Fmt(TotalBytes)}";
            if (Copying && rate > 0)
                s += $" · {MainWindow.Fmt((long)rate)}/s · {TimeSpan.FromSeconds((TotalBytes - DoneBytes) / rate):hh\\:mm\\:ss} left";
            return s;
        }
    }
    public bool Active => Phase is Phase.Queued or Phase.Scanning or Phase.Copying or Phase.Verifying or Phase.Conflicts;
    public bool Finished => !Active;
    public bool Copying => Phase is Phase.Copying or Phase.Verifying;
    public bool HasConflicts => Phase == Phase.Conflicts;
    public string ConflictTitle => $"{Conflicts.Count} file(s) already exist in the destination";
    public bool HasErrors => Errors.Count > 0 || Notes.Count > 0;
    public string ErrorSummary => string.Join("  ·  ", new[] { Errors.Count > 0 ? $"{Errors.Count} error(s)" : null }.Concat(Notes).Where(x => x != null));
    public string ErrorDetail => string.Join("\n", Errors.Take(50));
    public string PauseGlyph => Paused ? "\uE768" : "\uE769";

    // Speed chart data (drawn by SpeedChart): timestamped points and the held ceiling.
    readonly List<(double at, double value)> speeds = new();
    public (double at, double value)[] SpeedSamples { get { lock (speeds) return speeds.ToArray(); } }
    public double SpeedCeiling => SpeedChart.NiceCeiling(peak);
    public bool HasGraph { get { lock (speeds) return speeds.Count >= 2; } }
}

public static class Ferry
{
    static readonly Dictionary<string, Task> lanes = new(StringComparer.OrdinalIgnoreCase);
    public static event Action<Job> Finished;

    // Same destination volume → runs after the previous job there; other volumes run in parallel.
    public static Job Submit(IEnumerable<string> sources, string destination, bool move)
    {
        var job = new Job { Sources = sources.ToList(), Destination = destination, IsMove = move, Verify = Settings.Verify };
        var lane = Path.GetPathRoot(destination);
        lock (lanes)
        {
            var prev = lanes.TryGetValue(lane, out var t) ? t : Task.CompletedTask;
            lanes[lane] = prev.ContinueWith(_ => Run(job), TaskContinuationOptions.LongRunning);
        }
        return job;
    }

    static void Run(Job job)
    {
        try
        {
            if (job.IsCancelled) { job.Phase = Phase.Cancelled; return; }
            job.Phase = Phase.Scanning;
            var plan = Scan(job);
            if (job.IsCancelled) { job.Phase = Phase.Cancelled; return; }

            Choice choice = Choice.Overwrite; // irrelevant when there are no conflicts
            if (plan.Conflicts.Count > 0)
            {
                job.Conflicts = plan.Conflicts;
                job.Phase = Phase.Conflicts;
                if (job.WaitForResolution() is not { } c) { job.Phase = Phase.Cancelled; return; }
                choice = c;
            }
            job.Phase = Phase.Copying;
            Transfer(job, plan, choice);
            job.Phase = job.IsCancelled ? Phase.Cancelled : Phase.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // e.g. destination vanished mid-job: stop, and say why on the card
            job.Errors.Enqueue(ex.Message);
            job.Phase = Phase.Cancelled;
        }
        finally
        {
            job.Current = ""; job.CurrentSize = job.CurrentDone = 0;
            Finished?.Invoke(job);
        }
    }

    record FileEntry(string Source, string Relative, long Size);
    class Plan
    {
        public List<FileEntry> Files = new();
        public List<string> Dirs = new();
        public List<Conflict> Conflicts = new();
    }

    static Plan Scan(Job job)
    {
        var plan = new Plan();
        int links = 0;
        void Walk(string dir, string rel)
        {
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{dir}: {ex.Message}"); return; }
            foreach (var c in children)
            {
                if (job.IsCancelled) return;
                Add(c, Path.Combine(rel, c.Name));
            }
        }
        void Add(FileSystemInfo fsi, string rel)
        {
            if (fsi.Attributes.HasFlag(FileAttributes.ReparsePoint)) { links++; return; } // Ferry: links reported, not reproduced
            if (fsi is DirectoryInfo) { plan.Dirs.Add(rel); Walk(fsi.FullName, rel); }
            else
            {
                var f = (FileInfo)fsi;
                plan.Files.Add(new FileEntry(f.FullName, rel, f.Length));
                job.TotalBytes += f.Length; job.TotalFiles++;
            }
        }
        foreach (var s in job.Sources)
        {
            if (job.IsCancelled) break;
            FileSystemInfo fsi = Directory.Exists(s) ? new DirectoryInfo(s) : new FileInfo(s);
            if (!fsi.Exists) { job.Errors.Enqueue($"{s}: cannot be read"); continue; }
            if (Path.GetDirectoryName(s.TrimEnd('\\'))?.Equals(job.Destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true && job.IsMove)
            { job.Errors.Enqueue($"{fsi.Name}: already in this folder"); continue; }
            if (fsi is DirectoryInfo && (job.Destination + "\\").StartsWith(fsi.FullName.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            { job.Errors.Enqueue($"{fsi.Name}: can't copy a folder into itself"); continue; }
            Add(fsi, fsi.Name);
        }
        for (int i = 0; i < plan.Files.Count; i++)
        {
            var target = new FileInfo(Path.Combine(job.Destination, plan.Files[i].Relative));
            if (target.Exists)
                plan.Conflicts.Add(new Conflict(i, plan.Files[i].Relative, plan.Files[i].Size, File.GetLastWriteTime(plan.Files[i].Source), target.Length, target.LastWriteTime));
        }
        if (links > 0) job.Notes.Enqueue($"{links} shortcut(s) or junction(s) were left alone");
        return plan;
    }

    static bool SameVolume(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    static void Transfer(Job job, Plan plan, Choice choice)
    {
        // A move inside one volume is a rename: no bytes travel.
        if (job.IsMove && plan.Conflicts.Count == 0 && job.Sources.All(s => SameVolume(s, job.Destination)))
        {
            foreach (var s in job.Sources)
            {
                var name = Path.GetFileName(s.TrimEnd('\\'));
                if (!MoveFileEx(Long(s), Long(Path.Combine(job.Destination, name)), MOVEFILE_COPY_ALLOWED | MOVEFILE_WRITE_THROUGH))
                    job.Errors.Enqueue($"{name}: {LastError()}");
            }
            job.DoneBytes = job.TotalBytes; job.DoneFiles = job.TotalFiles;
            job.Notes.Enqueue("Moved within the same drive");
            return;
        }

        var conflicts = plan.Conflicts.ToDictionary(c => c.File);
        foreach (var d in plan.Dirs)
            try { Directory.CreateDirectory(Path.Combine(job.Destination, d)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{d}: {ex.Message}"); }

        var copied = new List<(string Source, string Target, long Size, string Relative)>();
        for (int i = 0; i < plan.Files.Count; i++)
        {
            if (job.IsCancelled) return;
            var e = plan.Files[i];
            var target = Path.Combine(job.Destination, e.Relative);
            bool overwrite = false;
            if (conflicts.TryGetValue(i, out var c))
            {
                var ch = choice == Choice.Newer && c.SourceModified <= c.TargetModified ? Choice.Skip : choice;
                if (ch == Choice.Skip) { job.TotalBytes -= e.Size; job.TotalFiles--; continue; }
                if (ch == Choice.Rename) target = UniqueName(target); else overwrite = true;
            }
            job.Current = e.Relative; job.CurrentSize = e.Size; job.CurrentDone = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            bool sameDiskMove = job.IsMove && SameVolume(e.Source, target);
            string error = null;
            if (sameDiskMove)
            {
                if (MoveFileEx(Long(e.Source), Long(target), MOVEFILE_COPY_ALLOWED | MOVEFILE_WRITE_THROUGH | (overwrite ? MOVEFILE_REPLACE_EXISTING : 0)))
                    job.AddBytes(e.Size);
                else error = LastError();
            }
            else error = CopyOne(job, e.Source, target, e.Size, overwrite);

            if (job.IsCancelled) return;
            if (error == null) { job.DoneFiles++; if (!sameDiskMove) copied.Add((e.Source, target, e.Size, e.Relative)); }
            else job.Errors.Enqueue($"{e.Relative}: {error}"); // one bad file doesn't stop the other thousand
        }

        var bad = job.Verify && !job.IsCancelled ? VerifyAll(job, copied) : new HashSet<string>();

        // Only now, with everything on the far side, does a cross-volume move delete anything —
        // and never a source whose copy failed verification.
        if (job.IsMove && !job.IsCancelled)
        {
            foreach (var s in copied.Select(c => c.Source).Where(x => !bad.Contains(x))) try { File.Delete(s); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{s}: not removed, {ex.Message}"); }
            foreach (var s in job.Sources.Where(Directory.Exists)) RemoveEmptyTree(s);
        }
    }

    // Reads both copies back and compares xxHash3 digests. Returns the sources that don't match.
    static HashSet<string> VerifyAll(Job job, List<(string Source, string Target, long Size, string Relative)> files)
    {
        var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        job.Phase = Phase.Verifying;
        job.DoneBytes = 0;
        job.TotalBytes = files.Sum(f => f.Size) * 2; // both copies are read
        foreach (var f in files)
        {
            if (job.IsCancelled) break;
            job.Current = f.Relative; job.CurrentSize = f.Size * 2; job.CurrentDone = 0;
            try
            {
                var a = Digest(job, f.Source);
                var b = Digest(job, f.Target);
                if (a == null || b == null) break; // cancelled midway: says nothing either way
                if (a != b) { bad.Add(f.Source); job.Errors.Enqueue($"{f.Relative}: copy does not match the source"); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bad.Add(f.Source);
                job.Errors.Enqueue($"{f.Relative}: could not verify, {ex.Message}");
            }
        }
        return bad;
    }

    static ulong? Digest(Job job, string path)
    {
        var hasher = new System.IO.Hashing.XxHash3();
        var buffer = new byte[1024 * 1024];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (job.IsCancelled) return null;
            while (job.Paused) Thread.Sleep(60);
            hasher.Append(buffer.AsSpan(0, n));
            job.AddBytes(n);
        }
        return hasher.GetCurrentHashAsUInt64();
    }

    static void RemoveEmptyTree(string root)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(root)) RemoveEmptyTree(d);
            Directory.Delete(root); // throws if something was left (skipped/failed files) — then it stays
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string UniqueName(string target)
    {
        string dir = Path.GetDirectoryName(target), stem = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
        for (int n = 2; n < 10000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return target;
    }

    // ---- CopyFileExW ----
    const uint COPY_FILE_FAIL_IF_EXISTS = 0x1, COPY_FILE_NO_BUFFERING = 0x1000;
    const long UnbufferedThreshold = 64L * 1024 * 1024; // big files bypass the cache instead of evicting everything else
    const uint PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1;
    const uint MOVEFILE_REPLACE_EXISTING = 0x1, MOVEFILE_COPY_ALLOWED = 0x2, MOVEFILE_WRITE_THROUGH = 0x8;

    delegate uint ProgressRoutine(long total, long transferred, long streamSize, long streamTransferred, uint stream, uint reason, IntPtr src, IntPtr dst, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CopyFileEx(string src, string dst, ProgressRoutine progress, IntPtr data, IntPtr cancel, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string src, string dst, uint flags);

    static string Long(string p) => p.StartsWith(@"\\?\") ? p : p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    static string CopyOne(Job job, string src, string dst, long size, bool overwrite)
    {
        long seen = 0;
        ProgressRoutine cb = (_, transferred, _, _, _, _, _, _, _) =>
        {
            if (transferred > seen) { job.AddBytes(transferred - seen); seen = transferred; }
            // Pause blocks inside the copy with both handles open; resume continues exactly here.
            while (job.Paused) Thread.Sleep(60);
            return job.IsCancelled ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };
        uint flags = (overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS) | (size >= UnbufferedThreshold ? COPY_FILE_NO_BUFFERING : 0);
        bool ok = CopyFileEx(Long(src), Long(dst), cb, IntPtr.Zero, IntPtr.Zero, flags);
        var err = ok ? null : LastError();
        GC.KeepAlive(cb);
        if (!ok && job.IsCancelled)
        {
            try { File.Delete(dst); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // partial file
            return null;
        }
        return err;
    }
}
