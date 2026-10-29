using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.VisualBasic.FileIO;

namespace FileExplorer;

// Commands beyond copy/move/delete: undo, zip, compare, rename many, checksum, shortcuts, run as admin.
public partial class MainWindow
{
    // ---- Undo (Ctrl+Z, the right-click menu, a transfer's Undo button) ----
    // Each operation is kept as the steps that take it back, in undo.json beside the settings, so it outlives a restart:
    // renames, new folders and files, zips, shortcuts, copies and moves (finished, or cancelled or failed halfway: what
    // they did), and deletes, put back from the Recycle Bin. A file that replaced another isn't undone, since the one it
    // replaced is gone. An entry stays until each of its steps has worked: what failed is tried again next time.
    // Kinds: recycle (Path, made by the operation), move (Path back to To, the exact old path, its old name included),
    // rename (Path to the name To), rmdir (Path, a folder it made, once empty), restore (Path: where it was deleted from;
    // To: that very item in the Recycle Bin, its $R file). Stamp (Ferry.Stamp): what the item looked like when the
    // operation left it; something put in its place since is left alone, and said so.
    internal record UndoStep(string Kind, string Path, string To = null, long Stamp = 0);
    internal record UndoEntry(string What, DateTime When, List<UndoStep> Steps);

    readonly List<UndoEntry> undo = LoadUndo();
    static string UndoFile => Path.Combine(Settings.Dir, "undo.json");
    static string JournalDir => Path.Combine(Settings.Dir, "undo-journal");
    static readonly object UndoFileLock = new();
    int undoVersion; // each save carries the history as it was; an older one still waiting is dropped

