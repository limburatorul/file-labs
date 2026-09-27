using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace FileExplorer;

public partial class App : Application
{
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
            try { Settings.SetDefaultFileManager(e.Args.Contains("--set-default")); Shutdown(0); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Shutdown(1); }
            return;
        }

        // Single instance: a second launch (folder double-click, Win+E) hands its path to the
        // running window, which opens it as a new tab, then exits.
        instance = new Mutex(true, PipeName, out bool first);
        if (!first)
        {
            AllowSetForegroundWindow(-1); // we were just launched by the user, so we may pass focus on
            if (HandOff(e.Args.FirstOrDefault(a => !a.StartsWith("--")) ?? "")) { Shutdown(); return; }
            // first instance hung or closing: just start normally
        }
        else _ = Listen();
        Settings.Load(); // before any window: the panes read column widths while being built
        SizeCache.StartLoading(); // folder sizes from previous sessions: a full 12 TB drive is minutes of walking
        Exit += (_, _) => SizeCache.Save();
        var sizeSaver = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        sizeSaver.Tick += (_, _) => Task.Run(SizeCache.SaveIfChanged); // off the UI thread: up to 50 000 lines
        sizeSaver.Start();
        base.OnStartup(e);
        new MainWindow().Show(); // opened here, not via StartupUri, so --agent etc. never build it
    }

    static readonly string PipeName = "FileLabs-" + Environment.UserName;

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
    Mutex instance;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    async Task Listen()
    {
        while (true)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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

    static Job Run(Job j) { Wait(() => !j.Active); return j; }
    static void Wait(Func<bool> done) { for (int i = 0; i < 300 && !done(); i++) Thread.Sleep(50); if (!done()) throw new TimeoutException("job did not finish"); }
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
}
