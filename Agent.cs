using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace FileExplorer;

// Win+E on Windows 11 25H2 no longer goes through the shell registry keys a file manager can take
// over (tested: both the {52205fd8…} launcher and This PC's opennewwindow are ignored). So, like
// OneCommander's connector, a small window-less File Labs process catches the key itself:
// `FileLabs.exe --agent`, started at sign-in only while File Labs is the default file manager.
public static class Agent
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "File Labs Agent";
    static string StopEventName => "FileLabs-AgentStop-" + Environment.UserName;
    static string MutexName => "FileLabs-Agent-" + Environment.UserName;

    // ---- turned on/off together with the default-file-manager setting ----
    public static void Enable(string exe)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue(RunValue, $"\"{exe}\" --agent");
        Process.Start(new ProcessStartInfo(exe, "--agent") { UseShellExecute = false });
    }

    public static void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(RunValue, throwOnMissingValue: false);
        if (EventWaitHandle.TryOpenExisting(StopEventName, out var stop)) using (stop) stop.Set();
    }

    // ---- the agent itself ----
    static IntPtr hook;
    static LowLevelKeyboardProc proc; // held so the GC can't collect the callback Windows calls
    static bool swallowUp;

    /// Runs until Disable() signals it. Returns the process exit code.
    public static int Run(System.Windows.Application app)
    {
        using var single = new Mutex(true, MutexName, out bool first);
        if (!first) return 0; // one agent per user is enough
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);

        proc = Hook;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) return 1;

        // the hook needs a message loop on this thread: WPF's dispatcher is one
        WatchExplorerWindows();
        ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown()), null, -1, true);
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        app.Exit += (_, _) => UnhookWindowsHookEx(hook);
        return -1; // keep running
    }

    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            if (vk == VK_E && down && WinHeld() && !Held(VK_CONTROL) && !Held(VK_MENU) && !Held(VK_SHIFT))
            {
                // Swallowing E leaves Windows seeing a lone Win press, which opens Start on release;
                // an unassigned key in between tells it Win was used as a modifier.
                TapUnassignedKey();
                swallowUp = true;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(LaunchFileLabs); // keep the hook fast
                return (IntPtr)1;
            }
            if (vk == VK_E && !down && swallowUp) { swallowUp = false; return (IntPtr)1; }

            // Ctrl+G in an Open/Save dialog: jump it to the folder File Labs is showing (Listary's
            // "quick switch"). Only the cheap checks happen here; the dialog is driven afterwards.
            if (vk == VK_G && down && Held(VK_CONTROL) && !Held(VK_MENU) && !Held(VK_SHIFT) && !WinHeld()
                && FileNameBox(GetForegroundWindow()) is var (dialog, box) && box != IntPtr.Zero && CurrentFolder() is { } folder)
            {
                swallowG = true;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => QuickSwitch(dialog, box, folder));
                return (IntPtr)1;
            }
            if (vk == VK_G && !down && swallowG) { swallowG = false; return (IntPtr)1; }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    // A plain launch: the running File Labs comes to the front, or a new one starts.
    static void LaunchFileLabs()
    {
        // We just handled the user's key press, so Windows lets us hand the right to take focus on;
        // without this File Labs opens (or is asked to come forward) behind the current window.
        AllowSetForegroundWindow(-1);
        // Off this thread: it also runs the keyboard hook, and Windows drops a hook that stops answering.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Already open: tell it directly. Starting a process only to pass that on cost ~175 ms a press.
            if (App.Running && App.HandOff("")) return;
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--front") { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception) { } // exe gone (e.g. mid-uninstall): nothing to open
        });
    }

    // ---- Explorer windows that other programs open ----
    // "Open destination folder" in qBittorrent, "Show in folder" in browsers, `explorer.exe /select` and
    // SHOpenFolderAndSelectItems all open Explorer directly: none of them goes through the Directory
    // verb that makes File Labs the default. So every new Explorer window is looked at once the shell
    // has registered it; if it shows a folder on disk, its folder and selected file are opened in File
    // Labs and the Explorer window is closed. Virtual places (Home, This PC, Control Panel, the Recycle
    // Bin, a zip) stay in Explorer, the only thing that can show them.
    public const string ExplorerWantedValue = "ExplorerWanted"; // File Labs' own "Open in File Explorer" sets it

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    static WinEventProc shown; // held so the GC can't collect the callback Windows calls
    static readonly HashSet<IntPtr> looked = new();

    static void WatchExplorerWindows()
    {
        shown = OnWindowEvent;
        SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, shown, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    // Out of sight while it is being looked at — an Explorer window flashing up and closing again looks
    // broken. Two steps, because each alone was seen to fail:
    //   • at creation it is moved off screen, asynchronously: Explorer's thread applies that before the
    //     first paint. (Making it transparent at creation made Explorer abandon windows it keeps, This
    //     PC among them; hiding it outright stopped them from ever opening.)
    //   • at showing it is also made transparent, in case Explorer put it back on screen. (Alone, that
    //     came ~150 ms late: a style change waits for Explorer's thread, busy right after showing.)
    // A window Explorer keeps gets its place and opacity back.
    sealed class Hidden { public RECT Place; public IntPtr? Style; }
    static readonly Dictionary<IntPtr, Hidden> hidden = new();

    static void OnWindowEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == EVENT_OBJECT_DESTROY || idObject != 0 || idChild != 0) return; // the window itself, created or shown
        if (ev == EVENT_OBJECT_SHOW)
        {
            if (hidden.TryGetValue(hwnd, out var h) && h.Style == null) h.Style = MakeTransparent(hwnd);
            return;
        }
        var cls = new System.Text.StringBuilder(32);
        if (GetClassName(hwnd, cls, cls.Capacity) == 0 || cls.ToString() != "CabinetWClass") return;
        if (looked.Count > 500) looked.Clear();
        if (!looked.Add(hwnd) || ExplorerWanted()) return;
        GetWindowRect(hwnd, out var place);
        hidden[hwnd] = new Hidden { Place = place };
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        // A fresh explorer.exe registers its window ~1.3 s after starting (measured); poll until then.
        int tries = 0, waitedForSelection = 0;
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        poll.Tick += (_, _) =>
        {
            if (!IsWindow(hwnd)) { poll.Stop(); hidden.Remove(hwnd); return; }
            var (window, folder, selected) = Locate(hwnd);
            if (window == null && ++tries < 100) return;
            // "Show in folder" selects the file a moment after the folder is shown; give it ~0.5 s
            if (folder != null && selected == null && ++waitedForSelection < 10) return;
            poll.Stop();
            if (folder == null) { GiveBack(hwnd); return; } // a virtual place, or never registered: Explorer keeps it
            hidden.Remove(hwnd);
            try { window.Quit(); } catch (COMException) { }
            OpenInFileLabs(selected ?? folder);
        };
        poll.Start();
    }

    /// Invisible and click-through: a layered window at alpha 0. Returns the style to restore.
    static IntPtr MakeTransparent(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)style | WS_EX_LAYERED));
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        return style;
    }

    /// The Explorer window's folder, and its selected item when there is exactly one ("show in folder").
    /// Window null: not registered with the shell yet. Folder null: not a folder on disk.
    static (dynamic Window, string Folder, string Selected) Locate(IntPtr hwnd)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic w in shell.Windows())
            {
                if (new IntPtr((long)w.HWND) != hwnd) continue;
                string path = w.Document.Folder.Self.Path;
                if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path)) return (w, null, null);
                dynamic items = w.Document.SelectedItems();
                return (w, path, items.Count == 1 ? (string)items.Item(0).Path : null);
            }
        }
        // still navigating (no document yet), or the window went away between two calls
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return (null, null, null);
    }

    /// A window Explorer keeps: back in its place, opaque, and in front where the user expected it. An
    /// empty mouse input first — Windows only lets a process that just had input take the foreground.
    static void GiveBack(IntPtr hwnd)
    {
        if (!hidden.Remove(hwnd, out var h)) return;
        if (h.Style is { } style)
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, style);
        }
        SetWindowPos(hwnd, IntPtr.Zero, h.Place.Left, h.Place.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_ASYNCWINDOWPOS);
        SendInput(1, new INPUT[1], Marshal.SizeOf<INPUT>()); // all zero = a mouse event that moves nothing
        SetForegroundWindow(hwnd);
    }

    static bool ExplorerWanted()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StateKey);
        return key?.GetValue(ExplorerWantedValue) is long ticks && DateTime.UtcNow.Ticks - ticks < TimeSpan.FromSeconds(10).Ticks;
    }

    static void OpenInFileLabs(string path) => ThreadPool.QueueUserWorkItem(_ =>
    {
        if (App.Running && App.HandOff(path)) return;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath) { ArgumentList = { "--front", path }, UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { } // exe gone (e.g. mid-uninstall)
    });

    // ---- quick switch ----
    static bool swallowG;
    public const string StateKey = @"Software\Protagonist Labs\File Labs";

    /// The folder in File Labs' active pane, which the app writes whenever it changes.
    static string CurrentFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StateKey);
        return key?.GetValue("CurrentFolder") is string s && s != "" ? s : null;
    }

    /// For a common Open/Save dialog: the dialog and its file-name edit box (control 1148 or inside it).
    static (IntPtr Dialog, IntPtr Box) FileNameBox(IntPtr window)
    {
        var cls = new System.Text.StringBuilder(64);
        if (window == IntPtr.Zero || GetClassName(window, cls, cls.Capacity) == 0 || cls.ToString() != "#32770") return (window, IntPtr.Zero);
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            var c = new System.Text.StringBuilder(64);
            GetClassName(child, c, c.Capacity);
            if (c.ToString() != "Edit") return true;
            for (var h = child; h != IntPtr.Zero && h != window; h = GetParent(h))
                if (GetDlgCtrlID(h) == 1148) { found = child; return false; }
            return true;
        }, IntPtr.Zero);
        return (window, found);
    }

    // Typing a folder into the file name box and pressing Open makes the dialog go there instead of
    // opening anything. The name that was in the box (a Save dialog's file name) is put back after.
    static void QuickSwitch(IntPtr dialog, IntPtr box, string folder)
    {
        var old = new System.Text.StringBuilder(1024);
        SendMessage(box, WM_GETTEXT, (IntPtr)old.Capacity, old);
        SendMessage(box, WM_SETTEXT, IntPtr.Zero, new System.Text.StringBuilder(folder));
        SendMessage(dialog, WM_COMMAND, (IntPtr)1 /* IDOK */, IntPtr.Zero);
        SendMessage(box, WM_SETTEXT, IntPtr.Zero, old);
    }

    static bool WinHeld() => Held(VK_LWIN) || Held(VK_RWIN);
    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static void TapUnassignedKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = inputs[1].type = 1; // keyboard
        inputs[0].ki.wVk = inputs[1].ki.wVk = 0xE8;
        inputs[1].ki.dwFlags = 2; // key up
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    const int WM_GETTEXT = 0x0D, WM_SETTEXT = 0x0C, WM_COMMAND = 0x111, VK_G = 0x47;
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;
    const uint LWA_ALPHA = 2;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, System.Text.StringBuilder l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    const int WH_KEYBOARD_LL = 13, VK_E = 0x45, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10;
    static readonly IntPtr WM_KEYDOWN = (IntPtr)0x100, WM_SYSKEYDOWN = (IntPtr)0x104;

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    // INPUT is a union; pad to the size of its largest member (MOUSEINPUT) so SendInput accepts it
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
