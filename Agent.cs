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
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--front") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { } // exe gone (e.g. mid-uninstall): nothing to open
    }

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
