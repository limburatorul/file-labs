using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileExplorer;

// The window without Windows' title bar (1.0.43): its own minimize / maximize / close at the right end of the
// tab row, the history menu beside them, and the notification-area icon for "close to the notification area".
public partial class MainWindow
{
    const double CaptionWidth = 40 + 3 * 46 + 6; // the four buttons and a little air, kept free in the tab row under them

    void SetUpChrome()
    {
        VersionText.Text = Updater.Current.ToString();
        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(CaptionHook);
            // The frame reaches over the whole window (for the acrylic), and Windows draws its own caption buttons
            // in it, faintly, behind ours. The theme attribute stops that in a normal window; maximized, only a
            // window without the system-menu style is spared. Set once, before the first frame: changing the style
            // later brings them back. The menu itself still opens from a right-click on the tab row
            // (SystemCommands.ShowSystemMenu); Alt+Space no longer does.
            SetWindowLong(hwnd, -16 /* GWL_STYLE */, GetWindowLong(hwnd, -16) & ~0x00080000 /* WS_SYSMENU */);
            var options = new ThemeOptions { Flags = 1 | 2 | 4, Mask = 1 | 2 | 4 }; // NODRAWCAPTION | NODRAWICON | NOSYSMENU
            SetWindowThemeAttribute(hwnd, 1 /* WTA_NONCLIENT */, ref options, (uint)Marshal.SizeOf<ThemeOptions>());
        };
        ApplyWindowState();
        PaneView.Visited += path => Settings.Bump(Settings.RecentFolders, path, Settings.RecentMax);
        PaneView.TabClosed += path => { closedTabs.Remove(path); closedTabs.Insert(0, path); if (closedTabs.Count > 10) closedTabs.RemoveAt(10); };
        Application.Current.SessionEnding += (_, _) => quitting = true; // signing out must not stop at the tray
        ApplyTray();
    }

    /// A maximized window without a title bar reaches past the screen by its resize border; the margin takes
    /// that back so nothing is cut off. The glyph follows the state, as in Windows' own button.
    void ApplyWindowState()
    {
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        if (!Themes.Current.Lights) MaxButton.Content = max ? "" : ""; // the lights keep their green dot
        MaxButton.ToolTip = max ? "Restore down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, (string)MaxButton.ToolTip);
    }

    /// Which element sits under the window's buttons, top right: the right pane, the left one when it is
    /// alone, or the preview pane's header. That one keeps the room free; the others get it back.
    void PlaceCaption()
    {
        var rightmost = Settings.SinglePane ? Left : Right;
        Left.ReserveCaption(!Settings.Preview && rightmost == Left ? CaptionWidth : 0);
        Right.ReserveCaption(!Settings.Preview && rightmost == Right ? CaptionWidth : 0);
        PreviewHeader.Margin = new Thickness(12, Settings.Preview ? 40 : 6, 6, 4);
    }

    struct ThemeOptions { public uint Flags, Mask; }
    [DllImport("uxtheme.dll")] static extern int SetWindowThemeAttribute(IntPtr hwnd, int attribute, ref ThemeOptions options, uint size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    const int WM_NCHITTEST = 0x84, WM_NCMOUSELEAVE = 0x2A2, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, HTCAPTION = 2, HTMAXBUTTON = 9;

    /// The empty part of the tab row and the app's name in the sidebar behave like a title bar: drag moves the
    /// window (Windows does the moving, so Aero Snap and dragging off a maximized window work), double-click
    /// maximizes, right-click opens the window menu.
    long captionDownAt; // the double-click is timed here: the move loop of the first click swallows WPF's own count
    Point captionDownPos;

    internal void CaptionDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            var at = e.GetPosition(this);
            bool second = Environment.TickCount64 - captionDownAt <= PaneView.GetDoubleClickTime()
                          && Math.Abs(at.X - captionDownPos.X) <= SystemParameters.MinimumHorizontalDragDistance
                          && Math.Abs(at.Y - captionDownPos.Y) <= SystemParameters.MinimumVerticalDragDistance;
            captionDownAt = second ? 0 : Environment.TickCount64;
            captionDownPos = at;
            if (second || e.ClickCount == 2) ToggleMaximize();
            else
            {
                ReleaseCapture();
                SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
            }
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            var at = PointToScreen(e.GetPosition(this)); // device pixels; the menu wants device-independent ones
            var dpi = VisualTreeHelper.GetDpi(this);
            SystemCommands.ShowSystemMenu(this, new Point(at.X / dpi.DpiScaleX, at.Y / dpi.DpiScaleY));
            e.Handled = true;
        }
    }

    void Caption_MouseDown(object s, MouseButtonEventArgs e) => CaptionDown(e);

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object s, RoutedEventArgs e) => ToggleMaximize();
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // Windows 11 shows its snap layouts when the pointer rests on a window's maximize button, which it knows by
    // the hit test answering HTMAXBUTTON. Clicks on it then arrive as non-client messages and are handled here.
    IntPtr CaptionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                bool over = OverMaxButton(lParam);
                MaxButton.Tag = over ? "hover" : null;
                if (over) { handled = true; return HTMAXBUTTON; }
                break;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                handled = true;
                ToggleMaximize();
                break;
            case WM_NCMOUSELEAVE:
                MaxButton.Tag = null;
                break;
        }
        return IntPtr.Zero;
    }

    bool OverMaxButton(IntPtr lParam)
    {
        // a hit test can still arrive while the window closes, after its presentation source is gone: PointToScreen
        // threw there and took the app down (seen in 1.0.50's live test)
        if (!MaxButton.IsVisible || PresentationSource.FromVisual(MaxButton) == null) return false;
        int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16)); // screen pixels
        var topLeft = MaxButton.PointToScreen(new Point(0, 0));
        var bottomRight = MaxButton.PointToScreen(new Point(MaxButton.ActualWidth, MaxButton.ActualHeight));
        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    // ---- history: recent folders and recently closed tabs, like a browser's ----
    readonly List<string> closedTabs = new(); // this session only, newest first

    void ReopenClosedTab()
    {
        if (closedTabs.Count == 0) { Status.Text = "No closed tab to reopen"; return; }
        var path = closedTabs[0];
        closedTabs.RemoveAt(0);
        active.NewTab();
        active.Navigate(path, record: false);
        active.FocusList();
    }

    void History_Click(object s, RoutedEventArgs e)
    {
        // hangs from the button's right edge, as a browser's history menu does: the button is at the window's right
        var menu = new ContextMenu
        {
            PlacementTarget = HistoryButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) => new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(target.Width - popup.Width, target.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) },
        };
        MenuItem Folder(string path, Action open)
        {
            var mi = new MenuItem
            {
                Header = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
                InputGestureText = ParentForMenu(path), // where it is, muted at the right
                ToolTip = path,
                Icon = ShellIcon(path, "", 16, new Thickness(0)),
            };
            mi.Click += (_, _) => open();
            return mi;
        }

        if (closedTabs.Count > 0)
        {
            menu.Items.Add(Item("Reopen closed tab", "", "Ctrl+Shift+T", ReopenClosedTab));
            menu.Items.Add(new MenuItem { Header = "Recently closed tabs", IsEnabled = false });
            foreach (var path in closedTabs.ToList())
                menu.Items.Add(Folder(path, () => { closedTabs.Remove(path); active.NewTab(); active.Navigate(path, record: false); active.FocusList(); }));
            menu.Items.Add(new Separator());
        }
        var recent = Settings.RecentFolders.Where(p => !p.Equals(active.Dir, StringComparison.OrdinalIgnoreCase)).ToList();
        menu.Items.Add(new MenuItem { Header = recent.Count > 0 ? "Recent folders" : "No recent folders yet", IsEnabled = false });
        foreach (var path in recent)
            menu.Items.Add(Folder(path, () => { active.Navigate(path); active.FocusList(); }));
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Clear history", "", "", () => { Settings.RecentFolders.Clear(); closedTabs.Clear(); Settings.Save(); }));
        }
        menu.IsOpen = true;
    }

    /// The folder a path is in, short enough for a menu: past 40 characters, "C:\…\Projects\Site".
    internal static string ParentForMenu(string path)
    {
        var dir = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        var parts = dir.Split('\\');
        return dir.Length <= 40 || parts.Length <= 3 ? dir : $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    // ---- reusing a tab: a folder opened from outside switches to the tab that already shows it ----
    /// The pane and tab showing `folder` (the active pane first), or null.
    (PaneView Pane, int Tab)? TabShowing(string folder)
    {
        foreach (var p in new[] { active, Other })
            if (p.Visibility == Visibility.Visible) // the hidden right pane, with one pane, doesn't count
                if (p.FindTab(folder) is >= 0 and var i) return (p, i);
        return null;
    }

    // ---- the startup clock ----
    bool filesTimed;
    void FilesShown()
    {
        if (filesTimed || startHidden || !Left.Listed || !(Settings.SinglePane || Right.Listed)) return;
        filesTimed = true;
        StartupClock.FilesShown();
    }

    // ---- started at sign-in (--tray): drawn once off screen, then hidden, so the first open is as fast as the next ----
    bool startHidden;
    (double Left, double Top, WindowState State) shownAt;

    /// Called before Show(): the window is built and drawn where nobody sees it, without taking focus.
    internal void PrepareHiddenStart()
    {
        startHidden = true;
        shownAt = (((Window)this).Left, Top, WindowState);
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        ((Window)this).Left = -32000; Top = -32000;
        ShowActivated = false;
        ShowInTaskbar = false;
    }

    void FinishHiddenStart()
    {
        Hide();
        ((Window)this).Left = shownAt.Left; Top = shownAt.Top;
        ShowInTaskbar = true;
        WindowState = shownAt.State;
        startHidden = false;
    }

    // ---- the notification area: closing hides the window, the next open shows it at once ----
    System.Windows.Forms.NotifyIcon tray;
    bool quitting; // a real exit (Quit, updating, signing out) closes even with "close to the notification area" on

    public void ApplyTray()
    {
        if (Settings.CloseToTray && tray == null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open File Labs", null, (_, _) => ShowFromTray());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Text = "File Labs",
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
        }
        else if (!Settings.CloseToTray && tray != null)
        {
            tray.Dispose();
            tray = null;
            if (!IsVisible) ShowFromTray(); // never leave a hidden window with no icon to bring it back
        }
    }

    void ShowFromTray()
    {
        if (startHidden) return; // still drawing itself off screen, a moment after sign-in
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        active.FocusList();
    }

    public void Quit()
    {
        quitting = true;
        Close();
    }
}

