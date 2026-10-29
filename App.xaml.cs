using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace FileExplorer;

// A second launch (a folder double-click, the Start menu, Win+E without the agent) only hands its folder to the running
// window. Done here, before WPF and App.xaml are loaded, that costs 86 ms instead of 185 (190 instead of 381 on a
// throttled laptop-like CPU). Switches (--agent, --selftest, …) always take the full start.
static class Program
{
    /// The folder of a portable copy: the exe's own, when a portable.txt sits beside it (the portable zip has one).
    /// Before PipeName, which reads it: static fields start in the order they are written.
    internal static readonly string PortableDir = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")) ? AppContext.BaseDirectory.TrimEnd('\\') : null;

    // a test copy (FILELABS_PROFILE) gets a single instance per profile folder, so two test copies don't hand off to each
    // other. Read from the environment here, not from Settings, whose types would load WPF.
    // A portable copy (portable.txt beside the exe) is its own instance too: started while the installed one runs, it
    // would otherwise hand its start over and never open.
    internal static readonly string PipeName = "FileLabs-" + Environment.UserName
        + ((Environment.GetEnvironmentVariable("FILELABS_PROFILE") is { Length: > 0 } p ? p : PortableDir) is { } own
            ? "-profile-" + System.IO.Hashing.XxHash32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(own.ToLowerInvariant())).ToString("x8") : "");

    [STAThread]
    static void Main(string[] args)
    {
        if (!args.Any(a => a.StartsWith("--")) && Running)
        {
            AllowSetForegroundWindow(-1); // we were just launched by the user, so we may pass focus on
            if (HandOff(PathArg(args))) return;
            // the running window didn't answer (hung or closing): start normally
        }
        App.Main();
    }

    /// True while a File Labs window is open for this user (it holds the single-instance mutex).
    internal static bool Running
    {
        get
        {
            if (!Mutex.TryOpenExisting(PipeName, out var m)) return false;
            m.Dispose();
            return true;
        }
    }

    /// The path a launch asks for, "" if none. Explorer's own form too, which "Open path" in Everything and other
    /// programs pass to a file manager: `/select,"C:\x\file.txt"` (also behind `/n,` and the like). Windows keeps
    /// `/select,"C:\a b\f.txt"` as one argument, but a space after the comma, or a path with spaces and no
    /// quotes, arrives in pieces: they are put back together.
    internal static string PathArg(IEnumerable<string> args)
    {
        var s = string.Join(" ", args.Where(a => !a.StartsWith("--"))).Trim();
        while (s.StartsWith('/') && s.IndexOf(',') is > 0 and var comma) s = s[(comma + 1)..].TrimStart();
        return s.Trim('"').Trim();
    }

    /// Passes a folder to the running window ("" = just come to the front). False if it didn't answer.
    internal static bool HandOff(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(path);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int pid);
}

public partial class App : Application
{
    public App() => Motion.Hook(); // before any window is made

    // `FileLabs.exe --selftest`: runs the Ferry engine on real files in a temp dir, exit code 0 = pass.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest()); return; }

        // Window-less Win+E catcher (see Agent.cs); never opens the main window.
        if (e.Args.Contains("--agent"))
        {
            int code = Agent.Run(this);
            if (code >= 0) Shutdown(code);
            return;
        }

        // Called by the installer / uninstaller: same code path as the switch in Settings.
        if (e.Args.Contains("--set-default") || e.Args.Contains("--unset-default"))
        {
            Settings.Load(); // "Win+E opens File Labs" may be off
            try { Settings.SetDefaultFileManager(e.Args.Contains("--set-default")); Shutdown(0); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Shutdown(1); }
            return;
        }

        // Run by the installer as the signed-in user after the files are in place (runasoriginaluser): the data from
        // before 1.0.48 moved if the installer couldn't, the HKCU entries pointed at this exe, the card for the sisters.
        if (e.Args.Contains("--installed"))
        {
            if (!Program.Running) DataFolder.Migrate();
            DataFolder.Repoint();
            Sisters.WriteCard();
            Shutdown(0);
            return;
        }

