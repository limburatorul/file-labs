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
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(3000);
                using var w = new StreamWriter(pipe);
                w.WriteLine(e.Args.FirstOrDefault(a => !a.StartsWith("--")) ?? "");
                Shutdown(); return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { } // first instance hung/closing: just start normally
        }
        else _ = Listen();
        Settings.Load(); // before any window: the panes read column widths while being built
        SizeCache.Load(); // folder sizes from previous sessions: a full 12 TB drive is minutes of walking
        Exit += (_, _) => SizeCache.Save();
        base.OnStartup(e);
        new MainWindow().Show(); // opened here, not via StartupUri, so --agent etc. never build it
    }

    static readonly string PipeName = "FileLabs-" + Environment.UserName;
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

            Console.WriteLine("selftest: all passed");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "filelabs-selftest.txt"), ex.ToString());
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    static Job Run(Job j) { Wait(() => !j.Active); return j; }
    static void Wait(Func<bool> done) { for (int i = 0; i < 300 && !done(); i++) Thread.Sleep(50); if (!done()) throw new TimeoutException("job did not finish"); }
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
}