/// How long the last starts took, from launching the process to the window's first frame and to the files in its
/// panes, with whether the PC was on battery. Shown in Settings → About and kept in startup.log (the last 30), so a
/// slow start on another PC can be read there instead of guessed.
static class StartupClock
{
    static string Log => System.IO.Path.Combine(Settings.Dir, "startup.log");
    static double windowMs;

    public static void WindowShown() => windowMs = Since();

    static double Since() => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    public static void FilesShown()
    {
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm}  window {windowMs:0} ms  files {Since():0} ms  {(SystemParameters.PowerLineStatus == PowerLineStatus.Offline ? "on battery" : "plugged in")}  {Updater.Current}");
        Task.Run(() =>
        {
            try
            {
                var lines = File.Exists(Log) ? File.ReadAllLines(Log).TakeLast(29).ToList() : new List<string>();
                lines.Add(line);
                Directory.CreateDirectory(Settings.Dir);
                Settings.WriteAllLines(Log, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // only a record
        });
    }

    /// The last start, for About: from the log, so a start hidden at sign-in (not timed) shows the one before.
    public static string Last
    {
        get
        {
            try { return File.Exists(Log) && File.ReadLines(Log).LastOrDefault() is { Length: > 0 } l ? "Last start: " + l : ""; }
            catch (IOException) { return ""; }
        }
    }
}
