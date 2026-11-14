using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media;

namespace FileExplorer;

// Ferry, the transfer engine, for Linux: the Windows one (../Ferry.cs) step for step: scan first (totals and every
// conflict in one pass), one conflict decision per job, pause and cancel inside each file, moves on one file system as
// renames, one lane per destination file system, optional xxHash3 verification, a journal for Undo.
// What differs: a copy is a stream loop instead of CopyFileExW; "the same drive" is the same mount; names are compared
// exactly (Linux tells "a.txt" from "A.txt"); symbolic links are made again as links instead of being left alone.

public enum Phase { Queued, Scanning, Conflicts, Copying, Verifying, Done, Cancelled }
public enum Choice { Skip, Overwrite, Rename, Newer }

public record Conflict(int File, string Name, long SourceSize, DateTime SourceModified, long TargetSize, DateTime TargetModified)
{
    public string Detail => $"{Fmt.Size(SourceSize)}, {SourceModified:yyyy-MM-dd HH:mm}  →  existing {Fmt.Size(TargetSize)}, {TargetModified:yyyy-MM-dd HH:mm}";
}

public class Job : INotifyPropertyChanged
{
    public List<string> Sources { get; init; }
    public string Destination { get; init; }
    public bool IsMove { get; init; }
    public bool Verify { get; init; }
    public bool Parallel { get; init; }
    public IReadOnlyDictionary<string, string> Names { get; init; }
    public string Journal { get; internal set; }

    volatile int state; // 0 running, 1 paused, 2 cancelled
    public bool Paused => state == 1;
    public bool IsCancelled => state == 2;
    public void Pause() { if (state == 0) state = 1; Refresh(); }
    public void Resume() { if (state == 1) state = 0; Refresh(); }
    public void Cancel() { state = 2; resolved.TrySetResult(null); Refresh(); }

    public Phase Phase { get; internal set; }
    public long TotalBytes, DoneBytes, CurrentSize, CurrentDone;
    public int TotalFiles, DoneFiles, FailedFiles, SkippedFiles;
    public string Current { get; internal set; } = "";
    public List<Conflict> Conflicts { get; internal set; } = new();
    public bool Merged { get; internal set; }
    public readonly Dictionary<string, string> Duplicates = new(StringComparer.Ordinal);
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Notes = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<(string From, string To, bool Moved, long Stamp)> Completed = new();
    public readonly List<string> CreatedDirs = new();

