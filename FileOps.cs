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
    // ---- Undo (Ctrl+Z) ----
    // Renames, new folders/files, zips, shortcuts, and copies/moves that met no conflicts. A job that
    // had conflicts is not undoable: its targets are not simply "what it created" (replaced files,
    // "keep both" copies), so undoing could remove files that were there before. Deletes need no
    // entry here — the Recycle Bin is their undo.
    readonly List<(string What, Action Undo)> undo = new();

    void Remember(string what, Action undoIt)
    {
        undo.Add((what, undoIt));
        if (undo.Count > 50) undo.RemoveAt(0);
    }

    void Undo()
    {
        if (undo.Count == 0) { Status.Text = "Nothing to undo"; return; }
        var (what, run) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        if (Run(run)) Status.Text = "Undone: " + what;
    }

    readonly HashSet<Job> undoJobs = new(); // moves that undo a move: not undoable themselves

    // Called when a Ferry job ends.
    void RememberJob(Job job)
    {
        if (undoJobs.Remove(job) || job.Phase != Phase.Done || job.Conflicts.Count > 0 || !job.Errors.IsEmpty) return;
        var made = job.Sources.Select(s => (Source: s, Target: Path.Combine(job.Destination, Path.GetFileName(s.TrimEnd('\\')))))
                              .Where(x => Path.GetFileName(x.Target) != "").ToList();
        if (made.Count == 0) return;
        string what = $"{(job.IsMove ? "move" : "copy")} of {(made.Count == 1 ? Path.GetFileName(made[0].Target) : $"{made.Count} items")}";
        if (!job.IsMove) Remember(what, () => Recycle(made.Select(x => x.Target).ToList(), UIOption.OnlyErrorDialogs));
        else Remember(what, () =>
        {
            foreach (var back in made.GroupBy(x => Path.GetDirectoryName(x.Source)))
                undoJobs.Add(Submit(back.Select(x => x.Target).ToList(), back.Key, move: true));
        });
    }

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
    void RenameMany()
    {
        var sel = active.Selected;
        var plan = MultiRenameDialog.Ask(this, sel);
        if (plan == null || plan.Count == 0) return;
        var pane = active;
        List<(string Path, string NewName)> reverse = null;
        if (Run(() => reverse = RenameAll(plan)))
        {
            Remember($"rename of {plan.Count} items", () => RenameAll(reverse));
            Status.Text = $"Renamed {plan.Count} items";
        }
        pane.Refresh();
    }

    /// Renames in two steps (everything to a temporary name first), so new names may reuse old ones —
    /// renumbering 3,4,5 into 1,2,3 would otherwise collide with itself. Any failure puts every file
    /// back where it was and rethrows. Returns the plan that reverses it.
    static List<(string Path, string NewName)> RenameAll(List<(string Path, string NewName)> plan)
    {
        var at = plan.Select(p => p.Path).ToList(); // where each item is right now
        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var tmp = Path.Combine(Path.GetDirectoryName(plan[i].Path), $".filelabs-{Guid.NewGuid():N}");
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
                if (at[i] != plan[i].Path)
                    try { MovePath(at[i], plan[i].Path); } catch (Exception back) when (back is IOException or UnauthorizedAccessException) { }
            throw;
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
        Remember($"zip {Path.GetFileName(zip)}", () => Recycle(new() { zip }, UIOption.OnlyErrorDialogs));
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
                using (var zip = ZipFile.OpenRead(e.Path))
                {
                    var top = zip.Entries.Select(x => x.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                                         .Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var clash = top.FirstOrDefault(t => File.Exists(Path.Combine(dest, t)) || Directory.Exists(Path.Combine(dest, t)));
                    if (clash != null) return $"{clash} already exists here — use Extract to folder";
                    created = toFolder ? new() { dest } : top.Select(t => Path.Combine(dest, t)).ToList();
                }
                ZipFile.ExtractToDirectory(e.Path, dest); // refuses entries that would land outside dest
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return ex.Message; }
        });
        if (error != null) { Status.Text = "Extract failed: " + error; return; }
        Status.Text = $"Extracted {e.Name}";
        Remember($"extract {e.Name}", () => Recycle(created, UIOption.OnlyErrorDialogs));
        ShowCreated(pane, toFolder ? dest : e.Path);
    }

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
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(e.Path);
                link.SetWorkingDirectory(e.IsDir ? e.Path : Path.GetDirectoryName(e.Path));
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { Status.Text = ex.Message; return; }
            finally { Marshal.ReleaseComObject(link); }
            Remember($"shortcut {Path.GetFileName(lnk)}", () => Recycle(new() { lnk }, UIOption.OnlyErrorDialogs));
            last = lnk;
        }
        if (last != null) ShowCreated(pane, last);
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
    static readonly HashSet<string> Runnable = Set(".exe", ".bat", ".cmd", ".msi", ".com");

    void RunAsAdmin(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) }); }
        catch (System.ComponentModel.Win32Exception) { } // the UAC prompt was declined
    }
}