    /// The history, plus what transfers that never finished (File Labs closed or crashed while they ran) wrote in
    /// their journals. A history that can't be read is kept aside as undo.json.bad, not overwritten.
    static List<UndoEntry> LoadUndo()
    {
        List<UndoEntry> list;
        try { list = System.Text.Json.JsonSerializer.Deserialize<List<UndoEntry>>(File.ReadAllText(UndoFile)) ?? new(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { list = new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            list = new();
            try { File.Copy(UndoFile, UndoFile + ".bad", overwrite: true); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
        string[] journals;
        try { journals = Directory.Exists(JournalDir) ? Directory.GetFiles(JournalDir, "*.txt") : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { journals = Array.Empty<string>(); }
        foreach (var j in journals) if (ReadJournal(j) is { } entry) list.Add(entry);
        if (journals.Length > 0 && WriteUndoFile(list) == null)
            foreach (var j in journals) try { File.Delete(j); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// A journal Ferry wrote while a transfer ran: "move|copy  destination", then "moved|came  from  to  stamp" per item
    /// that arrived and "dir  path" per folder it made.
    internal static UndoEntry ReadJournal(string file)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2) return null;
            bool move = lines[0].StartsWith("move");
            var steps = new List<UndoStep>();
            foreach (var parts in lines.Skip(1).Select(l => l.Split('\t')))
                if (parts is ["dir", var dir]) steps.Add(new("rmdir", dir));
                else if (parts is [var how, var from, var to, var stamp] && long.TryParse(stamp, out var st))
                    steps.Add(how == "moved" || move && !Exists(from) ? new("move", to, from, st) : new("recycle", to, null, st));
            int items = steps.Count(s => s.Kind != "rmdir");
            return items == 0 ? null : new($"{(move ? "move" : "copy")} of {items:N0} items (interrupted)", File.GetCreationTime(file), steps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Written whole to a side file, then put in place in one rename, so a crash mid-write leaves the old history whole.
    static string WriteUndoFile(List<UndoEntry> list)
    {
        lock (UndoFileLock)
            try
            {
                var tmp = UndoFile + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(list));
                File.Move(tmp, UndoFile, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    /// Saved off the UI thread; a failure is said in the status line. `then` (a transfer's journal) is deleted once
    /// the history that holds its steps is on disk.
    void SaveUndo(string then = null)
    {
        var snapshot = undo.ToList();
        int version = ++undoVersion;
        Task.Run(() =>
        {
            if (version != Volatile.Read(ref undoVersion) && then == null) return; // a newer save follows
            if (WriteUndoFile(snapshot) is { } error) { Dispatcher.BeginInvoke(() => Status.Text = "Undo history not saved: " + error); return; }
            if (then != null) try { File.Delete(then); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    UndoEntry Remember(string what, IEnumerable<UndoStep> steps, DateTime? when = null, string then = null)
    {
        var entry = new UndoEntry(what, when ?? DateTime.Now, steps.ToList());
        undo.Add(entry);
        if (undo.Count > 50) undo.RemoveAt(0);
        SaveUndo(then);
        return entry;
    }

    UndoEntry Remember(string what, params UndoStep[] steps) => Remember(what, steps, null);

    /// Something an operation just made, to send to the Recycle Bin on Undo while it still looks the same.
    static UndoStep Made(string path) => new("recycle", path, null, Ferry.Stamp(path));

    UndoEntry undoing; // a Ctrl+Z still under way
    readonly Dictionary<Job, Action> undoJobs = new(); // moves that undo a move: not undoable themselves; each says when it's done
    readonly Dictionary<Job, UndoEntry> jobUndo = new(); // a transfer's card → its entry, for its Undo button

    void Undo() => Undo(undo.Count > 0 ? undo[^1] : null);

    async void Undo(UndoEntry entry)
    {
        if (undoing != null) { Status.Text = "Still undoing " + undoing.What; return; }
        if (entry == null || !undo.Contains(entry)) { Status.Text = "Nothing to undo"; return; }
        undoing = entry;
        Status.Text = $"Undoing {entry.What}…";
        var left = new List<UndoStep>();
        string error = null;
        int gone = 0, changed = 0;
        // Off the UI thread: what was changed since is set aside, names go back (all together, so names swapped among
        // themselves don't collide), deleted items come back from the Recycle Bin, each the very item that was deleted.
        var ready = await Task.Run(() =>
        {
            var kept = new List<UndoStep>();
            foreach (var s in entry.Steps)
                if (s.Kind is "recycle" or "move" or "rename" && s.Stamp != 0 && Exists(s.Path) && Ferry.Stamp(s.Path) != s.Stamp) changed++;
                else kept.Add(s);
            var renames = kept.Where(s => s.Kind == "rename" && Exists(s.Path)).ToList(); // an interrupted rename lists two places for each item
            if (renames.Count > 0)
                try { RenameAll(renames.Select(s => (s.Path, s.To)).ToList()); }
                catch (RenameFailed ex) { left.AddRange(ex.NotBack.Select(n => new UndoStep("rename", n.Now, n.Name, Ferry.Stamp(n.Now)))); error ??= ex.Message; }
            foreach (var s in kept.Where(s => s.Kind == "restore"))
                if (RecycleBinWindow.RestoreItem(s.To ?? "", out bool lost) is { } why) { left.Add(s); error ??= why; }
                else if (lost) gone++; // emptied from the bin: nothing left to bring back, so not kept either
            return kept;
        });
        // then through the shell and through transfers (each item to its exact old path); the folders it made go last, once emptied
        var recycle = ready.Where(s => s.Kind == "recycle" && Exists(s.Path)).Select(s => s.Path).ToList();
        var moves = ready.Where(s => s.Kind == "move" && Exists(s.Path)).GroupBy(s => Path.GetDirectoryName(s.To)).ToList();
        int waiting = (recycle.Count > 0 ? 1 : 0) + moves.Count;
        void OneDone() { if (--waiting == 0) Finish(); }
        async void Finish()
        {
            var rest = await Task.Run(() =>
            {
                foreach (var s in ready.Where(s => s.Kind == "rmdir").OrderByDescending(s => s.Path.Length))
                    try { if (Directory.Exists(s.Path) && !Directory.EnumerateFileSystemEntries(s.Path).Any()) Directory.Delete(s.Path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                // what still stands where the operation put it is tried again next time (a folder still holding something stays, quietly)
                var stays = ready.Where(s => (s.Kind is "recycle" or "move") && Exists(s.Path)).ToList();
                if (stays.Count > 0 || left.Count > 0) stays.AddRange(ready.Where(s => s.Kind == "rmdir" && Directory.Exists(s.Path))); // emptied once the rest goes
                return stays;
            });
            left.AddRange(rest);
            undo.Remove(entry);
            if (left.Count > 0) undo.Add(entry with { Steps = left });
            SaveUndo();
            foreach (var (job, e) in jobUndo.Where(x => x.Value == entry).ToList()) { job.CanUndo = false; job.Refresh(); jobUndo.Remove(job); }
            undoing = null;
            var notes = string.Join(", ", new[] { gone > 0 ? $"{gone} no longer in the Recycle Bin" : null, changed > 0 ? $"{changed} changed since, left alone" : null }.Where(n => n != null));
            Status.Text = (left.Count == 0 ? $"Undone: {entry.What}" : $"Partly undone: {entry.What}. Ctrl+Z tries the rest")
                        + (notes != "" ? $" ({notes})" : "") + (left.Count > 0 && error != null ? $": {error}" : "");
            Left.Refresh(); Right.Refresh();
        }
        if (waiting == 0) { Finish(); return; }
        if (recycle.Count > 0) Recycle(recycle, UIOption.OnlyErrorDialogs, done: OneDone);
        foreach (var back in moves)
            if (Submit(back.Select(s => s.Path).ToList(), back.Key, move: true, ask: false, names: back.ToDictionary(s => s.Path, s => Path.GetFileName(s.To), StringComparer.OrdinalIgnoreCase), journal: false) is { } job)
                undoJobs[job] = OneDone;
            else OneDone();
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // Called when a Ferry job ends. Finished cleanly: what it brought, as a whole (a folder is one step). Cancelled, failed,
    // or with files that were already there: each file it brought, and the folders it made. Worked out off the UI thread;
    // the job's journal goes once the history holding the same steps is saved.
    async void RememberJob(Job job)
    {
        if (undoJobs.Remove(job, out var undone)) { undone(); return; }
        var steps = await Task.Run(() =>
        {
            if (job.Phase == Phase.Done && job.Conflicts.Count == 0 && !job.Merged && job.Errors.IsEmpty)
                return job.Sources.Select(s => (Source: s, Target: job.Duplicates.GetValueOrDefault(s) ?? Path.Combine(job.Destination, job.Names?.GetValueOrDefault(s) ?? Path.GetFileName(s.TrimEnd('\\')))))
                                  .Where(x => Path.GetFileName(x.Target) != "")
                                  .Select(x => job.IsMove ? new UndoStep("move", x.Target, x.Source, Ferry.Stamp(x.Target)) : Made(x.Target)).ToList();
            List<string> dirs;
            lock (job.CreatedDirs) dirs = job.CreatedDirs.ToList();
            return job.Completed.Select(c => c.Moved || job.IsMove && !Exists(c.From) ? new UndoStep("move", c.To, c.From, c.Stamp) : new UndoStep("recycle", c.To, null, c.Stamp))
                                .Concat(dirs.Select(d => new UndoStep("rmdir", d))).ToList();
        });
        int items = steps.Count(s => s.Kind != "rmdir");
        if (items == 0)
        {
            if (job.Journal != null) _ = Task.Run(() => { try { File.Delete(job.Journal); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } });
            return;
        }
        var what = $"{(job.IsMove ? "move" : "copy")} of {(items == 1 ? Path.GetFileName(steps[0].Path) : $"{items:N0} items")}"
                   + (job.Phase == Phase.Done ? "" : job.IsCancelled ? " (cancelled)" : " (stopped)");
        jobUndo[job] = Remember(what, steps, null, job.Journal);
        job.CanUndo = true;
        job.Refresh();
    }

    void JobUndo_Click(object s, RoutedEventArgs e) { if (jobUndo.TryGetValue(JobOf(s), out var entry)) Undo(entry); }

    static void RenamePath(string path, string newName)
    {
        if (Directory.Exists(path)) FileSystem.RenameDirectory(path, newName);
        else FileSystem.RenameFile(path, newName);
    }

    static void MovePath(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// "name.zip", or "name (2).zip" etc. when taken.
    internal static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    // After a command created something: show it selected, or re-run the search the pane is showing.
    static void ShowCreated(PaneView pane, string path)
    {
        if (pane.InSearch) pane.Refresh();
        else pane.Navigate(pane.Dir, Path.GetFileName(path), record: false);
    }

    // ---- Rename many (F2 with several selected) ----
    // Off the UI thread, the count in the status line. Before the first name changes, a record of where each item may
    // end up (its temporary name or its new one, and how it looks) is saved in the Undo history, so if File Labs stops
    // halfway Ctrl+Z can still put every name back; it is replaced by the plain entry once the rename is done.
    async void RenameMany()
    {
        var sel = active.Selected;
        var plan = MultiRenameDialog.Ask(this, sel);
        if (plan == null || plan.Count == 0) return;
        var pane = active;
        Status.Text = $"Renaming {plan.Count:N0} items…";
        var temps = plan.Select(p => TempBeside(p.Path)).ToList();
        var stamps = await Task.Run(() => plan.Select(p => Ferry.Stamp(p.Path)).ToList()); // a rename keeps size, dates and creation time
        var pending = new UndoEntry($"rename of {plan.Count:N0} items (interrupted)", DateTime.Now, plan.SelectMany((p, i) => new[]
        {
            new UndoStep("rename", temps[i], Path.GetFileName(p.Path), stamps[i]),
            new UndoStep("rename", Path.Combine(Path.GetDirectoryName(p.Path), p.NewName), Path.GetFileName(p.Path), stamps[i]),
        }).ToList());
        undo.Add(pending);
        var snapshot = undo.ToList();
        var (reverse, failure, saveError) = await Task.Run(() =>
        {
            var saved = WriteUndoFile(snapshot);
            try { return (RenameAll(plan, temps), (RenameFailed)null, saved); }
            catch (RenameFailed ex) { return (null, ex, saved); }
        });
        undo.Remove(pending);
        if (reverse != null)
        {
            Remember($"rename of {plan.Count} items", reverse.Select((r, i) => new UndoStep("rename", r.Path, r.NewName, stamps[i])), null);
            Status.Text = $"Renamed {plan.Count} items" + (saveError != null ? $" (Undo history not saved: {saveError})" : "");
        }
        else
        {
            // names that couldn't go back are kept as an Undo entry: Ctrl+Z tries them again
            if (failure.NotBack.Count > 0) Remember($"names left after a failed rename ({failure.NotBack.Count})", failure.NotBack.Select(n => new UndoStep("rename", n.Now, n.Name, Ferry.Stamp(n.Now))), null);
            else SaveUndo();
            MessageBox.Show(this, failure.NotBack.Count == 0 ? $"{failure.Message}\n\nEvery name was put back as it was."
                : $"{failure.Message}\n\n{failure.NotBack.Count} item(s) couldn't get their old name back, for example “{Path.GetFileName(failure.NotBack[0].Now)}”. Ctrl+Z tries again.", "Rename");
        }
        pane.Refresh();
    }

    static string TempBeside(string path) => Path.Combine(Path.GetDirectoryName(path), $".filelabs-{Guid.NewGuid():N}");

    /// Why a batch rename stopped. NotBack: items that couldn't get their old name back, where each is now.
    internal sealed class RenameFailed(string message, List<(string Now, string Name)> notBack) : IOException(message)
    {
        public List<(string Now, string Name)> NotBack { get; } = notBack;
    }

    /// Renames in two steps (everything to a temporary name first), so new names may reuse old ones —
    /// renumbering 3,4,5 into 1,2,3 would otherwise collide with itself. A failure puts every item back the
    /// same way, through temporary names (an old name may be held by another item of the batch until that one
    /// moves), and throws RenameFailed naming any that couldn't go back. Returns the plan that reverses it.
    internal static List<(string Path, string NewName)> RenameAll(List<(string Path, string NewName)> plan, List<string> temps = null)
    {
        var at = plan.Select(p => p.Path).ToList(); // where each item is right now
        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var tmp = temps?[i] ?? TempBeside(plan[i].Path);
                MovePath(at[i], tmp);
                at[i] = tmp;
            }
            for (int i = 0; i < plan.Count; i++)
            {
                var final = Path.Combine(Path.GetDirectoryName(plan[i].Path), plan[i].NewName);
                if (File.Exists(final) || Directory.Exists(final)) throw new IOException($"{plan[i].NewName} already exists");
                MovePath(at[i], final);
                at[i] = final;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = 0; i < plan.Count; i++)
                if (at[i] != plan[i].Path && !Path.GetFileName(at[i]).StartsWith(".filelabs-")) // under its new name: parked first
                    try { var tmp = TempBeside(plan[i].Path); MovePath(at[i], tmp); at[i] = tmp; }
                    catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            var notBack = new List<(string Now, string Name)>();
            for (int i = 0; i < plan.Count; i++)
                if (at[i] != plan[i].Path)
                    try { MovePath(at[i], plan[i].Path); at[i] = plan[i].Path; }
                    catch (Exception again) when (again is IOException or UnauthorizedAccessException) { notBack.Add((at[i], Path.GetFileName(plan[i].Path))); }
            throw new RenameFailed(ex.Message, notBack);
        }
        return plan.Select((p, i) => (at[i], Path.GetFileName(p.Path))).ToList();
    }

    // ---- ZIP ----
    async void Compress()
    {
        var sel = active.Selected;
        if (sel.Count == 0) return;
        var pane = active;
        var stem = sel.Count == 1 ? (sel[0].IsDir ? sel[0].Name : Path.GetFileNameWithoutExtension(sel[0].Name)) : Path.GetFileName(pane.Dir.TrimEnd('\\'));
        var zip = Unique(Path.Combine(pane.Dir, (stem == "" ? "Archive" : stem) + ".zip"));
        var paths = sel.Select(e => e.Path).ToList();
        Status.Text = $"Compressing to {Path.GetFileName(zip)}…";
        var error = await Task.Run(() =>
        {
            try
            {
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var p in paths)
                {
                    if (File.Exists(p)) { archive.CreateEntryFromFile(p, Path.GetFileName(p)); continue; }
                    var parent = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p; // entries keep the folder's own name
                    archive.CreateEntry(Path.GetRelativePath(parent, p).Replace('\\', '/') + "/");
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var f in new DirectoryInfo(p).EnumerateFileSystemInfos("*", opts))
                    {
                        var name = Path.GetRelativePath(parent, f.FullName).Replace('\\', '/');
                        if (f is DirectoryInfo) archive.CreateEntry(name + "/"); // keeps empty folders
                        else archive.CreateEntryFromFile(f.FullName, name);
                    }
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(zip); } catch (IOException) { } // no half-written archive left behind
                return ex.Message;
            }
        });
        if (error != null) { Status.Text = "Compress failed: " + error; return; }
        Status.Text = $"Created {Path.GetFileName(zip)}";
        Remember($"zip {Path.GetFileName(zip)}", Made(zip));
        ShowCreated(pane, zip);
    }

    async void Extract(Entry e, bool toFolder)
    {
        var pane = active;
        var here = Path.GetDirectoryName(e.Path);
        var dest = toFolder ? Unique(Path.Combine(here, Path.GetFileNameWithoutExtension(e.Name))) : here;
        Status.Text = $"Extracting {e.Name}…";
        List<string> created = null;
        var error = await Task.Run(() =>
        {
            try
            {
                // "Here" must not overwrite anything: check first rather than stop halfway through.
                var top = Archive.List(e.Path, "").Select(x => x.Name).ToList();
                var clash = top.FirstOrDefault(t => File.Exists(Path.Combine(dest, t)) || Directory.Exists(Path.Combine(dest, t)));
                if (clash != null) return $"{clash} already exists here — use Extract to folder";
                created = toFolder ? new() { dest } : top.Select(t => Path.Combine(dest, t)).ToList();
                Directory.CreateDirectory(dest);
                Archive.Extract(e.Path, top, "", dest); // refuses entries that would land outside dest
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { return ex.Message; }
        });
        if (error != null) { Status.Text = "Extract failed: " + error; return; }
        Status.Text = $"Extracted {e.Name}";
        Remember($"extract {e.Name}", created.Select(Made), null);
        ShowCreated(pane, toFolder ? dest : e.Path);
    }

    // ---- Inside an archive (1.0.60): a ZIP or TAR opens in the pane like a folder ----
    // Its rows aren't files on disk, so the usual commands don't get them (PaneView.Selected is empty there); these do.
    List<Entry> ArchiveSelection(PaneView p) => p.List.SelectedItems.Cast<Entry>().Where(x => !x.IsUp).ToList();

    async void ExtractFromArchive(PaneView p, string dest)
    {
        var sel = ArchiveSelection(p);
        if (sel.Count == 0 || p.ArchiveRoot is not var (archive, inner)) return;
        if (PaneView.IsVirtual(dest) || !Directory.Exists(dest)) { Status.Text = "Extract into a folder on disk"; return; }
        Status.Text = $"Extracting {sel.Count} item(s)…";
        var names = sel.Select(x => x.Name).ToList();
        (int Written, int Skipped) n = default;
        string error = null;
        try { n = await Task.Run(() => Archive.Extract(archive, names, inner, dest)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { error = ex.Message; }
        Status.Text = error != null ? "Extract failed: " + error
            : $"Extracted {n.Written} file(s) to {dest}" + (n.Skipped > 0 ? $"  ·  {n.Skipped} already there, left as they were" : "");
        foreach (var pane in new[] { Left, Right }) if (pane.Dir.Equals(dest, StringComparison.OrdinalIgnoreCase)) pane.Refresh();
    }

    void ExtractFromArchiveTo(PaneView p)
    {
        var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to which folder?" };
        if (pick.ShowDialog(this) == true) ExtractFromArchive(p, pick.FolderName);
    }

    /// A file in an archive opens from a copy in the temp folder (refreshed each time), as Explorer does.
    async void OpenFromArchive(PaneView p, Entry e)
    {
        if (p.ArchiveRoot is not var (archive, inner)) return;
        var temp = Path.Combine(Path.GetTempPath(), "FileLabs", "open", $"{(uint)archive.ToLowerInvariant().GetHashCode():x8}", inner.Replace('/', '\\'));
        string error = null;
        try { await Task.Run(() => { Directory.CreateDirectory(temp); var f = Path.Combine(temp, e.Name); if (File.Exists(f)) File.Delete(f); Archive.Extract(archive, new[] { e.Name }, inner, temp); }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { error = ex.Message; }
        if (error != null) { Status.Text = "Couldn't open it: " + error; return; }
        try { Process.Start(new ProcessStartInfo(Path.Combine(temp, e.Name)) { UseShellExecute = true, WorkingDirectory = temp }); }
        catch (System.ComponentModel.Win32Exception ex) { Status.Text = ex.Message; }
    }

    async void DeleteFromArchive(PaneView p)
    {
        var sel = ArchiveSelection(p);
        if (sel.Count == 0 || p.ArchiveRoot is not var (archive, inner)) return;
        if (!Archive.CanChange(archive)) { Status.Text = "Only a ZIP can be changed in place; extract from this one instead"; return; }
        if (PromptDialog.Choose(this, $"Delete {sel.Count} item(s) from {Path.GetFileName(archive)}?", "They are taken out of the archive for good: the Recycle Bin doesn't hold them.", "Delete", "Cancel") != 0) return;
        string error = null;
        try { await Task.Run(() => Archive.Delete(archive, sel.Select(x => x.Name).ToList(), inner)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { error = ex.Message; }
        Status.Text = error != null ? "Delete failed: " + error : $"Deleted {sel.Count} item(s) from {Path.GetFileName(archive)}";
        p.Refresh();
    }

    /// Files dropped or pasted into a ZIP shown in a pane are added at the folder shown; names already there are left alone.
    async void AddToArchive(PaneView p, List<string> files, string into)
    {
        if (PaneView.ArchiveOf(into) is not var (archive, inner)) return;
        if (!Archive.CanChange(archive)) { Status.Text = "Only a ZIP can be added to; this archive can only be extracted from"; return; }
        Status.Text = $"Adding to {Path.GetFileName(archive)}…";
        (int Added, int Skipped) n = default;
        string error = null;
        try { n = await Task.Run(() => Archive.Add(archive, inner, files)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { error = ex.Message; }
        Status.Text = error != null ? "Couldn't add: " + error
            : $"Added {n.Added} file(s) to {Path.GetFileName(archive)}" + (n.Skipped > 0 ? $"  ·  {n.Skipped} already in it, left as they were" : "");
        p.Refresh();
    }

    // ---- Disk images: Windows mounts them (as Explorer's Mount does), and the pane opens the new drive ----
    internal static readonly HashSet<string> DiskImageExt = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".vhd", ".vhdx" };

    async void MountImage(string path)
    {
        var before = DriveInfo.GetDrives().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the class, not the file's own: here .vhd belonged to VirtualBox, which has no Mount
        var cls = Path.GetExtension(path).ToLowerInvariant() is ".vhd" or ".vhdx" ? "Windows.VhdFile" : "Windows.IsoFile";
        var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = SEE_MASK_CLASSNAME, lpVerb = "mount", lpFile = path, lpClass = cls, nShow = 1,
                                          hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle };
        if (!ShellExecuteEx(ref info)) { if (Marshal.GetLastWin32Error() != 1223) Status.Text = $"Couldn't mount {Path.GetFileName(path)}"; return; }
        Status.Text = $"Mounting {Path.GetFileName(path)}…";
        var pane = active;
        for (int i = 0; i < 40; i++) // up to 10 s: a VHD waits for its UAC prompt
        {
            await Task.Delay(250);
            if (DriveInfo.GetDrives().FirstOrDefault(d => !before.Contains(d.Name)) is { } drive)
            {
                RefreshDrives();
                pane.NewTab();
                pane.Navigate(drive.Name);
                Status.Text = $"{Path.GetFileName(path)} is {drive.Name.TrimEnd('\\')} — eject it from the drive's right-click menu";
                return;
            }
        }
        Status.Text = $"{Path.GetFileName(path)} is mounted (it was already, or it has no drive letter)";
    }

    const uint SEE_MASK_CLASSNAME = 0x1;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string lpVerb, lpFile, lpParameters, lpDirectory; public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIconOrMonitor, hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    // ---- Compare the two panes (Shift+F2), Total Commander style ----
    // Selects, on each side, what is missing on the other side or newer than its twin (same date but a
    // different size selects both). Folders are compared by name only. F5 then copies the selection over.
    void ComparePanes()
    {
        if (Left.InSearch || Right.InSearch) { Status.Text = "Compare works on two folders, not on search results"; return; }
        var l = Left.Items.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var r = Right.Items.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        static bool Differs(Entry a, Dictionary<string, Entry> other)
        {
            if (!other.TryGetValue(a.Name, out var b) || a.IsDir != b.IsDir) return true;
            if (a.IsDir) return false;
            var seconds = (a.Modified - b.Modified).TotalSeconds;
            return seconds > 2 || Math.Abs(seconds) <= 2 && a.Size != b.Size; // 2 s: FAT and some shares round times
        }
        Left.SetSelection(e => Differs(e, r));
        Right.SetSelection(e => Differs(e, l));
        int nl = Left.Selected.Count, nr = Right.Selected.Count;
        Status.Text = nl + nr == 0 ? "The two folders have the same files" : $"Different: {nl} on the left, {nr} on the right  ·  F5 copies the selection across";
    }

    // ---- Checksum ----
    async void Checksum(Entry e)
    {
        Status.Text = $"Calculating SHA-256 of {e.Name}…";
        string hash;
        try { hash = await Task.Run(() => { using var s = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; return; }
        Status.Text = "";
        // A hash copied from a download page is compared automatically.
        var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        var label = clip.Length == 64 && clip.All(Uri.IsHexDigit)
            ? clip.Equals(hash, StringComparison.OrdinalIgnoreCase) ? "SHA-256 — matches the hash on the clipboard ✓" : "SHA-256 — does NOT match the hash on the clipboard ✗"
            : "SHA-256 (copy a published hash first and it is compared automatically)";
        if (PromptDialog.Ask(this, e.Name, label, hash, "Copy", selectAll: true) != null) Clipboard.SetText(hash);
    }

    // ---- Shortcuts ----
    void CreateShortcuts()
    {
        var pane = active;
        string last = null;
        foreach (var e in pane.Selected)
        {
            var lnk = Unique(Path.Combine(pane.Dir, e.Name + " - Shortcut.lnk"));
            try { SaveShortcut(lnk, e.Path, e.IsDir ? e.Path : Path.GetDirectoryName(e.Path)); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { Status.Text = ex.Message; return; }
            Remember($"shortcut {Path.GetFileName(lnk)}", Made(lnk));
            last = lnk;
        }
        if (last != null) ShowCreated(pane, last);
    }

    internal static void SaveShortcut(string lnk, string target, string workingDir)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDir);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    /// What a shortcut points at, or the path itself when it is not one or points at nothing on disk.
    /// QuickLook is handed the document behind a shortcut, as Explorer's Space shows it (Frostpane does the same).
    internal static string ShortcutTarget(string path) => ShortcutTarget(path, out _);

    /// The folder a shortcut points at, or null: a middle-click opens it in a tab and a drop on the shortcut
    /// goes into it, as in Explorer.
    internal static string ShortcutFolder(string path) => ShortcutTarget(path, out bool folder) is var t && folder && t != path ? t : null;

    static string ShortcutTarget(string path, out bool folder)
    {
        folder = false;
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        var link = (IShellLinkW)new ShellLink();
        var buffer = Marshal.AllocHGlobal(2 * 1024);
        var found = Marshal.AllocHGlobal(592); // WIN32_FIND_DATAW: what the shortcut remembers about its target
        try
        {
            Marshal.WriteInt16(buffer, 0); // stays empty for a shortcut to something that is not a file (Control Panel…)
            Marshal.WriteInt32(found, 0);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
            link.GetPath(buffer, 1024, found, 0);
            var target = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrEmpty(target)) return path;
            // a network target is not checked: a server that is off answers after 21 s, and Space runs on the UI
            // thread. Whether it is a folder comes from the shortcut itself (dwFileAttributes, recorded when made).
            if (PaneView.IsRemote(target)) { folder = (Marshal.ReadInt32(found) & (int)FileAttributes.Directory) != 0; return target; }
            folder = Directory.Exists(target);
            return folder || File.Exists(target) ? target : path;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException) { return path; } // not a readable shortcut
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(found); Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath(IntPtr file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation(IntPtr path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    // ---- Run as administrator ----
    internal static readonly HashSet<string> Runnable = Set(".exe", ".bat", ".cmd", ".msi", ".com");

    void RunAsAdmin(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) }); }
        catch (System.ComponentModel.Win32Exception) { } // the UAC prompt was declined
    }
}

/// ZIP and TAR (also .tar.gz / .tgz) without anything but .NET: listing a folder inside, extracting, and for a ZIP adding and
/// deleting. Paths inside are '/'-separated, relative to the archive; `inner` is the folder shown ("" = its top).
static class Archive
{
    public static bool IsArchiveName(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    public static bool CanChange(string archive) => archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public sealed record Item(string Name, bool IsDir, long Size, DateTime Modified);
    record Raw(string Path, bool IsDir, long Size, DateTime Modified);

    // the last archives read, by their write time: going in and out of folders in one doesn't read it again
    static readonly Dictionary<string, (DateTime Stamp, List<Raw> Entries)> read = new(StringComparer.OrdinalIgnoreCase);

    static List<Raw> Entries(string archive)
    {
        var stamp = File.GetLastWriteTimeUtc(archive);
        lock (read) if (read.TryGetValue(archive, out var hit) && hit.Stamp == stamp) return hit.Entries;
        var list = new List<Raw>();
        Visit(archive, (path, isDir, size, modified, _) => { list.Add(new(path, isDir, size, modified)); return false; });
        lock (read) { if (read.Count > 8) read.Clear(); read[archive] = (stamp, list); }
        return list;
    }

    /// An entry's path without the "./" tar puts in front ("./docs/a.txt" → "docs/a.txt"), or a leading slash; a name that
    /// itself starts with a dot (.gitignore) keeps it. "." alone is the archive's top: "".
    internal static string Clean(string path)
    {
        while (path.StartsWith("./")) path = path[2..];
        path = path.TrimStart('/').TrimEnd('/');
        return path == "." ? "" : path;
    }

    /// Every entry, in order; `open` gives its content (null for a folder). Stops when the callback says so.
    static void Visit(string archive, Func<string, bool, long, DateTime, Func<Stream>, bool> each)
    {
        if (CanChange(archive))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                bool dir = name.EndsWith('/');
                name = Clean(name);
                if (name == "") continue; // the "./" a tar-made ZIP starts with
                if (each(name, dir, e.Length, e.LastWriteTime.LocalDateTime, dir ? null : e.Open)) return;
            }
            return;
        }
        using var file = File.OpenRead(archive);
        using Stream data = archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? file : new GZipStream(file, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(data);
        while (tar.GetNextEntry() is { } e)
        {
            bool dir = e.EntryType == System.Formats.Tar.TarEntryType.Directory;
            bool regular = e.EntryType is System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile or System.Formats.Tar.TarEntryType.ContiguousFile;
            if (!dir && !regular) continue; // links, devices: nothing to show or extract
            var name = Clean(e.Name.Replace('\\', '/'));
            if (name == "") continue;
            if (each(name, dir, e.Length, e.ModificationTime.LocalDateTime, dir ? null : () => e.DataStream ?? Stream.Null)) return;
        }
    }

    /// What is directly in `inner`: folders (also the ones only implied by a deeper path) and files.
    public static List<Item> List(string archive, string inner)
    {
        var prefix = inner == "" ? "" : inner.TrimEnd('/') + "/";
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries(archive))
        {
            if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e.Path.Length == prefix.Length) continue;
            var rest = e.Path[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash >= 0) { var dir = rest[..slash]; if (!items.ContainsKey(dir)) items[dir] = new(dir, true, -2, e.Modified); }
            else if (e.IsDir) items[rest] = new(rest, true, -2, e.Modified);
            else items[rest] = new(rest, false, e.Size, e.Modified);
        }
        return items.Values.ToList();
    }

    /// Writes `names` (files or folders in `inner`) into `dest`, keeping the folders under them. A file already there is left as
    /// it was (counted as skipped); an entry whose path would end outside `dest` is refused.
    public static (int Written, int Skipped) Extract(string archive, IReadOnlyCollection<string> names, string inner, string dest)
    {
        var prefix = inner == "" ? "" : inner.TrimEnd('/') + "/";
        var picked = names.Select(n => prefix + n).ToList();
        var root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        int written = 0, skipped = 0;
        Visit(archive, (path, isDir, size, modified, open) =>
        {
            if (!picked.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))) return false;
            var target = Path.GetFullPath(Path.Combine(dest, path[prefix.Length..].Replace('/', '\\')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException($"{path} would land outside the folder");
            if (isDir) { Directory.CreateDirectory(target); return false; }
            if (File.Exists(target)) { skipped++; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var from = open();
            try { using var to = File.Create(target); from.CopyTo(to); }
            finally { if (CanChange(archive)) from.Dispose(); } // a TAR entry's stream belongs to the reader, which moves on past it
            File.SetLastWriteTime(target, modified);
            written++;
            return false;
        });
        return (written, skipped);
    }

    /// Takes `names` (files or folders in `inner`) out of a ZIP.
    public static void Delete(string archive, IReadOnlyCollection<string> names, string inner)
    {
        var prefix = inner == "" ? "" : inner.TrimEnd('/') + "/";
        var picked = names.Select(n => prefix + n).ToList();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            foreach (var e in zip.Entries.ToList())
            {
                var path = Clean(e.FullName.Replace('\\', '/'));
                if (picked.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))) e.Delete();
            }
        lock (read) read.Remove(archive);
    }

    /// Adds files and folders (with what is in them) to a ZIP at `inner`; a name already in it is left as it was.
    public static (int Added, int Skipped) Add(string archive, string inner, IEnumerable<string> paths)
    {
        var prefix = inner == "" ? "" : inner.TrimEnd('/') + "/";
        int added = 0, skipped = 0;
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var have = zip.Entries.Select(e => Clean(e.FullName.Replace('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            void One(string file, string name)
            {
                if (have.Contains(name)) { skipped++; return; }
                zip.CreateEntryFromFile(file, name);
                added++;
            }
            foreach (var p in paths)
            {
                if (File.Exists(p)) { One(p, prefix + Path.GetFileName(p)); continue; }
                if (!Directory.Exists(p)) continue;
                var parent = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    One(f, prefix + Path.GetRelativePath(parent, f).Replace('\\', '/'));
            }
        }
        lock (read) read.Remove(archive);
        return (added, skipped);
    }
}