    StreamWriter journal;
    long journalFlushed;
    internal void Arrived(string from, string to, bool moved)
    {
        var item = (from, to, moved, Ferry.Stamp(to));
        Completed.Enqueue(item);
        Write($"{(moved ? "moved" : "came")}\t{from}\t{to}\t{item.Item4}");
    }
    internal void MadeDir(string dir) { lock (CreatedDirs) CreatedDirs.Add(dir); Write($"dir\t{dir}"); }
    void Write(string line)
    {
        if (Journal == null) return;
        lock (Completed)
            try
            {
                if (journal == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Journal));
                    journal = new StreamWriter(Journal, append: false);
                    journal.WriteLine($"{(IsMove ? "move" : "copy")}\t{Destination}");
                }
                journal.WriteLine(line);
                if (Environment.TickCount64 - journalFlushed > 250) { journal.Flush(); journalFlushed = Environment.TickCount64; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notes.Enqueue("Undo notes couldn't be written: " + ex.Message); try { journal?.Dispose(); } catch (IOException) { } journal = null; Journal = null; }
    }
    internal void CloseJournal() { lock (Completed) try { journal?.Dispose(); } catch (IOException) { } finally { journal = null; } }
    bool canUndo;
    public bool CanUndo { get => canUndo; set { canUndo = value; Refresh(); } }

    readonly TaskCompletionSource<Choice?> resolved = new();
    public void Resolve(Choice c) => resolved.TrySetResult(c);
    internal Choice? WaitForResolution() => resolved.Task.Result;

    const double Tau = 1.5;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastAt, lastPointAt = -1, rate, peak;
    internal void AddBytes(long delta)
    {
        Interlocked.Add(ref DoneBytes, delta);
        Interlocked.Add(ref CurrentDone, delta);
        lock (clock)
        {
            double now = clock.Elapsed.TotalSeconds, dt = now - lastAt;
            if (dt <= 0) return;
            rate += (1 - Math.Exp(-dt / Tau)) * (delta / dt - rate);
            lastAt = now;
            if (now - lastPointAt < 0.1) return;
            lastPointAt = now;
            peak = Math.Max(peak, rate);
            lock (speeds)
            {
                speeds.Add((now * 1000, rate));
                while (speeds.Count > 900 || speeds[0].at < now * 1000 - 90000) speeds.RemoveAt(0);
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));

    string Verb => IsMove ? "Moving" : "Copying";
    string Outcome => (FailedFiles > 0 ? $" · {FailedFiles} failed" : "") + (SkippedFiles > 0 ? $" · {SkippedFiles} skipped" : "");
    public string Title => Phase switch
    {
        Phase.Done when FailedFiles > 0 || SkippedFiles > 0 => $"{(IsMove ? "Moved" : "Copied")} {DoneFiles} of {TotalFiles + SkippedFiles} file(s) to {Destination}{Outcome}",
        Phase.Done when TotalFiles == 0 => $"{(IsMove ? "Moved" : "Copied")} {Sources.Count} item(s) to {Destination}", // empty folders: no file to count
        Phase.Done => $"{(IsMove ? "Moved" : "Copied")} {TotalFiles} file(s) to {Destination}",
        Phase.Cancelled => $"Cancelled — {DoneFiles} of {TotalFiles + SkippedFiles} file(s) to {Destination}{Outcome}",
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
            var s = $"{DoneFiles}/{TotalFiles} files · {Fmt.Size(DoneBytes)} of {Fmt.Size(TotalBytes)}";
            if (Copying && rate > 0) s += $" · {Fmt.Size((long)rate)}/s · {TimeSpan.FromSeconds(Math.Min(359999, (TotalBytes - DoneBytes) / rate)):hh\\:mm\\:ss} left";
            return s;
        }
    }
    public bool Active => Phase is Phase.Queued or Phase.Scanning or Phase.Copying or Phase.Verifying or Phase.Conflicts;
    public bool Finished => !Active;
    public bool Copying => Phase is Phase.Copying or Phase.Verifying;
    public bool HasConflicts => Phase == Phase.Conflicts;
    public string ConflictTitle => $"{Conflicts.Count} file(s) already exist in the destination";
    public bool HasErrors => !Errors.IsEmpty || !Notes.IsEmpty;
    public string ErrorSummary => string.Join("  ·  ", new[] { Errors.IsEmpty ? null : $"{Errors.Count} error(s)" }.Concat(Notes).Where(x => x != null));
    public string ErrorDetail => string.Join("\n", Errors.Take(50));
    public string PauseGlyph => Paused ? Glyph.Play : Glyph.Pause;
    public string PauseText => Paused ? "Resume" : "Pause";
    public IBrush Hue { get; set; } = Brushes.CornflowerBlue;
    public string PercentText => $"{Fraction * 100:0}%";
    public double Rate => Copying && !Paused ? rate : 0;
    readonly List<(double at, double value)> speeds = new();
    public (double at, double value)[] SpeedSamples { get { lock (speeds) return speeds.ToArray(); } }
    public double Peak => peak;
}

public static class Ferry
{
    static readonly Dictionary<string, Task> lanes = new(StringComparer.Ordinal);
    public static event Action<Job> Finished;