        // Single instance: a second launch (folder double-click, Win+E) hands its path to the
        // running window, which opens it as a new tab, then exits.
        instance = new Mutex(true, Program.PipeName, out bool first);
        bool hidden = e.Args.Contains("--tray"); // started at sign-in (Settings → Windows integration)
        if (!first && hidden) { Shutdown(); return; } // already running: nothing to start
        if (!first)
        {
            Program.AllowSetForegroundWindow(-1); // we were just launched by the user, so we may pass focus on
            if (Program.HandOff(Program.PathArg(e.Args))) { Shutdown(); return; }
            // first instance hung or closing: just start normally
        }
        else
        {
            DataFolder.Migrate(); // the data from before 1.0.48, before anything reads it (Settings.cs)
            _ = Listen();
        }
        Settings.Load(); // before any window: the panes read column widths while being built
        UseTipDelay();
        ShellMenu.FollowSystemTheme(); // the Windows right-click menu dark in dark mode
        Task.Run(() => { DataFolder.Repoint(); Sisters.WriteCard(); }); // the ecosystem card, so sister apps find this copy (Sisters.cs)
        // folder sizes from previous sessions are read after the first frame, or on first need (SizeCache.StartLoading)
        Exit += (_, _) => SizeCache.Save();
        var sizeSaver = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        sizeSaver.Tick += (_, _) => Task.Run(SizeCache.SaveIfChanged); // off the UI thread: up to 50 000 lines
        sizeSaver.Start();
        base.OnStartup(e);
        var window = new MainWindow(); // opened here, not via StartupUri, so --agent etc. never build it
        if (hidden) window.PrepareHiddenStart();
        window.Show();
    }

    Mutex instance;

    /// Settings: every tooltip waits this long, 1.5 s unless changed. Up to 1.0.50 it was WPF's own 1 s, and once one tooltip
    /// had shown, the next came after 0.1 s (BetweenShowDelay): running the pointer down a list, every row popped one. Now
    /// each waits the whole time. A tooltip reads its wait from its own element, so the default for all of them is changed,
    /// once and before the first window: it can't be changed again while running, hence "from the next start".
    internal static void UseTipDelay()
    {
        foreach (var type in new[] { typeof(FrameworkElement), typeof(FrameworkContentElement) })
        {
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(Settings.TipDelay));
            System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(0));
        }
    }

    async Task Listen()
    {
        while (true)
        {
            using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            string path;
            try { path = await new StreamReader(server).ReadLineAsync(); }
            catch (IOException) { continue; } // client went away
            if (MainWindow is MainWindow w) w.OpenFromOutside(path);
        }
    }

    static int SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "filelabs-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "src", "tree", "sub")).Parent.Parent.FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            File.WriteAllText(Path.Combine(src, "tree", "a.txt"), "hello");
            File.WriteAllBytes(Path.Combine(src, "tree", "sub", "b.bin"), new byte[3_000_000]);

            // copy a folder tree
            var j = Run(Ferry.Submit(new[] { Path.Combine(src, "tree") }, dst, move: false));
            Check(j.Phase == Phase.Done && j.DoneFiles == 2 && j.Errors.IsEmpty, "copy tree");
            Check(File.ReadAllText(Path.Combine(dst, "tree", "a.txt")) == "hello", "copied content");
            Check(new FileInfo(Path.Combine(dst, "tree", "sub", "b.bin")).Length == 3_000_000, "copied size");

            // same copy again → conflicts; "keep both" renames
            j = Ferry.Submit(new[] { Path.Combine(src, "tree") }, dst, move: false);
            Wait(() => j.Phase == Phase.Conflicts);
            Check(j.Conflicts.Count == 2, "two conflicts found in one pass");
            j.Resolve(Choice.Rename);
            Run(j);
            Check(File.Exists(Path.Combine(dst, "tree", "a (2).txt")), "keep both → a (2).txt");

            // move within the same volume is a rename; source is gone afterwards
            var mv = Directory.CreateDirectory(Path.Combine(root, "moved")).FullName;
            j = Run(Ferry.Submit(new[] { Path.Combine(src, "tree") }, mv, move: true));
            Check(j.Phase == Phase.Done && !Directory.Exists(Path.Combine(src, "tree")) && File.Exists(Path.Combine(mv, "tree", "a.txt")), "same-volume move");
            Check(j.Notes.Contains("Moved within the same drive"), "move took the rename path");

            // move a folder onto a folder of the same name: merged file by file, never renamed over it,
            // and not undoable (undo would take the files that were already there with it)
            var merge = Directory.CreateDirectory(Path.Combine(root, "merge")).FullName;
            Directory.CreateDirectory(Path.Combine(merge, "tree"));
            File.WriteAllText(Path.Combine(merge, "tree", "kept.txt"), "was here");
            var incoming = Directory.CreateDirectory(Path.Combine(root, "incoming", "tree")).FullName;
            File.WriteAllText(Path.Combine(incoming, "new.txt"), "arrived");
            // a junction inside the moved folder, pointing at a folder with an empty subfolder
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside", "empty")).Parent.FullName;
            var link = Path.Combine(incoming, "link");
            bool junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(5000) && Directory.Exists(link);
            j = Run(Ferry.Submit(new[] { incoming }, merge, move: true));
            Check(j.Phase == Phase.Done && j.Merged && j.Errors.IsEmpty, "merge move finished");
            Check(File.Exists(Path.Combine(merge, "tree", "kept.txt")) && File.Exists(Path.Combine(merge, "tree", "new.txt")), "merge keeps both folders' files");
            if (junction)
            {
                Check(Directory.Exists(Path.Combine(outside, "empty")), "a junction's target is never emptied");
                Check(Directory.Exists(link), "the junction itself stays where it was");
                Directory.Delete(link); // the link only, so the clean-up below has plain folders to remove
            }

            // folder sizes: every folder a scan passes through is remembered, and a change drops the
            // totals above it
            var sizes = Path.Combine(root, "sizes");
            var deep = Directory.CreateDirectory(Path.Combine(sizes, "a", "b")).FullName;
            File.WriteAllBytes(Path.Combine(deep, "x.bin"), new byte[1000]);
            // what a listing compares against: the folder's entry in its parent
            DateTime Stamp(string p) => new DirectoryInfo(Path.GetDirectoryName(p)).EnumerateDirectories(Path.GetFileName(p)).First().LastWriteTimeUtc;
            Check(PaneView.Measure(new DirectoryInfo(sizes), default) == 1000, "folder size");
            Check(SizeCache.Get(deep, Stamp(deep)) == 1000, "subfolders remembered");
            Check(SizeCache.Get(deep, DateTime.UtcNow) == -1, "a changed folder is forgotten");
            var mid = Path.Combine(sizes, "a");
            Check(SizeCache.Get(mid, Stamp(mid)) == -1, "and so is every total above it");
            File.WriteAllBytes(Path.Combine(deep, "y.bin"), new byte[500]);
            Check(PaneView.Measure(new DirectoryInfo(sizes), default) == 1500, "measured again after the change");

            // names sort the way Explorer sorts them: symbols first, numbers by value
            var order = string.Join(",", new[] { "b", "file10", "_drafts", "file2", "A", "(old)" }.OrderBy(x => x, ExplorerOrder.Instance));
            Check(order == "(old),_drafts,A,b,file2,file10", "Explorer name order, got " + order);

            // copy with verification on
            Settings.Verify = true;
            var vdst = Directory.CreateDirectory(Path.Combine(root, "verified")).FullName;
            j = Run(Ferry.Submit(new[] { Path.Combine(mv, "tree") }, vdst, move: false));
            Settings.Verify = false;
            Check(j.Phase == Phase.Done && j.Errors.IsEmpty && j.DoneBytes == j.TotalBytes && j.TotalBytes == 2 * (5 + 3_000_000), "verify pass reads both copies");

            // cancel before it starts
            j = Ferry.Submit(new[] { Path.Combine(mv, "tree") }, dst, move: false);
            j.Cancel();
            Run(j);
            Check(j.Phase == Phase.Cancelled, "cancel");

            // Recycle Bin record ($I file, Windows 10+ layout): version 2, size, FILETIME, length, path
            var when = new DateTime(2026, 9, 21, 7, 30, 0, DateTimeKind.Local);
            var original = @"C:\Users\someone\Documents\report final.docx";
            var info = Path.Combine(root, "$IABC123.docx");
            File.WriteAllBytes(info, BitConverter.GetBytes(2L).Concat(BitConverter.GetBytes(12345L)).Concat(BitConverter.GetBytes(when.ToFileTime()))
                .Concat(BitConverter.GetBytes(original.Length + 1)).Concat(System.Text.Encoding.Unicode.GetBytes(original + "\0")).ToArray());
            Check(RecycleBinWindow.ParseInfo(info) is { } rec && rec.Path == original && rec.Size == 12345 && rec.Deleted == when, "recycle bin record");

            // a sister app is found through its uninstall entry, which names the exe and an icon index
            var self = Environment.ProcessPath;
            Check(FileExplorer.MainWindow.ExeOf(self + ",0") == self && FileExplorer.MainWindow.ExeOf($"\"{self}\",0") == self
                && FileExplorer.MainWindow.ExeOf(Path.Combine(root, "gone.exe") + ",0") == null && FileExplorer.MainWindow.ExeOf(null) == null, "installed app from its uninstall entry");

            // ecosystem cards: what this app writes reads back; garbage, another schema or a gone exe count as not installed
            var card = Path.Combine(root, "card.json");
            Sisters.Write(card, new { schema = 1, updated = "2026-10-15T09:00:00Z", exe = self });
            Check(File.ReadAllBytes(card)[0] == '{' && Sisters.ExeFromCard(card) == self, "card written without BOM and read back");
            Sisters.Write(card, new { schema = 1, exe = Path.Combine(root, "gone.exe") });
            Check(Sisters.ExeFromCard(card) == null, "card of an uninstalled app");
            Sisters.Write(card, new { schema = 2, exe = self });
            Check(Sisters.ExeFromCard(card) == null, "card with another schema");
            File.WriteAllText(card, "{ not json");
            Check(Sisters.ExeFromCard(card) == null && Sisters.ExeFromCard(Path.Combine(root, "none.json")) == null, "broken or missing card");

            // QuickLook is handed what a shortcut points at; anything else, or a shortcut that can't be read, as it is
            var lnk = Path.Combine(root, "record.lnk");
            FileExplorer.MainWindow.SaveShortcut(lnk, info, root);
            Check(FileExplorer.MainWindow.ShortcutTarget(lnk) == info && FileExplorer.MainWindow.ShortcutTarget(info) == info
                && FileExplorer.MainWindow.ShortcutTarget(card) == card && FileExplorer.MainWindow.ShortcutTarget(Path.Combine(root, "gone.lnk")) == Path.Combine(root, "gone.lnk"), "shortcut target");
            // a shortcut to a folder is where a middle-click opens and a drop lands; one to a file is not
            var folderLnk = Path.Combine(root, "dst.lnk");
            FileExplorer.MainWindow.SaveShortcut(folderLnk, dst, dst);
            Check(FileExplorer.MainWindow.ShortcutFolder(folderLnk) == dst && FileExplorer.MainWindow.ShortcutFolder(lnk) == null
                && FileExplorer.MainWindow.ShortcutFolder(dst) == null && new Entry { Name = "dst.lnk", Path = folderLnk }.FolderTarget == dst, "shortcut to a folder");
            // Settings → Files → "Sort shortcuts to folders with the folders": off, the shortcut sorts with the files
            string Sorted(bool shortcutsWithFolders)
            {
                Settings.ShortcutsWithFolders = shortcutsWithFolders;
                var rows = new List<Entry> { new() { Name = "record.lnk", Path = lnk }, new() { Name = "a.txt", Path = info }, new() { Name = "dst.lnk", Path = folderLnk },
                                             new() { Name = "zz", Path = dst, IsDir = true }, new() { Name = "..", Path = root, IsDir = true } };
                rows.Sort((x, y) => new EntrySort("Name", false).Compare(x, y));
                return string.Join(",", rows.Select(r => r.Name));
            }
            Check(Sorted(false) == "..,zz,a.txt,dst.lnk,record.lnk" && Sorted(true) == "..,dst.lnk,zz,a.txt,record.lnk", "shortcuts to folders sorted with the folders, or not");
            Settings.ShortcutsWithFolders = false;
            // the Windows menu brings forward the app that took the file: its title has to name the file
            Check(ShellMenu.ShowsFile("note.txt - Notepad", new[] { "note.txt" }) && ShellMenu.ShowsFile("*NOTE.TXT - Notepad", new[] { "x.png", "note.txt" })
                && !ShellMenu.ShowsFile("Untitled - Notepad", new[] { "note.txt" }), "a window showing the file");
            // 7-Zip / WinRAR / Open with items in File Labs' menu: their access keys in WPF's form, stray separators dropped
            Check(ShellMenu.Label("Add &to \"a_b.rar\"\tCtrl+T") == ("Add _to \"a__b.rar\"", "Ctrl+T") && ShellMenu.Label("Tom && Jerry").Label == "Tom & Jerry", "shell menu labels");
            ShellMenu.Node Sep() => new(null, null, null, null, null);
            ShellMenu.Node Cmd(string t) => new(t, null, null, null, () => { });
            Check(string.Join(",", ShellMenu.Tidy(new() { Sep(), Cmd("a"), Sep(), Sep(), Cmd("b"), Sep() }).Select(n => n.Text ?? "-")) == "a,-,b", "shell menu separators tidied");

            // a silent permanent delete (Shift+Delete with the sound off): read-only files go too, and a
            // junction inside is removed without emptying the folder it points at
            var doomed = Directory.CreateDirectory(Path.Combine(root, "doomed", "sub")).Parent.FullName;
            var ro = Path.Combine(doomed, "sub", "locked.txt");
            File.WriteAllText(ro, "read-only");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            var kept = Directory.CreateDirectory(Path.Combine(root, "kept")).FullName;
            File.WriteAllText(Path.Combine(kept, "stays.txt"), "here");
            bool doomedLink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(doomed, "link")}\" \"{kept}\"")
                { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(5000) && Directory.Exists(Path.Combine(doomed, "link"));
            FileExplorer.MainWindow.DeleteQuietly(doomed);
            Check(!Directory.Exists(doomed), "silent delete removes the folder, read-only file included");
            Check(!doomedLink || File.Exists(Path.Combine(kept, "stays.txt")), "silent delete never empties a junction's target");

            // a failed update check is a failure, never "nothing newer": offline, 403 (rate limit), 5xx,
            // an unreadable answer all throw; a readable answer with nothing newer returns null
            Check(UpdateCheckFails("http://127.0.0.1:9/"), "update check offline fails");
            Check(UpdateCheckFails(Serve(403, "{\"message\":\"API rate limit exceeded\"}")), "update check 403 fails");
            Check(UpdateCheckFails(Serve(502, "")), "update check 5xx fails");
            Check(UpdateCheckFails(Serve(200, "<html>")), "update check unreadable answer fails");
            Updater.Feed = Serve(200, "{\"tag_name\":\"v0.0.1\",\"body\":\"\",\"assets\":[{\"name\":\"FileLabs-0.0.1-setup.exe\",\"browser_download_url\":\"x\",\"size\":1}]}");
            Check(Task.Run(() => Updater.Check(manual: true)).GetAwaiter().GetResult() == null, "update check: nothing newer is not a failure");

            // a copy into the folder it is in duplicates beside it, as in Explorer; folders keep their dots
            var dup = Directory.CreateDirectory(Path.Combine(root, "dup", "v1.2", "inner")).Parent.Parent.FullName;
            File.WriteAllText(Path.Combine(dup, "v1.2", "inner", "x.txt"), "x");
            File.WriteAllText(Path.Combine(dup, "notes.txt"), "n");
            Run(Ferry.Submit(new[] { Path.Combine(dup, "notes.txt"), Path.Combine(dup, "v1.2") }, dup, move: false));
            j = Run(Ferry.Submit(new[] { Path.Combine(dup, "notes.txt") }, dup, move: false));
            Check(File.ReadAllText(Path.Combine(dup, "notes - Copy.txt")) == "n" && File.Exists(Path.Combine(dup, "notes - Copy (2).txt"))
                && File.Exists(Path.Combine(dup, "v1.2 - Copy", "inner", "x.txt")) && File.ReadAllText(Path.Combine(dup, "notes.txt")) == "n"
                && j.Conflicts.Count == 0 && j.Duplicates[Path.Combine(dup, "notes.txt")] == Path.Combine(dup, "notes - Copy (2).txt"), "duplicate in the same folder");

            // the path a launch asks for (1.0.46): Explorer's /select, as "Open path" programs send it, or a plain path
            Check(Program.PathArg(new[] { @"/select,C:\a b\f.txt" }) == @"C:\a b\f.txt", "launch: /select,\"path\"");
            Check(Program.PathArg(new[] { "/select,", @"C:\a", @"b\f.txt" }) == @"C:\a b\f.txt", "launch: /select, then a path in pieces");
            Check(Program.PathArg(new[] { @"/n,/select,C:\f.txt""" }) == @"C:\f.txt", "launch: two switches, a stray quote");
            Check(Program.PathArg(new[] { "--front", @"C:\Users" }) == @"C:\Users" && Program.PathArg(new[] { "--front" }) == "", "launch: plain path, none");

            // selected text: white on the default accent at 8:1 or better (1.0.38's 6.97:1 still read as grey), and
            // readable on other accents and strengths
            var (fill, text) = FileExplorer.MainWindow.Selection(System.Windows.Media.Color.FromRgb(0x4C, 0x8D, 0xFF), 1.0);
            Check(text == System.Windows.Media.Colors.White && FileExplorer.MainWindow.Contrast(text, fill) >= 8, "selection text on the default accent");
            foreach (var hex in new[] { "#FFD23F", "#FF4FA3", "#E5484D", "#30D158", "#FFFFFF" })
                foreach (var strength in new[] { 0.3, 1.0, 2.5 })
                {
                    var (f, t) = FileExplorer.MainWindow.Selection((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex), strength);
                    Check(FileExplorer.MainWindow.Contrast(t, f) >= 4.5, $"selection text on {hex} at {strength}");
                }

            // Stash and Backup Labs backups (1.0.40): told apart by their first bytes, read without trusting them
            var bk = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            string Zip(string name, params (string Entry, string Text)[] entries)
            {
                var file = Path.Combine(bk, name);
                using var z = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var (entry, text) in entries) { using var w = new StreamWriter(z.CreateEntry(entry).Open()); w.Write(text); }
                return file;
            }
            const string mbox = "From - Tue Sep  1 10:00:00 2026\r\nSubject: one\r\n\r\nbody\r\nFrom inside a paragraph, not a message\r\n>From quoted\r\n\r\n"
                              + "From - Tue Sep  1 11:00:00 2026\r\nSubject: two\r\n\r\nbody\r\n\r\nFrom - x\nSubject: three, LF\n\nbody\n\n";
            var stashEntries = new[]
            {
                ("manifest.json", "{\"Format\":2,\"Created\":\"2026-09-01T10:00:00+03:00\",\"Apps\":{\"Thunderbird\":{\"ActiveProfile\":\"p\"}}}"),
                ("Thunderbird/profiles.ini", "[Profile0]\nName=default-release\nPath=Profiles/p\n"),
                ("Thunderbird/Profiles/p/Mail/Local Folders/Inbox", mbox), ("Thunderbird/Profiles/p/Mail/Local Folders/Inbox.msf", ""),
                ("Thunderbird/Profiles/p/Mail/Local Folders/Archives.sbd/2025", mbox), ("Thunderbird/Profiles/p/prefs.js", ""),
            };
            var stashZip = Zip("Stash-2026-09-01_10-00-00.zip", stashEntries);
            var st = Backups.Inspect(stashZip, details: true);
            Check(st is { Id: "stash", Encrypted: false, Unreadable: false } && st.Made == new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(3)).LocalDateTime
                && st.Rows.Contains(("Thunderbird", "profile default-release")) && st.Rows.Contains(("Files", "5 · " + FileExplorer.MainWindow.Fmt(stashEntries.Skip(1).Sum(x => x.Item2.Length))))
                && st.Rows.Contains(("Local Folders / Inbox", "3 messages")) && st.Rows.Contains(("Local Folders / Archives/2025", "3 messages")) && st.Rows.Count == 4,
                "plain Stash backup: app, profile, files, mail folders and messages");
            Check(Backups.Inspect(Zip("Stash-2026-09-02.stash", stashEntries)) is { Id: "stash", Unreadable: false }, "plain .stash (2.1)");
            var stashEnc = Path.Combine(bk, "Stash-2026-09-03_08-00-00.stash");
            File.WriteAllBytes(stashEnc, "STASHENC\u0001 rest is ciphertext"u8.ToArray());
            Check(Backups.Inspect(stashEnc, details: true) is { Id: "stash", Encrypted: true, Unreadable: false } se && se.Made == new DateTime(2026, 9, 3, 8, 0, 0), "encrypted Stash backup");
            var blEnc = Path.Combine(bk, "2026-09-04_120000.bkl");
            File.WriteAllBytes(blEnc, "BKLENC1\0 salt and chunks"u8.ToArray());
            Check(Backups.Inspect(blEnc) is { Id: "backuplabs", Encrypted: true }, "encrypted Backup Labs archive");
            var blEntries = new[] { ("C/Users/me/Documents/a.txt", "a"), ("C/Users/me/Pictures/2026/b.jpg", "bb"), ("C/Users/me/Pictures/empty/", ""), ("UNC/nas/share/x/c.txt", "c") };
            var bl = Backups.Inspect(Zip("2026-09-05_120000.bkl", blEntries), details: true);
            Check(bl is { Id: "backuplabs", Encrypted: false, Unreadable: false } && bl.Rows.Contains(("Files", "3 · " + FileExplorer.MainWindow.Fmt(4)))
                && bl.Rows.Contains(("From", @"C:\Users\me")) && bl.Rows.Contains(("From", @"\\nas\share\x")), "plain Backup Labs archive: files, size, where from");
            Check(Backups.Inspect(Zip("2026-09-06_120000.zip", blEntries)) is { Id: "backuplabs" }, "a Backup Labs .zip from before 1.3.0");
            Check(Backups.Inspect(Zip("2026-09-07_120000.zip", ("docs/a.txt", "a"))) == null && Backups.Inspect(Zip("Stash-2026-09-08_10-00-00.zip", ("a.txt", "a"))) == null
                && Backups.Inspect(Zip("photos.zip", stashEntries)) == null, "ordinary zips, even named like a backup, stay ordinary zips");
            var junk = Path.Combine(bk, "junk.bkl");
            File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(k => (byte)(k * 7919 % 251)).ToArray());
            var full = File.ReadAllBytes(stashZip);
            var cut = Path.Combine(bk, "cut.stash");
            File.WriteAllBytes(cut, full[..(full.Length / 2)]);
            Check(Backups.Inspect(junk, details: true) is { Id: "backuplabs", Unreadable: true } && Backups.Inspect(cut, details: true) is { Id: "stash", Unreadable: true }
                && Backups.Inspect(Zip("nothing.stash", ("a.txt", "a")), details: true) is { Unreadable: true }
                && FileExplorer.MainWindow.BackupStart(junk) == null, "junk, a cut-off zip, a .stash without a manifest: could not read");
            var many = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("From - x\r\n\r\nbody\r\n\r\n", 200_000))));
            var (found, whole) = Backups.CountMessages(many, 1 << 20, () => false);
            Check(!whole && found > 40_000 && found < 200_000, "counting stops at its cap and says so");

            // Reel's list of video files (1.0.41): another app's file, so nothing in it is trusted
            var rl = Directory.CreateDirectory(Path.Combine(root, "reel")).FullName;
            var posters = Directory.CreateDirectory(Path.Combine(rl, "posters")).FullName + "\\";
            var png = Path.Combine(posters, "m1.png");
            using (var fs = File.Create(png))
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(4, 6, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[4 * 6 * 4], 16)));
                enc.Save(fs);
            }
            string ReelJson(string name, string json, bool bom = false)
            {
                var file = Path.Combine(rl, name);
                File.WriteAllBytes(file, (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray());
                return file;
            }
            string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
            var reelJson = $$"""
                {"schema":1,"updated":"2001-01-01T00:00:00Z","files":[
                  {"path":"D:\\Films\\Heat (1995)\\Heat.mkv","title":"Heat","year":1995,"poster":{{J(png)}},"watched":true,"lastWatched":"2026-10-12T12:00:00.000Z"},
                  {"path":"D:\\TV\\X\\S02E04.mkv","title":"Memento Mori","poster":{{J(posters + @"..\..\evil.png")}},"watched":false,"series":"The X-Files","season":2,"episode":4},
                  {"path":"D:\\TV\\X\\S02E05.mkv","title":"The X-Files","watched":true,"series":"The X-Files","season":2,"episode":5},
                  {"path":"D:\\a.mkv","title":"UNC poster","poster":"\\\\nas\\share\\p.png","watched":false,"year":1500,"season":5000},
                  {"path":"D:\\b.mkv","title":"Device poster","poster":"\\\\?\\C:\\p.png","watched":"yes"},
                  {"path":"relative.mkv","title":"x","watched":true},
                  {"path":"D:\\c.mkv","title":42,"watched":true},
                  {"path":"D:\\d.mkv","title":"Bad fields","year":"2019","watched":true,"lastWatched":"yesterday","poster":{{J(posters + "m1.exe")}}},
                  {"path":"D:\\e.mkv","title":"line\nbreak","watched":true},
                  {"path":"D:\\Films\\Heat (1995)\\HEAT.mkv","title":"Duplicate","watched":false},
                  7, null, []
                ]}
                """;
            var good = ReelJson("good.json", reelJson);
            var rm = Sisters.ReelFiles(good, posters);
            Check(rm != null && rm.Count == 5, "Reel list: the usable entries only");
            Check(rm[@"d:\films\heat (1995)\HEAT.MKV"] is { Title: "Heat", Year: 1995, Watched: true, Series: null } heat && heat.Poster == png
                && heat.LastWatched == new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).ToLocalTime()
                && FileExplorer.MainWindow.ReelLine(heat) is ("Heat (1995)", var hs) && hs.StartsWith("watched 12 Oct"), "Reel film: title, year, poster, watched, by path in any case");
            Check(rm[@"D:\TV\X\S02E04.mkv"] is { Poster: null } ep && FileExplorer.MainWindow.ReelLine(ep) == ("The X-Files — S02E04 Memento Mori", "not watched")
                && FileExplorer.MainWindow.ReelLine(rm[@"D:\TV\X\S02E05.mkv"]) == ("The X-Files — S02E05", "watched"), "Reel episode; a poster climbing out of Reel's folder is dropped");
            Check(rm[@"D:\a.mkv"] is { Poster: null, Year: null, Season: null } && rm[@"D:\d.mkv"] is { Poster: null, Year: null, LastWatched: null },
                "UNC poster, poster that isn't a picture, year/season/date out of range or of the wrong type: dropped");
            Check(!Sisters.Local(@"\\nas\share\x.exe") && !Sisters.Local(@"\\?\C:\x.exe") && !Sisters.Local(@"\\.\pipe\x") && !Sisters.Local("x.exe")
                && !Sisters.Local("C:x.exe") && Sisters.Local(Environment.ProcessPath), "local paths only");
            File.WriteAllText(card, $$"""{"schema":1,"id":"reel","exe":"\\\\127.0.0.1\\c$\\Windows\\notepad.exe"}""");
            Check(Sisters.ExeFromCard(card) == null, "a card whose exe is on a share is never followed");
            Check(ReferenceEquals(Sisters.ReelFiles(good, posters), rm), "Reel list cached while unchanged");
            File.AppendAllText(good, " ");
            Check(Sisters.ReelFiles(good, posters) is { Count: 5 } again && !ReferenceEquals(again, rm), "and read again when it changes");
            Check(Sisters.ReelFiles(ReelJson("bom.json", reelJson, bom: true), posters) is { Count: 5 }, "Reel list with a BOM");
            Check(Sisters.ReelFiles(Path.Combine(rl, "missing.json"), posters) == null
                && Sisters.ReelFiles(ReelJson("s2.json", reelJson.Replace("\"schema\":1", "\"schema\":2")), posters) == null
                && Sisters.ReelFiles(ReelJson("s1str.json", reelJson.Replace("\"schema\":1", "\"schema\":\"1\"")), posters) == null
                && Sisters.ReelFiles(ReelJson("obj.json", """{"schema":1,"files":{}}"""), posters) == null
                && Sisters.ReelFiles(ReelJson("arr.json", "[]"), posters) == null
                && Sisters.ReelFiles(ReelJson("cut.json", reelJson[..(reelJson.Length / 2)]), posters) == null
                && Sisters.ReelFiles(ReelJson("deep.json", "{\"schema\":1,\"files\":[" + new string('[', 64) + new string(']', 64) + "]}"), posters) == null,
                "Reel list missing, schema 2, wrong types, cut off, too deep");
            var junkJson = Path.Combine(rl, "junk.json");
            var rnd = new Random(1);
            var junkBytes = new byte[8 << 20];
            rnd.NextBytes(junkBytes);
            File.WriteAllBytes(junkJson, junkBytes);
            var huge = Path.Combine(rl, "huge.json");
            using (var fs = File.Create(huge)) fs.SetLength(Sisters.ReelFilesMaxBytes + 1);
            Check(Sisters.ReelFiles(junkJson, posters) == null && Sisters.ReelFiles(huge, posters) == null, "8 MB of junk, a list over 16 MB");
            // 20,000 entries the way Reel writes them (~330 bytes each), plus one past Reel's cap
            var many20 = new System.Text.StringBuilder("{\"schema\":1,\"updated\":\"2026-10-20T10:00:00.000Z\",\"files\":[");
            for (int k = 0; k <= Sisters.ReelFilesMax; k++)
                many20.Append(k == 0 ? "" : ",").Append($$"""{"path":{{J($@"E:\Media\Films\A Rather Long Film Title Number {k} (2019)\A.Rather.Long.Film.Title.Number.{k}.2019.1080p.BluRay.x264.mkv")}},"title":"A Rather Long Film Title Number {{k}}","year":2019,"poster":{{J($@"{posters}{k:x8}-0000-4000-8000-000000000000.jpg")}},"watched":{{(k % 3 == 0 ? "true" : "false")}},"lastWatched":"2026-10-12T20:00:00.000Z"}""");
            var big = ReelJson("big.json", many20.Append("]}").ToString());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bigMap = Sisters.ReelFiles(big, posters);
            var parse = sw.Elapsed.TotalMilliseconds;
            Check(bigMap is { Count: 20_000 } && bigMap.ContainsKey($@"E:\Media\Films\A Rather Long Film Title Number 19999 (2019)\A.Rather.Long.Film.Title.Number.19999.2019.1080p.BluRay.x264.mkv")
                && !bigMap.ContainsKey($@"E:\Media\Films\A Rather Long Film Title Number 20000 (2019)\A.Rather.Long.Film.Title.Number.20000.2019.1080p.BluRay.x264.mkv"), "20,001 entries: Reel's 20,000 read, the rest ignored");
            sw.Restart();
            for (int k = 0; k < 1000; k++) Sisters.ReelFiles(big, posters).TryGetValue($@"E:\Media\Films\A Rather Long Film Title Number {k * 20} (2019)\a.rather.long.film.title.number.{k * 20}.2019.1080p.bluray.x264.mkv", out _);
            Console.WriteLine($"reel.files.json: 20,001 entries, {new FileInfo(big).Length / 1024} KB, first read {parse:F0} ms, cached lookup {sw.Elapsed.TotalMilliseconds:F1} µs (incl. the file check)");
            var hugePoster = Path.Combine(posters, "huge.png");
            using (var fs = File.Create(hugePoster)) { fs.Write(File.ReadAllBytes(png)); fs.SetLength(10 << 20); }
            File.WriteAllBytes(Path.Combine(posters, "junk.jpg"), junkBytes[..5000]);
            Check(FileExplorer.MainWindow.Poster(png) is { PixelWidth: 200 } && FileExplorer.MainWindow.Poster(hugePoster) == null
                && FileExplorer.MainWindow.Poster(Path.Combine(posters, "junk.jpg")) == null && FileExplorer.MainWindow.Poster(Path.Combine(posters, "none.jpg")) == null,
                "poster: decoded small; 10 MB, junk or missing = none");
            // ecosystem v3: Reel's posters move to %AppData%\ProtagonistLabs\Reel\posters; the old folder still counts
            var postersV3 = Directory.CreateDirectory(Path.Combine(rl, "ProtagonistLabs", "Reel", "posters")).FullName + "\\";
            var v3 = ReelJson("v3.json", $$"""{"schema":1,"files":[{"path":"D:\\new.mkv","title":"New","watched":false,"poster":{{J(postersV3 + "n.jpg")}}},{"path":"D:\\old.mkv","title":"Old","watched":false,"poster":{{J(posters + "o.jpg")}}}]}""");
            Check(Sisters.ReelFiles(v3, postersV3, posters) is { } m3 && m3[@"D:\new.mkv"].Poster == postersV3 + "n.jpg" && m3[@"D:\old.mkv"].Poster == posters + "o.jpg"
                && Sisters.ReelPosters.Length == 2 && Sisters.ReelPosters.All(p => p.EndsWith(@"\posters\")), "Reel posters from the new and the old folder");

            // ecosystem v3: the data folder moves once, by rename, never mixed, never half (DataFolder in Settings.cs)
            var mig = Directory.CreateDirectory(Path.Combine(root, "migrate")).FullName;
            string oldR = Path.Combine(mig, "Roaming", "FileLabs"), oldL = Path.Combine(mig, "Local", "FileLabs"), newR = Path.Combine(mig, "Roaming", "ProtagonistLabs", "File Labs");
            Directory.CreateDirectory(Path.Combine(oldL, "WebView2", "EBWebView"));
            File.WriteAllText(Path.Combine(oldL, "WebView2", "EBWebView", "Local State"), "{}");
            Directory.CreateDirectory(oldR);
            File.WriteAllText(Path.Combine(oldR, "settings.txt"), "accent=#FF0000");
            Directory.CreateDirectory(newR); // an empty new folder (a sister wrote nothing yet) doesn't block the move
            DataFolder.Migrate(oldR, oldL, newR);
            Check(File.ReadAllText(Path.Combine(newR, "settings.txt")) == "accent=#FF0000" && File.Exists(Path.Combine(newR, "WebView2", "EBWebView", "Local State"))
                && !Directory.Exists(oldR) && !Directory.Exists(oldL) && File.ReadAllText(Path.Combine(newR, "migration.log")).Contains("moved"), "data: both old folders moved into the new one, logged");
            var logLength = new FileInfo(Path.Combine(newR, "migration.log")).Length;
            DataFolder.Migrate(oldR, oldL, newR);
            Check(new FileInfo(Path.Combine(newR, "migration.log")).Length == logLength && File.Exists(Path.Combine(newR, "settings.txt")), "data: a second run does nothing");
            Directory.CreateDirectory(oldR);
            File.WriteAllText(Path.Combine(oldR, "settings.txt"), "accent=#00FF00");
            DataFolder.Migrate(oldR, oldL, newR);
            Check(File.ReadAllText(Path.Combine(oldR, "settings.txt")) == "accent=#00FF00" && File.ReadAllText(Path.Combine(newR, "settings.txt")) == "accent=#FF0000"
                && File.ReadAllText(Path.Combine(newR, "migration.log")).Contains("left in place"), "data: the new folder has data, so the old one is left alone, not mixed in");
            var newB = Path.Combine(mig, "Roaming", "ProtagonistLabs", "File Labs B");
            using (new FileStream(Path.Combine(oldR, "settings.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                DataFolder.Migrate(oldR, oldL, newB);
            Check(File.Exists(Path.Combine(oldR, "settings.txt")) && !Directory.Exists(newB) && File.ReadAllText(Path.Combine(oldR, "migration.log")).Contains("could not move"),
                "data: a locked file stops the move whole, the old folder stays in use and says why");
            DataFolder.Migrate(oldR, oldL, newB);
            Check(File.ReadAllText(Path.Combine(newB, "settings.txt")) == "accent=#00FF00" && File.Exists(Path.Combine(newB, "migration.log")) && !Directory.Exists(oldR),
                "data: moved at the next start, its log with it");

            // What's new: every release parses, newest first, with notes, the running version included (a release
            // without its entry in whatsnew.txt fails here)
            var notes = WhatsNewWindow.Load();
            Check(notes.Count >= 44 && notes[0].Version == Updater.Current.ToString() && notes.All(n => n.Lines.Count > 0)
                  && notes.Zip(notes.Skip(1)).All(p => Version.Parse(p.First.Version) > Version.Parse(p.Second.Version)), "what's new: every release, newest first, this one included");
            Check(notes.All(n => { using var pic = WhatsNewWindow.Picture(n); return pic != null; }), "what's new: every release has the picture of its month built in");
            var recent = new List<string> { @"C:\A", @"C:\B" };
            Settings.Bump(recent, @"c:\b", 2); Settings.Bump(recent, @"C:\C", 2);
            Check(recent.SequenceEqual(new[] { @"C:\C", @"c:\b" }), "recent folders: newest first, once, capped");
            Check(FileExplorer.MainWindow.ParentForMenu(@"C:\Users\x\file") == @"C:\Users\x"
                  && FileExplorer.MainWindow.ParentForMenu(@"C:\Users\someone\AppData\Local\Temp\claude\deep\folder\x") == @"C:\…\deep\folder", "menu paths shortened past 40 characters");

            // the size cache drops paths that go round a junction loop (563,000 of them made a 277 MB cache), not long real ones
            Check(SizeCache.Loops(@"G:\Claude Projects\automaton\repo\packages\cli\node_modules\@conway\automaton\packages\cli\node_modules\@conway\automaton\packages\cli\node_modules\@conway\automaton\packages\cli\node_modules\viem\_types")
                  && !SizeCache.Loops(@"G:\Claude Projects\automaton\repo\packages\cli\node_modules\.pnpm\@typescript-eslint+typescript-estree@8.8.0_typescript@5.6.2\node_modules\@typescript-eslint\typescript-estree\dist\create-program\shared\x")
                  && !SizeCache.Loops(@"C:\short\a\b\c\a\b\c"), "size cache: loop paths dropped, long real ones and short ones kept");

            // 1.0.57 Group by date: Explorer's buckets, from a fixed "now" (Wednesday 28 Oct 2026, noon)
            var at = new DateTime(2026, 10, 28, 12, 0, 0);
            Check(Entry.DateGroupOf(at.AddHours(3), at) == 0 && Entry.DateGroupOf(at.Date, at) == 0 && Entry.DateGroupOf(at.Date.AddMinutes(-1), at) == 1
                  && Entry.DateGroupOf(new DateTime(2026, 10, 8), at) == 4 && Entry.DateGroupOf(new DateTime(2026, 9, 18), at) == 5
                  && Entry.DateGroupOf(new DateTime(2026, 2, 1), at) == 6 && Entry.DateGroupOf(new DateTime(2025, 12, 31), at) == 7,
                  "group by date: today (and later), yesterday, this month, last month, this year, long ago");

            // 1.0.58 search terms: name words with the terms taken out, sizes in 1000s, dates, a regex, a quoted content
            var sq = PaneView.SearchQuery.Parse("report size:1MB-100MB within:7d re:\"^q\\d\" content:\"two words\"", at);
            Check(sq.Error == null && sq.Name == "report" && sq.MinSize == 1_000_000 && sq.MaxSize == 100_000_000 && sq.After == at.AddDays(-7)
                  && sq.Pattern.IsMatch("Q3 report.pdf") && sq.Content == "two words" && sq.FilesOnly, "search terms: parsed");
            var overGb = PaneView.SearchQuery.Parse("size:>1GB", at);
            Check(!overGb.MatchesMeta("a.iso", false, 1_000_000_000, at) && overGb.MatchesMeta("a.iso", false, 1_000_000_001, at) && !overGb.MatchesMeta("dir", true, 0, at)
                  && PaneView.SearchQuery.Parse("*.txt before:2026-01-01", at).MatchesMeta("a.txt", false, 5, new DateTime(2025, 6, 1))
                  && !PaneView.SearchQuery.Parse("*.txt before:2026-01-01", at).MatchesMeta("a.txt", false, 5, at)
                  && PaneView.SearchQuery.Parse("re:\"(\"", at).Error != null && PaneView.SearchQuery.Parse("size:lots", at).Error != null,
                  "search terms: size, date, wildcard, bad regex and bad size refused");

            // 1.0.63 custom size and dates: what the dialogs write, read back by the parser, both ends of a size included
            var (sizeTerm, _) = PaneView.SizeTerm(("1,5", 2), ("2", 3));
            var custom = PaneView.SearchQuery.Parse(sizeTerm, at);
            Check(sizeTerm == "size:1.5MB-2GB" && custom.MinSize == 1_500_000 && custom.MaxSize == 2_000_000_000
                  && PaneView.SizeTerm(("10", 1), ("", 0)).Term == "size:>=10KB" && PaneView.SearchQuery.Parse("size:>=10KB", at).MinSize == 10_000
                  && PaneView.SizeTerm(("", 0), ("3", 3)).Term == "size:<=3GB" && PaneView.SearchQuery.Parse("size:<=3GB", at).MaxSize == 3_000_000_000
                  && PaneView.SizeTerm(("", 0), ("", 0)) == (null, null) && PaneView.SizeTerm(("5", 3), ("1", 3)).Error != null && PaneView.SizeTerm(("lots", 2), ("", 0)).Error != null,
                  "custom size: terms written and read back");
            var range = PaneView.DateTerms("2026-10-01", "2026-10-15");
            var inRange = PaneView.SearchQuery.Parse(range.After + " " + range.Before, at);
            Check(range == ("after:2026-10-01", "before:2026-10-15", null)
                  && inRange.MatchesMeta("a", false, 1, new DateTime(2026, 10, 1)) && inRange.MatchesMeta("a", false, 1, new DateTime(2026, 10, 14, 23, 0, 0))
                  && !inRange.MatchesMeta("a", false, 1, new DateTime(2026, 10, 15)) && !inRange.MatchesMeta("a", false, 1, new DateTime(2026, 9, 30))
                  && PaneView.DateTerms("", "2026-01-01") == (null, "before:2026-01-01", null) && PaneView.DateTerms("2026-01-01", "") == ("after:2026-01-01", null, null)
                  && PaneView.DateTerms("2026-10-15", "2026-10-01").Error != null && PaneView.DateTerms("soon", "").Error != null && PaneView.DateTerms("", "").Error != null,
                  "custom dates: range, before, after, and wrong ones refused");

            // 1.0.61 small files several at once: the same files arrive, whole, either way; the times are printed
            var manyDir = Directory.CreateDirectory(Path.Combine(root, "many", "files")).FullName;
            var seeded = new Random(7);
            for (int k = 0; k < 1200; k++)
            {
                var bytes = new byte[4096 + k % 60 * 1024];
                seeded.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(manyDir, $"f{k:D4}.bin"), bytes);
            }
            Job Timed(bool parallel, string to, out long ms)
            {
                var keep = Settings.ParallelCopy;
                Settings.ParallelCopy = parallel;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var job = Run(Ferry.Submit(new[] { manyDir }, Directory.CreateDirectory(to).FullName, move: false));
                ms = clock.ElapsedMilliseconds;
                Settings.ParallelCopy = keep;
                return job;
            }
            var oneByOne = Timed(false, Path.Combine(root, "many", "one"), out var msOne);
            var together = Timed(true, Path.Combine(root, "many", "four"), out var msFour);
            bool same = Directory.GetFiles(manyDir).All(f =>
                File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "many", "four", "files", Path.GetFileName(f)))));
            Check(oneByOne.DoneFiles == 1200 && together.DoneFiles == 1200 && together.Errors.IsEmpty && same,
                  $"parallel copy: 1200 small files arrive whole ({together.DoneFiles} copied, {together.Errors.Count} errors)");
            Console.WriteLine($"1200 small files on {Path.GetPathRoot(root)} (fast: {Ferry.Fast(root)}): one by one {msOne} ms, four at a time {msFour} ms");

            // 1.0.60 archives: a ZIP and a TAR.GZ listed by folder, extracted without overwriting, a "../" entry refused,
            // a ZIP added to and taken from
            var arch = Path.Combine(root, "arch");
            Directory.CreateDirectory(arch);
            var zipPath = Path.Combine(arch, "a.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("top.txt").Open())) w.Write("top");
                using (var w = new StreamWriter(z.CreateEntry("docs/inner/deep.txt").Open())) w.Write("deep");
            }
            var tgz = Path.Combine(arch, "b.tar.gz");
            var tarSrc = Path.Combine(arch, "tarsrc");
            Directory.CreateDirectory(Path.Combine(tarSrc, "sub"));
            File.WriteAllText(Path.Combine(tarSrc, "sub", "t.txt"), "tar text");
            using (var fs = File.Create(tgz))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
                System.Formats.Tar.TarFile.CreateFromDirectory(tarSrc, gz, includeBaseDirectory: false);
            var topList = string.Join(",", Archive.List(zipPath, "").OrderBy(i => i.Name).Select(i => i.Name + (i.IsDir ? "/" : "")));
            var innerList = string.Join(",", Archive.List(zipPath, "docs").Select(i => i.Name + (i.IsDir ? "/" : "")));
            var outDir = Path.Combine(arch, "out");
            Directory.CreateDirectory(outDir);
            var first = Archive.Extract(zipPath, new[] { "docs" }, "", outDir);
            File.WriteAllText(Path.Combine(outDir, "top.txt"), "mine");
            var second = Archive.Extract(zipPath, new[] { "top.txt", "docs" }, "", outDir);
            var fromTar = Archive.Extract(tgz, new[] { "t.txt" }, "sub", outDir);
            Check(topList == "docs/,top.txt" && innerList == "inner/" && first == (1, 0) && second == (0, 2)
                  && File.ReadAllText(Path.Combine(outDir, "docs", "inner", "deep.txt")) == "deep" && File.ReadAllText(Path.Combine(outDir, "top.txt")) == "mine"
                  && Archive.List(tgz, "sub").Single().Name == "t.txt" && fromTar == (1, 0) && File.ReadAllText(Path.Combine(outDir, "t.txt")) == "tar text",
                  $"archives: listed by folder, extracted without overwriting ({topList} / {innerList} / {first} {second} {fromTar})");
            var evil = Path.Combine(arch, "evil.zip");
            using (var z = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
                using (var w = new StreamWriter(z.CreateEntry("../escaped.txt").Open())) w.Write("x");
            bool refused = false;
            try { Archive.Extract(evil, new[] { ".." }, "", outDir); } catch (IOException) { refused = true; }
            Check(refused && !File.Exists(Path.Combine(arch, "escaped.txt")), "archives: an entry leading out of the folder is refused");
            var addMe = Path.Combine(arch, "add me.txt");
            File.WriteAllText(addMe, "added");
            var addedOnce = Archive.Add(zipPath, "docs", new[] { addMe });
            var addedTwice = Archive.Add(zipPath, "docs", new[] { addMe });
            Archive.Delete(zipPath, new[] { "inner" }, "docs");
            Check(addedOnce == (1, 0) && addedTwice == (0, 1) && string.Join(",", Archive.List(zipPath, "docs").Select(i => i.Name)) == "add me.txt",
                  "archives: added to a ZIP once, a folder taken out of it");
            Check(Archive.Clean("./docs/.gitignore") == "docs/.gitignore" && Archive.Clean("./") == "" && Archive.Clean(".hidden/") == ".hidden"
                  && Archive.Clean("../out.txt") == "../out.txt", "archives: tar's ./ dropped, a leading dot in a name kept, ../ left for the guard");
            Check(PaneView.ArchiveOf(Path.Combine(zipPath, "docs")) is ({ } za, "docs") && za == zipPath && PaneView.ArchiveOf(arch) == null,
                  "archives: a path running through a ZIP is found, a plain folder isn't");

            // 1.0.51 Reset to defaults: every setting belongs to a card that resets it, or is knowingly kept (Windows
            // integration, what File Labs remembers by itself); a reset puts back the defaults and touches nothing else
            var resettable = SettingsWindow.AppearanceKeys.Concat(SettingsWindow.FilesKeys).Concat(SettingsWindow.TransfersKeys).ToList();
            var defaults = Settings.DefaultKeys.ToHashSet();
            Check(defaults.All(k => resettable.Contains(k) || SettingsWindow.NotReset.Contains(k)), "reset: every setting is in a card or kept on purpose, missing "
                  + string.Join(",", defaults.Where(k => !resettable.Contains(k) && !SettingsWindow.NotReset.Contains(k))));
            Check(resettable.All(defaults.Contains) && resettable.Distinct().Count() == resettable.Count, "reset: the cards name real settings, each once");
            (Settings.Accent, Settings.TipDelay, Settings.Autoscroll, Settings.CloseEmptyPane, Settings.ShowHidden, Settings.Verify, Settings.CloseToTray, Settings.RowPad)
                = ("#FF0000", 2500, true, true, true, true, true, 6);
            Settings.Reset(SettingsWindow.AppearanceKeys);
            Check(Settings.Accent == "#4C8DFF" && Settings.RowPad == 3 && !Settings.Autoscroll && !Settings.CloseEmptyPane
                  && Settings.TipDelay == 2500 && Settings.ShowHidden && Settings.Verify && Settings.CloseToTray, "reset: one card, and only that card");
            Settings.Reset(resettable);
            Check(Settings.TipDelay == 1500 && !Settings.ShowHidden && !Settings.Verify && Settings.CloseToTray && Settings.AutoscrollSideways, "reset: all three cards; Windows integration kept");
            Settings.CloseToTray = false;

            // 1.0.51 Quick access lists: the file of 1.0.50 and before is one list, written back exactly as it was
            var oldQuick = new[] { @"C:\Users\x\Projects", @"\\nas\share\photos", @"D:\Games" };
            var one = FileExplorer.MainWindow.ParseQuickLists(oldQuick.Append(""));
            Check(one is [{ Name: "Quick access" } q1] && q1.Folders.SequenceEqual(oldQuick) && FileExplorer.MainWindow.QuickListLines(one).SequenceEqual(oldQuick), "quick access: a file from before is one list, kept in its form");
            one.Add(new FileExplorer.MainWindow.QuickList { Name = "Work # 2", Folders = { @"E:\Work" } });
            var two = FileExplorer.MainWindow.ParseQuickLists(FileExplorer.MainWindow.QuickListLines(one));
            Check(two.Count == 2 && two[0].Name == "Quick access" && two[0].Folders.SequenceEqual(oldQuick) && two[1].Name == "Work # 2" && two[1].Folders.SequenceEqual(new[] { @"E:\Work" }), "quick access: two lists, names and folders, read back");
            two[0].Name = "Home";
            two.Add(new FileExplorer.MainWindow.QuickList { Name = "Empty" });
            Check(FileExplorer.MainWindow.ParseQuickLists(FileExplorer.MainWindow.QuickListLines(two)) is [{ Name: "Home" }, { Name: "Work # 2" }, { Name: "Empty", Folders.Count: 0 }]
                  && FileExplorer.MainWindow.ParseQuickLists(Array.Empty<string>()) is [{ Name: "Quick access", Folders.Count: 0 }], "quick access: renamed first list, an empty list, no file");

            // 1.0.51 Find duplicates: same size and content, in subfolders when asked; shortcuts by their target
            var dupes = Directory.CreateDirectory(Path.Combine(root, "dupes", "sub", "deeper")).Parent.Parent.FullName;
            var blob = new byte[200_000]; new Random(7).NextBytes(blob);
            var blobOther = (byte[])blob.Clone(); blobOther[150_000] ^= 1; // the same first 64 KB, then one byte apart
            File.WriteAllBytes(Path.Combine(dupes, "a.bin"), blob);
            File.WriteAllBytes(Path.Combine(dupes, "a copy.bin"), blob);
            File.WriteAllBytes(Path.Combine(dupes, "almost.bin"), blobOther);
            File.WriteAllBytes(Path.Combine(dupes, "sub", "deeper", "a again.bin"), blob);
            File.WriteAllText(Path.Combine(dupes, "small.txt"), "same words");
            File.WriteAllText(Path.Combine(dupes, "sub", "small too.txt"), "same words");
            File.WriteAllText(Path.Combine(dupes, "other.txt"), "diff words"); // same size, other content
            File.WriteAllText(Path.Combine(dupes, "empty1.txt"), ""); File.WriteAllText(Path.Combine(dupes, "empty2.txt"), "");
            FileExplorer.MainWindow.SaveShortcut(Path.Combine(dupes, "to a.lnk"), Path.Combine(dupes, "a.bin"), dupes);
            FileExplorer.MainWindow.SaveShortcut(Path.Combine(dupes, "sub", "also to a.lnk"), Path.Combine(dupes, "a.bin"), dupes);
            FileExplorer.MainWindow.SaveShortcut(Path.Combine(dupes, "to other.lnk"), Path.Combine(dupes, "other.txt"), dupes);
            string Shape(List<DuplicatesWindow.DupGroup> gs) => string.Join(" | ", gs.Select(g => (g.Target != null ? "lnk:" : "") + string.Join(",", g.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n))));
            var top = DuplicatesWindow.Find(new[] { dupes }, subfolders: false, hidden: false, null, default);
            Check(Shape(top) == "a copy.bin,a.bin", "duplicates in one folder: " + Shape(top));
            var nested = DuplicatesWindow.Find(new[] { dupes }, subfolders: true, hidden: false, null, default);
            Check(Shape(nested) == "a again.bin,a copy.bin,a.bin | small too.txt,small.txt | lnk:also to a.lnk,to a.lnk" && nested[0].Wasted == 400_000, "duplicates with subfolders: " + Shape(nested));
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                bool cancelled = false;
                try { DuplicatesWindow.Find(new[] { dupes }, true, false, null, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "duplicates: Stop stops it");
            }

            // 1.0.59 several locations and looser matches: a file under two listed folders counts once; by name, sizes may differ
            File.WriteAllText(Path.Combine(dupes, "same name.txt"), "first");
            File.WriteAllText(Path.Combine(dupes, "sub", "same name.txt"), "second, longer");
            var sub = Path.Combine(dupes, "sub");
            var twice = DuplicatesWindow.Find(new[] { dupes, sub, dupes }, subfolders: true, hidden: false, null, default);
            Check(Shape(twice) == Shape(nested), "duplicates: overlapping locations count each file once: " + Shape(twice));
            var byName = DuplicatesWindow.Find(new[] { dupes, sub }, subfolders: false, hidden: false, null, default, DuplicatesWindow.Match.Name);
            var bySizeToo = DuplicatesWindow.Find(new[] { dupes, sub }, subfolders: false, hidden: false, null, default, DuplicatesWindow.Match.NameAndSize);
            var byBytes = DuplicatesWindow.Find(new[] { dupes, sub }, subfolders: false, hidden: false, null, default);
            Check(byName.Any(g => g.Name == "same name.txt" && g.Files.Count == 2) && !bySizeToo.Any(g => g.Files.Any(f => f.Path.EndsWith("same name.txt")))
                  && !byBytes.Any(g => g.Files.Any(f => f.Path.EndsWith("same name.txt"))) && byName.First(g => g.Name == "same name.txt").Wasted == 5,
                  "duplicates: by name finds the two, by name and size and by content don't: " + Shape(byName));

            // 1.0.51 Open path through the agent: the file Explorer selects late still reaches File Labs, after its folder
            (string, bool)[] Watch(params (string Folder, string Selected)[] looks)
            {
                var w = new Agent.SelectionWatch();
                var said = new List<(string, bool)>();
                foreach (var (f, sel) in looks) { var r = w.Look(f, sel); if (r.Open != null || r.Done) said.Add(r); if (r.Done) break; }
                return said.ToArray();
            }
            var none = Enumerable.Repeat((@"C:\D", (string)null), 9);
            Check(Watch((@"C:\D", @"C:\D\f.txt")).SequenceEqual(new[] { (@"C:\D\f.txt", true) }), "agent: selected at once, the file");
            Check(Watch(none.Append((@"C:\D", @"C:\D\f.txt")).ToArray()).SequenceEqual(new[] { (@"C:\D\f.txt", true) }), "agent: selected within 0.5 s, the file");
            Check(Watch(none.Concat(none).Append((@"C:\D", @"C:\D\f.txt")).ToArray()).SequenceEqual(new[] { (@"C:\D", false), (@"C:\D\f.txt", true) }), "agent: selected late, the folder, then the file");
            Check(Watch(Enumerable.Repeat((@"C:\D", (string)null), 200).ToArray()).SequenceEqual(new[] { (@"C:\D", false), ((string)null, true) }), "agent: never selected, the folder, then done");
            Check(Watch(none.Concat(none).Append((@"C:\Other", @"C:\Other\x")).ToArray()).SequenceEqual(new[] { (@"C:\D", false), ((string)null, true) }), "agent: navigated elsewhere, nothing more");

            // 1.0.51 middle-click autoscroll: still inside the dead zone, then faster the further away, either way
            Check(PaneView.AutoscrollSpeed(0) == 0 && PaneView.AutoscrollSpeed(-12) == 0 && PaneView.AutoscrollSpeed(30) > 0 && PaneView.AutoscrollSpeed(-30) == -PaneView.AutoscrollSpeed(30)
                  && PaneView.AutoscrollSpeed(200) > PaneView.AutoscrollSpeed(100) && PaneView.AutoscrollSpeed(100000) == 8000, "autoscroll speed");

            Console.WriteLine("selftest: all passed");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "filelabs-selftest.txt"), ex.ToString());
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    static bool UpdateCheckFails(string feed)
    {
        Updater.Feed = feed;
        try { Task.Run(() => Updater.Check(manual: true)).GetAwaiter().GetResult(); return false; }
        catch (Exception ex) when (Updater.Failed(ex)) { return true; }
    }

    // A one-request local server answering with this status and body; returns its address.
    static string Serve(int status, string body)
    {
        var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start(); int p = ((System.Net.IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{p}/");
        listener.Start();
        _ = Task.Run(() =>
        {
            var c = listener.GetContext();
            c.Response.StatusCode = status;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes);
            c.Response.Close(); // the listener is left to the process: stopping it here can cut the answer off
        });
        return $"http://localhost:{p}/";
    }

    static Job Run(Job j) { Wait(() => !j.Active); return j; }
    static void Wait(Func<bool> done) { for (int i = 0; i < 300 && !done(); i++) Thread.Sleep(50); if (!done()) throw new TimeoutException("job did not finish"); }
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
}