    /// The same destination file system runs after the previous job there; others run alongside.
    public static Job Submit(IEnumerable<string> sources, string destination, bool move, IReadOnlyDictionary<string, string> names = null, string journal = null)
    {
        var job = new Job { Sources = sources.ToList(), Destination = destination.TrimEnd('/') is { Length: > 0 } d ? d : "/", IsMove = move, Verify = Settings.Verify, Parallel = Settings.ParallelCopy, Names = names, Journal = journal };
        var lane = Mounts.MountOf(destination).Point;
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
            job.Phase = Phase.Scanning; job.Refresh();
            var plan = Scan(job);
            if (job.IsCancelled) { job.Phase = Phase.Cancelled; return; }
            var choice = Choice.Overwrite;
            if (plan.Conflicts.Count > 0)
            {
                job.Conflicts = plan.Conflicts;
                job.Phase = Phase.Conflicts; job.Refresh();
                if (job.WaitForResolution() is not { } c) { job.Phase = Phase.Cancelled; return; }
                choice = c;
            }
            job.Phase = Phase.Copying; job.Refresh();
            Transfer(job, plan, choice);
            job.Phase = job.IsCancelled ? Phase.Cancelled : Phase.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            job.Errors.Enqueue(ex.Message);
            job.Phase = Phase.Cancelled;
        }
        finally
        {
            job.Current = ""; job.CurrentSize = job.CurrentDone = 0;
            job.CloseJournal();
            job.Refresh();
            Finished?.Invoke(job);
        }
    }

    record FileEntry(string Source, string Relative, long Size, string LinkTarget);
    class Plan
    {
        public List<FileEntry> Files = new();
        public List<string> Dirs = new();
        public List<Conflict> Conflicts = new();
        public Dictionary<string, (int Files, long Bytes)> PerSource = new(StringComparer.Ordinal);
        public bool InBatch;
    }

    static string Parent(string p) => Path.GetDirectoryName(p.TrimEnd('/')) ?? "/";

    static Plan Scan(Job job)
    {
        var plan = new Plan();
        var topNames = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string dir, string rel)
        {
            List<FileSystemInfo> children;
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
            // a symbolic link is made again as a link, never followed: a link to a parent would copy forever
            if (fsi.LinkTarget is { } link) { plan.Files.Add(new FileEntry(fsi.FullName, rel, 0, link)); job.TotalFiles++; return; }
            if (fsi is DirectoryInfo) { plan.Dirs.Add(rel); Walk(fsi.FullName, rel); }
            else
            {
                var f = (FileInfo)fsi;
                plan.Files.Add(new FileEntry(f.FullName, rel, f.Length, null));
                job.TotalBytes += f.Length; job.TotalFiles++;
            }
        }
        foreach (var s0 in job.Sources)
        {
            if (job.IsCancelled) break;
            var s = s0.Length > 1 ? s0.TrimEnd('/') : s0;
            FileSystemInfo fsi = Directory.Exists(s) && !Trash.IsLink(s) ? new DirectoryInfo(s) : new FileInfo(s);
            if (!fsi.Exists && fsi.LinkTarget == null) { job.Errors.Enqueue($"{s}: cannot be read"); continue; }
            bool here = Parent(s) == job.Destination;
            if (here && job.IsMove) { job.Errors.Enqueue($"{fsi.Name}: already in this folder"); continue; }
            if (fsi is DirectoryInfo && (job.Destination + "/").StartsWith(s + "/", StringComparison.Ordinal))
            { job.Errors.Enqueue($"{fsi.Name}: can't copy a folder into itself"); continue; }
            if (here) { var copy = CopyName(s, fsi is DirectoryInfo); job.Duplicates[s] = copy; Add(fsi, Path.GetFileName(copy)); continue; }
            var name = job.Names?.GetValueOrDefault(s0) ?? job.Names?.GetValueOrDefault(s) ?? fsi.Name;
            if (!topNames.Add(name) || fsi is DirectoryInfo && Directory.Exists(Path.Combine(job.Destination, name))) job.Merged = true;
            int files = job.TotalFiles; long bytes = job.TotalBytes;
            Add(fsi, name);
            plan.PerSource[s] = (job.TotalFiles - files, job.TotalBytes - bytes);
        }
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < plan.Files.Count; i++)
        {
            var f = plan.Files[i];
            var target = new FileInfo(Path.Combine(job.Destination, f.Relative));
            if (target.Exists || target.LinkTarget != null)
                plan.Conflicts.Add(new Conflict(i, f.Relative, f.Size, Modified(f.Source), target.Exists ? target.Length : 0, target.Exists ? target.LastWriteTime : default));
            else if (first.TryGetValue(target.FullName, out var earlier))
            {
                plan.Conflicts.Add(new Conflict(i, f.Relative, f.Size, Modified(f.Source), plan.Files[earlier].Size, Modified(plan.Files[earlier].Source)));
                plan.InBatch = true;
            }
            else first[target.FullName] = i;
        }
        return plan;
    }

    static DateTime Modified(string p) { try { return File.GetLastWriteTime(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; } }

    static bool SameMount(string a, string b) => Mounts.MountOf(a).Point == Mounts.MountOf(b).Point;

    static void Transfer(Job job, Plan plan, Choice choice)
    {
        // a move on one file system is a rename: no bytes travel
        if (job.IsMove && plan.Conflicts.Count == 0 && !job.Merged && job.Sources.All(s => SameMount(s, job.Destination)))
        {
            foreach (var s0 in job.Sources)
            {
                var s = s0.Length > 1 ? s0.TrimEnd('/') : s0;
                while (job.Paused && !job.IsCancelled) Thread.Sleep(60);
                if (job.IsCancelled) return;
                if (!plan.PerSource.TryGetValue(s, out var holds)) continue;
                var name = job.Names?.GetValueOrDefault(s0) ?? job.Names?.GetValueOrDefault(s) ?? Path.GetFileName(s);
                var target = Path.Combine(job.Destination, name);
                job.Current = name;
                try
                {
                    if (Directory.Exists(s) && !Trash.IsLink(s)) Directory.Move(s, target); else File.Move(s, target);
                    Interlocked.Add(ref job.DoneFiles, holds.Files);
                    Interlocked.Add(ref job.DoneBytes, holds.Bytes);
                    job.Arrived(s, target, moved: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{name}: {ex.Message}"); Interlocked.Add(ref job.FailedFiles, holds.Files); }
            }
            job.Notes.Enqueue("Moved within the same drive");
            return;
        }

        var conflicts = plan.Conflicts.ToDictionary(c => c.File);
        foreach (var d in plan.Dirs)
            try
            {
                var made = Path.Combine(job.Destination, d);
                if (Directory.Exists(made)) continue;
                Directory.CreateDirectory(made);
                job.MadeDir(made);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{d}: {ex.Message}"); }

        var copied = new List<(string Source, string Target, long Size, string Relative)>();
        var work = new List<(FileEntry File, string Target, bool Overwrite)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < plan.Files.Count; i++)
        {
            var e = plan.Files[i];
            var target = Path.Combine(job.Destination, e.Relative);
            bool overwrite = false;
            if (conflicts.TryGetValue(i, out var c))
            {
                var ch = choice == Choice.Newer && c.SourceModified <= c.TargetModified ? Choice.Skip : choice;
                if (ch == Choice.Skip) { job.TotalBytes -= e.Size; job.TotalFiles--; job.SkippedFiles++; continue; }
                if (ch == Choice.Rename) target = UniqueName(target, taken); else overwrite = true;
            }
            taken.Add(target);
            work.Add((e, target, overwrite));
        }

        void One((FileEntry File, string Target, bool Overwrite) w)
        {
            var e = w.File;
            while (job.Paused && !job.IsCancelled) Thread.Sleep(60);
            if (job.IsCancelled) return;
            job.Current = e.Relative; job.CurrentSize = e.Size; job.CurrentDone = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(w.Target));
            bool sameMountMove = job.IsMove && SameMount(e.Source, w.Target);
            string error = null;
            try
            {
                if (e.LinkTarget != null)
                {
                    if (w.Overwrite && (File.Exists(w.Target) || Trash.IsLink(w.Target))) File.Delete(w.Target);
                    File.CreateSymbolicLink(w.Target, e.LinkTarget);
                }
                else if (sameMountMove) { File.Move(e.Source, w.Target, w.Overwrite); job.AddBytes(e.Size); }
                else error = CopyOne(job, e.Source, w.Target, w.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
            if (ReferenceEquals(error, Stopped)) return;
            if (error == null)
            {
                Interlocked.Increment(ref job.DoneFiles);
                if (!sameMountMove) lock (copied) copied.Add((e.Source, w.Target, e.Size, e.Relative));
                if (!w.Overwrite) job.Arrived(e.Source, w.Target, sameMountMove);
            }
            else { job.Errors.Enqueue($"{e.Relative}: {error}"); Interlocked.Increment(ref job.FailedFiles); }
        }

        bool together = job.Parallel && !plan.InBatch && Mounts.Fast(job.Destination) && job.Sources.All(Mounts.Fast);
        var small = together ? work.Where(w => w.File.Size < ParallelBelow).ToList() : new();
        if (small.Count > 1)
            System.Threading.Tasks.Parallel.ForEach(small, new ParallelOptions { MaxDegreeOfParallelism = ParallelWays }, (w, st) => { if (job.IsCancelled) st.Stop(); else One(w); });
        foreach (var w in small.Count > 1 ? work.Except(small) : work)
        {
            if (job.IsCancelled) return;
            One(w);
        }
        if (job.IsCancelled) return;

        var bad = job.Verify ? VerifyAll(job, copied.Where(c => !Trash.IsLink(c.Target)).ToList()) : new HashSet<string>();

        // only now, with everything on the far side, does a move between file systems delete anything, and never a
        // source whose copy failed verification
        if (job.IsMove && !job.IsCancelled)
        {
            foreach (var s in copied.Select(c => c.Source).Where(x => !bad.Contains(x)))
            {
                while (job.Paused && !job.IsCancelled) Thread.Sleep(60);
                if (job.IsCancelled) return;
                try { File.Delete(s); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Errors.Enqueue($"{s}: not removed, {ex.Message}"); }
            }
            foreach (var s in job.Sources.Select(x => x.TrimEnd('/')).Where(x => Directory.Exists(x) && !Trash.IsLink(x))) RemoveEmptyTree(s);
        }
    }

    static HashSet<string> VerifyAll(Job job, List<(string Source, string Target, long Size, string Relative)> files)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);
        job.Phase = Phase.Verifying;
        job.DoneBytes = 0;
        job.TotalBytes = files.Sum(f => f.Size) * 2;
        foreach (var f in files)
        {
            if (job.IsCancelled) break;
            job.Current = f.Relative; job.CurrentSize = f.Size * 2; job.CurrentDone = 0;
            try
            {
                var a = Digest(job, f.Source);
                var b = Digest(job, f.Target);
                if (a == null || b == null) break;
                if (a != b) { bad.Add(f.Source); job.Errors.Enqueue($"{f.Relative}: copy does not match the source"); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bad.Add(f.Source); job.Errors.Enqueue($"{f.Relative}: could not verify, {ex.Message}"); }
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
            if (Trash.IsLink(root)) return;
            foreach (var d in Directory.GetDirectories(root)) RemoveEmptyTree(d);
            Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string UniqueName(string target, HashSet<string> taken)
    {
        string dir = Path.GetDirectoryName(target), stem = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
        for (int n = 2; n < 100000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!taken.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return target;
    }

    public static long Stamp(string path)
    {
        try
        {
            var f = new FileInfo(path);
            if (f.Exists) return unchecked(f.LastWriteTimeUtc.Ticks * 1_000_003 + f.Length);
            var d = new DirectoryInfo(path);
            return d.Exists ? d.LastWriteTimeUtc.Ticks : 0; // Linux keeps no creation time on most file systems
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    static readonly string Stopped = new("cancelled");

    /// "a - Copy.txt", then "a - Copy (2).txt": a copy into its own folder, as on Windows.
    internal static string CopyName(string path, bool folder)
    {
        string dir = Path.GetDirectoryName(path), name = Path.GetFileName(path);
        string stem = folder ? name : Path.GetFileNameWithoutExtension(name), ext = folder ? "" : Path.GetExtension(name);
        for (int n = 1; n < 10000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} - Copy{(n > 1 ? $" ({n})" : "")}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return path;
    }

    internal const long ParallelBelow = 16L * 1024 * 1024;
    internal const int ParallelWays = 4;

    /// One file, a megabyte at a time, Pause and Cancel heard between blocks; the copy keeps the source's date and mode.
    static string CopyOne(Job job, string src, string dst, bool overwrite)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(dst, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                if (input.Length > 0) output.SetLength(input.Length); // one allocation, not a growing file
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    while (job.Paused && !job.IsCancelled) Thread.Sleep(60);
                    if (job.IsCancelled) break;
                    output.Write(buffer, 0, n);
                    job.AddBytes(n);
                }
            }
            if (job.IsCancelled) { try { File.Delete(dst); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } return Stopped; }
            try { File.SetUnixFileMode(dst, File.GetUnixFileMode(src)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
