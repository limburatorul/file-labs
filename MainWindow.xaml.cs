using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using VbFs = Microsoft.VisualBasic.FileIO.FileSystem;

namespace FileExplorer;

public partial class MainWindow : Window
{
    PaneView active;
    PaneView Other => active == Left ? Right : Left;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"File Labs {Updater.Current}";
        PaneView.ShowHidden = Settings.ShowHidden;
        SidebarColumn.Width = new GridLength(Settings.SidebarWidth);
        SetUpChrome();
        ApplyPanes();
        ApplyStats();
        previewDelay.Tick += PreviewTick;
        Activated += (_, _) => PaneView.WindowActivatedAt = Environment.TickCount64; // that click doesn't rename
        // restore size/position only if it's still on a connected screen
        if (Settings.WindowBounds is { } wb && new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight).IntersectsWith(wb))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            ((Window)this).Left = wb.Left; Top = wb.Top; Width = wb.Width; Height = wb.Height;
        }
        if (Settings.Maximized) WindowState = WindowState.Maximized;
        Closing += (_, e) =>
        {
            Settings.WindowBounds = RestoreBounds;
            Settings.Maximized = WindowState == WindowState.Maximized;
            KeepSplit();
            foreach (var p in new[] { Left, Right })
            {
                Settings.Tabs[p.Name] = p.OpenTabs.ToList();
                Settings.ActiveTab[p.Name] = p.ActiveTab;
                Settings.Pins[p.Name] = p.PinnedTabs;
            }
            Settings.Save();
            // with the notification-area icon on, closing only hides: the next open is instant
            if (tray != null && !quitting) { e.Cancel = true; Hide(); }
        };
        Closed += (_, _) => tray?.Dispose(); // or the icon lingers until the pointer passes over it
        SourceInitialized += (_, _) => { ApplyAcrylic(this); WatchShellChanges(); };
        StartUpdateChecks();

        foreach (var p in new[] { Left, Right })
        {
            p.Activated += () => SetActive(p);
            p.StatsChanged += () => { if (p == active) { UpdateStats(); PreviewFollow(); } QuickLookFollow(p); FilesShown(); };
            p.Error += msg => Status.Text = msg;
            p.FilesDropped += (files, dest, move) => { if (PaneView.ArchiveOf(dest) != null) AddToArchive(p, files, dest); else Submit(files, dest, move); };
            p.OpenArchived += e => OpenFromArchive(p, e);
            p.RenameCommitted += (entry, name) => RenameCommitted(p, entry, name);
        }

        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (icon, name, path) in new[] {
            ("", "Home", user),
            ("", "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("", "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("", "Downloads", Path.Combine(user, "Downloads")),
            ("", "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("", "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("", "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)) })
            if (Directory.Exists(path)) Favorites.Children.Add(SideButton(icon, name, path));
        Favorites.Children.Insert(0, SideButton("", "This PC", PaneView.ThisPC)); // the drives, in the pane (Rico, 1.0.53)
        // the shell's parsing name for the Recycle Bin: only used here to fetch its real icon
        Favorites.Children.Add(SideButton("", "Recycle Bin", "::{645FF040-5081-101B-9F08-00AA002F954E}", click: OpenRecycleBin));

        // Windows' own menu on each, as on a drive in Drives (Properties, Empty Recycle Bin, Manage…) (Rico, 1.0.53)
        foreach (var b in Favorites.Children.OfType<Button>())
        {
            var path = b.ToolTip as string == "Recycle Bin" ? "::{645FF040-5081-101B-9F08-00AA002F954E}" : (string)b.ToolTip;
            b.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShellMenu.Show(this, path.StartsWith("::") ? path : Path.GetDirectoryName(path) ?? path, new[] { path }, rename: false);
            };
        }

        LoadQuickAccess();
        ApplyAppearance(); // the accent and row height the user picked, before anything is drawn
        Ferry.Finished += JobDone;
        foreach (var p in new[] { Left, Right })
        {
            HookContextMenu(p);
        }

        // the drive rows and the folder tree's drives come after the first frame (RefreshDrives in ContentRendered):
        // building them took 200-270 ms of a throttled start
        driveTimer.Tick += (_, _) => { RefreshDrives(); UpdateStats(); }; // free space changes without us doing anything
        // The sidebar's folders are one click away, so read them into the panes' caches while the user
        // is still looking at the first folder.
        var warmUp = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        warmUp.Tick += (_, _) =>
        {
            warmUp.Stop();
            var paths = pinned.Concat(shownDrives.Select(d => d.Name))
                .Concat(Favorites.Children.OfType<Button>().Select(b => b.ToolTip as string))
                .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith("::")).ToList(); // This PC isn't a folder on disk
            Left.PrefetchPaths(paths);
            Right.PrefetchPaths(paths);
        };
        warmUp.Start();
        BuildFolderTree();
        BuildSidebar();


        active = Left;
        // last session's tabs, then the folder asked for on the command line as a new tab
        foreach (var (p, fallback) in new[] { (Left, user), (Right, Path.GetPathRoot(Environment.SystemDirectory)) })
            if (!Settings.Tabs.TryGetValue(p.Name, out var paths) || !p.Restore(paths, Settings.ActiveTab.TryGetValue(p.Name, out var a) ? a : 0, Settings.Pins.GetValueOrDefault(p.Name)))
                p.Navigate(fallback);
        SetActive(Left); // first: a folder already open in the right pane makes that one active
        ApplyCloseEmptyPane();
        var arg = Program.PathArg(Environment.GetCommandLineArgs().Skip(1));
        if (arg != "") OpenPath(Left, arg);
        ApplyPreview();
        Loaded += (_, _) => active.FocusList();
        // started from the Win+E agent or a folder double-click: make sure we open in front.
        // After the first paint — on Loaded the window isn't on screen yet and the flip does nothing.
        ContentRendered += (_, _) =>
        {
            PaneView.WindowShown = true;
            StartupClock.WindowShown();
            RefreshDrives();
            driveTimer.Start();
            Dispatcher.BeginInvoke(SizeCache.StartLoading, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(Motion.WarmPopups, System.Windows.Threading.DispatcherPriority.ApplicationIdle); // the first menu's stall, paid now
            if (startHidden) { FinishHiddenStart(); return; } // started at sign-in: shown by the notification-area icon later
            // Started by the Win+E agent (--front): Windows' foreground lock keeps a freshly started
            // background-launched window behind the current one.
            if (Environment.GetCommandLineArgs().Contains("--front")) TakeForeground();
            Topmost = true; Topmost = false; Activate(); active.FocusList();
        };
    }

    // Acrylic behind the whole window (Branding → Aplicatii, WPF section).
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    struct Margins { public int L, R, T, B; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, byte[] inputs, int size);

    // Windows only lets a window it didn't see clicked come to the front if its process has just had
    // input. An empty mouse event is input and does nothing else (PowerToys does the same). The Alt tap
    // used before could reach this window once it was already in front, and the next Space then opened
    // its system menu instead of Quick View.
    void TakeForeground()
    {
        int size = IntPtr.Size == 8 ? 40 : 28; // one INPUT; all zero = a mouse event that moves nothing
        SendInput(1, new byte[size], size);
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    public static void ApplyAcrylic(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        // Default is opaque black: partial-alpha pixels would be composed over it and look muddy.
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        int dark = 1, backdrop = Settings.Backdrop;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));     // dark title bar
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE: 1 none, 2 mica, 3 acrylic
    }

    // Sync apps announce status changes through SHChangeNotify ("this item's icon changed"), which is
    // how Explorer's badges update live. We listen for the same notices and let the panes re-ask.
    const int WM_SHELLNOTIFY = 0x0400 + 0x51; // WM_USER-based, private to this window
    const int SHCNE_ATTRIBUTES = 0x800, SHCNE_UPDATEDIR = 0x1000, SHCNE_UPDATEITEM = 0x2000;

    void WatchShellChanges()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(ShellNotify);
        SHGetSpecialFolderLocation(IntPtr.Zero, 0 /* CSIDL_DESKTOP: everything below it */, out var desktop);
        var entry = new SHChangeNotifyEntry { pidl = desktop, fRecursive = true };
        SHChangeNotifyRegister(hwnd, 0x1000 | 0x8000 /* SHCNRF_ShellLevel | SHCNRF_NewDelivery */,
            SHCNE_ATTRIBUTES | SHCNE_UPDATEDIR | SHCNE_UPDATEITEM, WM_SHELLNOTIFY, 1, ref entry);
    }

    IntPtr ShellNotify(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SHELLNOTIFY) return IntPtr.Zero;
        handled = true;
        var lk = SHChangeNotification_Lock(wParam, (int)lParam, out var pidls, out _);
        if (lk == IntPtr.Zero) return IntPtr.Zero;
        string path = null;
        var first = Marshal.ReadIntPtr(pidls);
        var sb = new System.Text.StringBuilder(1024);
        if (first != IntPtr.Zero && SHGetPathFromIDListEx(first, sb, sb.Capacity, 0)) path = sb.ToString();
        SHChangeNotification_Unlock(lk);
        if (!string.IsNullOrEmpty(path)) { Left.OverlayChanged(path); Right.OverlayChanged(path); }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct SHChangeNotifyEntry { public IntPtr pidl; [MarshalAs(UnmanagedType.Bool)] public bool fRecursive; }
    [DllImport("shell32.dll")] static extern uint SHChangeNotifyRegister(IntPtr hwnd, int sources, int events, int msg, int count, ref SHChangeNotifyEntry entry);
    [DllImport("shell32.dll")] static extern IntPtr SHChangeNotification_Lock(IntPtr change, int processId, out IntPtr pidls, out int eventId);
    [DllImport("shell32.dll")] static extern bool SHChangeNotification_Unlock(IntPtr lk);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDListEx(IntPtr pidl, System.Text.StringBuilder path, int cch, int flags);
    [DllImport("shell32.dll")] static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int folder, out IntPtr pidl);

    // Appearance lives in Settings; this applies it to a window.
    /// The background picture, read once per path: the opacity slider re-applies the appearance on
    /// every step. Decoded at most 2560 px wide, enough for a window and far less memory than a photo.
    static string bgPath;
    static ImageSource bgImage;
    static ImageSource BackgroundPicture(string path)
    {
        if (path == bgPath) return bgImage;
        bgPath = path; bgImage = null;
        if (path == "" || !File.Exists(path)) return null;
        try
        {
            var b = new System.Windows.Media.Imaging.BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // the file is not kept open
            b.DecodePixelWidth = 2560;
            b.EndInit();
            b.Freeze();
            bgImage = b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException) { } // not a picture after all
        return bgImage;
    }

    public static Brush Tint() =>
        new SolidColorBrush(Color.FromArgb((byte)(Settings.Backdrop == 1 ? 255 : Settings.Opacity * 2.55), 0x0A, 0x0D, 0x13));

    public static void SetBackdrop(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int b = Settings.Backdrop;
        DwmSetWindowAttribute(h, 38, ref b, sizeof(int));
    }

    public void ApplyAppearance()
    {
        Root.Background = Tint();
        SetBackdrop(this);
        Application.Current.Resources["ItemTips"] = Settings.ItemTips; // the rows' tooltips (App.xaml)
        if (quick != null) SetBackdrop(quick);
        EditorWindow.AccentChanged();
        ApplyAccent();
        Application.Current.Resources["RowPad"] = new Thickness(4, Settings.RowPad, 4, Settings.RowPad);
        // Scaling the whole window rather than each font: sizes are set in many places, and icons,
        // rows and buttons have to grow with the text or it gets clipped.
        Root.LayoutTransform = Settings.TextScale > 1 ? new ScaleTransform(Settings.TextScale, Settings.TextScale) : null;
        SidebarPanel.LayoutTransform = Settings.SidebarScale != 1 ? new ScaleTransform(Settings.SidebarScale, Settings.SidebarScale) : null;
        Left.ApplyScales(); Right.ApplyScales();
        BgImage.Source = BackgroundPicture(Settings.BackgroundImage);
        BgLayer.Visibility = BgImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
        BgLayer.Opacity = Settings.ImageOpacity / 100;
        // a blur fades towards transparent at the picture's edges, so the picture is made larger by
        // the radius and the overflow clipped
        BgImage.Effect = Settings.ImageBlur > 0 ? new System.Windows.Media.Effects.BlurEffect { Radius = Settings.ImageBlur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } : null;
        BgImage.Margin = new Thickness(-Settings.ImageBlur);
        BgShade.Fill = Settings.ImageBrightness < 0 ? Brushes.Black : Brushes.White;
        BgShade.Opacity = Math.Abs(Settings.ImageBrightness) / 100;
        // the pane edge is painted by hand, so a new accent has to be painted on by hand
        Left.SetActive(active == Left); Right.SetActive(active == Right);
    }

    /// Replaces the four accent brushes, at their four transparencies. WPF freezes brushes declared in
    /// XAML, so the colour cannot be changed in place — the resource itself is swapped, and everything
    /// that asks for it with a DynamicResource repaints. Selection, bars, the active pane's edge.
    public static void ApplyAccent()
    {
        var c = AccentColor;
        Shade("Accent", 0xFF); Shade("AccentEdge", 0xA6); Shade("AccentSoft", 0x45);
        var (fill, text) = Selection(c, Settings.AccentStrength);
        Put("AccentSelect", fill); Put("AccentSelectText", text);
        void Shade(string key, byte alpha)
        {
            // "Accent" itself is the solid fill for bars and must stay opaque; the tints follow the slider.
            if (key != "Accent") alpha = (byte)Math.Clamp(alpha * Settings.AccentStrength, 12, 255);
            Put(key, Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        static void Put(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }

    /// The selection fill and the text on it. The fill is the accent tint laid on a dark ground (#181A20) and
    /// opaque: a see-through one took on whatever the glass showed behind it, and over a bright wallpaper the
    /// selected name washed out. The ground was the panes' surface (#2C2E35) until 1.0.38; on it the default
    /// accent gave a mid blue (#3A5990, 6.97:1) on which white still read as grey. The text is white, or black
    /// where black reads better (a light accent at a high strength); one of the two always reaches 4.5:1 (WCAG AA).
    internal static (Color Fill, Color Text) Selection(Color accent, double strength)
    {
        double a = Math.Clamp(0x73 * strength, 12, 255) / 255;
        byte Mix(byte x, byte ground) => (byte)Math.Round(x * a + ground * (1 - a));
        var fill = Color.FromRgb(Mix(accent.R, 0x18), Mix(accent.G, 0x1A), Mix(accent.B, 0x20));
        return (fill, Contrast(Colors.White, fill) >= Contrast(Colors.Black, fill) ? Colors.White : Colors.Black);
    }

    /// WCAG contrast ratio of two opaque colours, 1 to 21.
    internal static double Contrast(Color x, Color y)
    {
        static double Lum(Color c)
        {
            static double Lin(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        double lx = Lum(x), ly = Lum(y);
        return (Math.Max(lx, ly) + 0.05) / (Math.Min(lx, ly) + 0.05);
    }

    public static Color AccentColor =>
        ((SolidColorBrush)Hex(Settings.Accent)).Color;

    public void RefreshPanes()
    {
        PaneView.ShowHidden = Settings.ShowHidden;
        Left.Refresh(); Right.Refresh();
    }

    public void ResortPanes() { foreach (var p in new[] { Left, Right }) p.SetSort(p.SortKey, p.SortDescending); }

    // 3 s after launch, then every hour — the app stays open for days.
    readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(60) };
    UpdateDialog updateDialog;

    void StartUpdateChecks()
    {
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            // started hidden at sign-in: What's new and an update offer wait for the window to be opened
            if (!IsVisible || startHidden) { await WhenVisible(); await Task.Delay(1500); }
            ShowWhatsNew();
            await CheckForUpdate(manual: false);
        };
        first.Start();
        updateTimer.Tick += async (_, _) => await CheckForUpdate(manual: false);
        updateTimer.Start();
    }

    Task WhenVisible()
    {
        var shown = new TaskCompletionSource();
        DependencyPropertyChangedEventHandler h = null;
        h = (_, _) => { if (IsVisible) { IsVisibleChanged -= h; shown.TrySetResult(); } };
        IsVisibleChanged += h;
        return shown.Task;
    }

    /// Returns what to tell the user (only used by the manual check in Settings).
    public async Task<string> CheckForUpdate(bool manual)
    {
        if (updateDialog != null) { Bring(updateDialog); return null; }
        Updater.Release release;
        try { release = await Updater.Check(manual); }
        catch (Exception ex) when (Updater.Failed(ex))
        {
            return manual ? Updater.CheckFailed : null; // the automatic check stays quiet and tries again next hour
        }
        if (release == null) return manual ? $"File Labs {Updater.Current} is the latest version." : null;

        updateDialog = new UpdateDialog(this, release);
        updateDialog.Closed += (_, _) => { Updater.Dismiss(release); updateDialog = null; };
        updateDialog.Show();
        return null;
    }

    // First launch after an update: the release notes for the version now running.
    void ShowWhatsNew()
    {
        var current = Updater.Current.ToString();
        if (Settings.LastSeenVersion == current) return;
        bool upgraded = Settings.LastSeenVersion != "";       // empty = fresh install, nothing to show
        Settings.LastSeenVersion = current;
        Settings.Save();
        if (!upgraded) return;
        OpenWhatsNew();
    }

    WhatsNewWindow whatsNewWindow;
    public void OpenWhatsNew()
    {
        if (whatsNewWindow != null) { Bring(whatsNewWindow); return; }
        whatsNewWindow = new WhatsNewWindow(this);
        whatsNewWindow.Closed += (_, _) => whatsNewWindow = null;
        whatsNewWindow.Show();
    }
    void WhatsNew_Click(object s, RoutedEventArgs e) => OpenWhatsNew();

    /// An open Settings, Help, What's new or update offer to the front. Activate alone leaves a window minimized to the taskbar
    /// where it is, so the gear did nothing then (1.0.48 report).
    static void Bring(Window w)
    {
        if (w.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(w); // as its taskbar button does: maximized again if it was
        w.Activate();
    }

    SettingsWindow settingsWindow;
    void OpenSettings()
    {
        if (settingsWindow != null) { Bring(settingsWindow); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }
    void Settings_Click(object s, RoutedEventArgs e) => OpenSettings();

    HelpWindow helpWindow;
    void OpenHelp()
    {
        if (helpWindow != null) { Bring(helpWindow); return; }
        helpWindow = new HelpWindow(this);
        helpWindow.Closed += (_, _) => helpWindow = null;
        helpWindow.Show();
    }
    void Help_Click(object s, RoutedEventArgs e) => OpenHelp();

    // The same check the timer runs, on demand. A new version opens the update dialog; otherwise the
    // answer ("… is the latest version") goes to the status bar, so nothing pops up for nothing.
    async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        Status.Text = "Checking for updates…";
        var answer = await CheckForUpdate(manual: true);
        UpdateButton.IsEnabled = true;
        // The status line is the folder's and is rewritten when the folder changes, which is fine for an answer
        // read at once; the button's tooltip keeps it until the next check.
        var text = answer == null ? "" : answer == Updater.CheckFailed ? "Couldn't check for updates · try later" : $"Up to date · File Labs {Updater.Current}";
        Status.Text = text;
        UpdateButton.ToolTip = answer == null ? "Check for updates (it also checks by itself every hour)" : text;
    }

    // A second launch sent us a folder → new tab in the active pane; a file → its folder, with the file selected
    // and the keyboard on it (PaneView.Navigate selects once the folder is read). Sent nothing (Win+E, Start menu,
    // taskbar) → just bring the existing window to the front, like switching to it.
    public void OpenFromOutside(string path)
    {
        if (startHidden) FinishHiddenStart(); // opened before the sign-in start finished drawing
        if (!IsVisible) Show(); // hidden to the notification area
        if (!string.IsNullOrEmpty(path)) OpenPath(active, path);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // Win+E arrives straight from the agent now, which can't pass on the right to take focus (a
        // process started only to relay it could, at ~175 ms a press).
        TakeForeground();
        // a brief Topmost flip gets the window above others even when Windows refuses a plain Activate
        Topmost = true; Topmost = false;
        Activate();
        active.FocusList();
    }

    /// A folder opens as a new tab; a file opens its folder with the file selected — what "show in
    /// folder" asks for (the agent passes those on from Explorer windows other programs open). A folder
    /// already open in a tab, in either pane, is switched to instead of opened twice.
    void OpenPath(PaneView pane, string path)
    {
        string folder, select = null;
        try
        {
            if (File.Exists(path)) { folder = Path.GetDirectoryName(Path.GetFullPath(path)); select = Path.GetFileName(path); }
            else if (PaneView.IsRemote(path) || Directory.Exists(path)) folder = Path.GetFullPath(path);
            else return;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (TabShowing(folder) is var (open, i))
        {
            open.ShowTab(i, select);
            SetActive(open);
            return;
        }
        pane.NewTab();
        pane.Navigate(folder, select);
    }

    /// A file found elsewhere in File Labs (Find duplicates): its folder, with it selected, in the active pane.
    internal void ShowFile(string path)
    {
        OpenPath(active, path);
        active.FocusList();
        Activate();
    }

    /// Find duplicates in this folder (and its subfolders, if asked in the window), in a window of its own.
    void FindDuplicates(params string[] folders) => new DuplicatesWindow(this, folders).Show();

    /// Explorer on purpose: the agent is told to leave the window alone instead of taking it over.
    static void OpenInExplorer(string arguments)
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Agent.StateKey))
            key.SetValue(Agent.ExplorerWantedValue, DateTime.UtcNow.Ticks, Microsoft.Win32.RegistryValueKind.QWord);
        Process.Start("explorer.exe", arguments);
    }

    void SidebarSplitter_DragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Settings.SidebarWidth = SidebarColumn.ActualWidth;
        Settings.Save();
    }
    /// The split the user dragged to, read before it is lost: on closing, and before one pane takes it all.
    void KeepSplit()
    {
        if (Settings.SinglePane) return; // one pane fills the row, there is no split to keep
        var cols = PaneGrid.ColumnDefinitions;
        double total = cols[0].ActualWidth + cols[2].ActualWidth;
        if (total > 0) Settings.Split = cols[0].ActualWidth / total; // closed before layout: keep the old value
    }

    /// One pane or two. The right pane keeps its tabs while hidden and comes back where it was.
    void ApplyPanes()
    {
        var cols = PaneGrid.ColumnDefinitions;
        bool one = Settings.SinglePane;
        Right.Visibility = PaneSplitter.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
        cols[1].Width = new GridLength(one ? 0 : 10);
        cols[2].MinWidth = one ? 0 : 250;
        cols[0].Width = new GridLength(one ? 1 : Settings.Split, GridUnitType.Star);
        cols[2].Width = new GridLength(one ? 0 : 1 - Settings.Split, GridUnitType.Star);
        if (one && active == Right) SetActive(Left);
        FitPreview(); // one pane leaves the preview more room
        PlaceCaption();
    }

    /// <summary>The cards below the panes: full, compact (the figure and its label, nothing else), or hidden.</summary>
    public void ApplyStats()
    {
        StatsRow.Visibility = Settings.Stats == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool full = Settings.Stats == 2;
        foreach (var e in new FrameworkElement[] { DriveIcon, FolderIcon, SelIcon, DriveBar, KindBar, StatFolderSub, SelSub })
            e.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        foreach (var card in StatsRow.Children.OfType<Border>())
            card.Padding = full ? new Thickness(12, 9, 14, 9) : new Thickness(10, 3, 12, 3);
        StatsRow.Margin = new Thickness(0, full ? 10 : 8, 0, 0);
    }

    /// Settings → "Close the second pane with its last tab": closing that tab hides the pane, as Ctrl+P does.
    public void ApplyCloseEmptyPane() => Right.CloseLastTab = Settings.CloseEmptyPane ? () => SetSinglePane(true) : null;

    public void SetSinglePane(bool on)
    {
        if (on == Settings.SinglePane) return;
        KeepSplit();
        Settings.SinglePane = on;
        Settings.Save();
        ApplyPanes();
        if (!on) Motion.Enter(Right); // the second pane fades in; resizing it per frame would relayout both panes every frame
        active?.FocusList();
    }

    void PaneSplitter_Reset(object s, MouseButtonEventArgs e)
    {
        var cols = ((Grid)((GridSplitter)s).Parent).ColumnDefinitions;
        cols[0].Width = cols[2].Width = new GridLength(1, GridUnitType.Star);
    }

    RecycleBinWindow recycleBin;
    void OpenRecycleBin()
    {
        if (recycleBin != null) { recycleBin.Activate(); return; }
        recycleBin = new RecycleBinWindow(this, () => { Left.Refresh(); Right.Refresh(); });
        recycleBin.Closed += (_, _) => recycleBin = null;
        recycleBin.Show();
    }

    Button SideButton(string icon, string text, string path, Action click = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(ShellIcon(path, icon, 16, new(0, 0, 10, 0)));
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var b = new Button { Content = grid, Style = (Style)FindResource("Side"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 1, 0, 1), ToolTip = click == null ? path : text };
        if (click != null) { b.Click += (_, _) => click(); return b; }
        b.Click += (_, _) => { active.Navigate(path); active.FocusList(); };
        b.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { active.OpenInBackgroundTab(path); e.Handled = true; } };
        return b;
    }

    // Drive rows are rebuilt from figures read off the UI thread: asking a network drive for its size
    // blocks for the best part of a second, and the numbers have to keep up with copies and deletes.
    record DriveRow(string Name, string Label, string Format, long Total, long Free, bool Network, string VolumeLabel, DriveType Type);

    static List<DriveRow> ReadDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
            try
            {
                if (!d.IsReady) continue;
                bool net = d.DriveType == DriveType.Network;
                var label = net && GetNetworkPath(d.Name) is { } unc ? $"({unc})" : d.VolumeLabel;
                rows.Add(new DriveRow(d.Name, label, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, net, d.VolumeLabel, d.DriveType));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // drive went away mid-read
        return rows;
    }

    List<DriveRow> shownDrives = new();

    void RefreshDrives() => Task.Run(ReadDrives).ContinueWith(t =>
    {
        var rows = t.Result;
        if (rows.SequenceEqual(shownDrives)) return; // nothing moved: leave the buttons alone
        bool plugged = !rows.Select(d => (d.Name, d.VolumeLabel)).SequenceEqual(shownDrives.Select(d => (d.Name, d.VolumeLabel)));
        shownDrives = rows;
        Drives.Children.Clear();
        foreach (var d in rows) Drives.Children.Add(DriveButton(d));
        if (plugged) ShowDriveNodes(); // only when drives came or went: rebuilding closes opened branches
        UpdateStats();
    }, TaskScheduler.FromCurrentSynchronizationContext());

    // OneCommander-style drive row: "C:  Label        used / total GB" with a usage bar under it.
    Button DriveButton(DriveRow d)
    {
        double used = d.Total - d.Free, frac = used / d.Total;
        var label = d.Label;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        var icon = ShellIcon(d.Name, d.Network ? "\uE968" : "\uEDA2", 16, new(0, 0, 8, 0));
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new System.Windows.Documents.Run(d.Name.TrimEnd((char)92) + "  ") { FontWeight = FontWeights.SemiBold });
        name.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = (Brush)FindResource("Muted") });
        var size = new TextBlock { Margin = new(6, 0, 0, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        size.Inlines.Add(new System.Windows.Documents.Run($"{used / 1e9:N0}"));
        size.Inlines.Add(new System.Windows.Documents.Run($" / {d.Total / 1e9:N0} GB") { Foreground = (Brush)FindResource("Muted") });
        var bar = new Grid { Height = 2, Margin = new(0, 4, 0, 1) };
        bar.Children.Add(new Border { Background = Hex("#1AFFFFFF") });
        var fill = new Border { Background = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Left };
        if (frac > 0.9) // almost full: a bright red that glows a little, so it reads as a warning at a glance
        {
            fill.Background = Hex("#FF3347");
            fill.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xFF, 0x33, 0x47), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.75 };
        }
        bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * frac;
        bar.Children.Add(fill);
        Grid.SetColumn(name, 1); Grid.SetColumn(size, 2);
        Grid.SetRow(bar, 1); Grid.SetColumn(bar, 1); Grid.SetColumnSpan(bar, 2);
        g.Children.Add(icon); g.Children.Add(name); g.Children.Add(size); g.Children.Add(bar);
        var b = new Button { Content = g, Style = (Style)FindResource("Side"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 1, 0, 1), ToolTip = $"{d.Name}  {d.Format}  ·  {Fmt(d.Free)} free" };
        b.Click += (_, _) => { active.Navigate(d.Name); active.FocusList(); };
        b.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { active.OpenInBackgroundTab(d.Name); e.Handled = true; } };
        // Windows' own menu for the drive (Properties, Eject, Format, BitLocker…), hosted as the folders' Windows menu is
        b.MouseRightButtonUp += (_, e) => { e.Handled = true; ShellMenu.Show(this, d.Name, new[] { d.Name }, rename: false); RefreshDrives(); };
        return b;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string local, System.Text.StringBuilder remote, ref int len);
    static string GetNetworkPath(string root)
    {
        var sb = new System.Text.StringBuilder(512); int len = sb.Capacity;
        return WNetGetConnection(root.TrimEnd((char)92), sb, ref len) == 0 ? sb.ToString() : null;
    }

    // Quick access: pinned folders, one path per line in quickaccess.txt in the data folder. Since 1.0.51 there can be several
    // lists, OneCommander style, each with a name the user picks: "# name" starts one. A file without such a line (every one
    // before 1.0.51) is one list, "Quick access", and is written back the same way while that is the only list.
    static string QuickFile => Path.Combine(Settings.Dir, "quickaccess.txt");
    internal sealed class QuickList { public string Name; public List<string> Folders = new(); }
    internal const string FirstQuickList = "Quick access";
    List<QuickList> quickLists = new() { new() { Name = FirstQuickList } };
    QuickList CurrentQuickList => quickLists[Math.Clamp(Settings.QuickList, 0, quickLists.Count - 1)];
    List<string> pinned => CurrentQuickList.Folders; // the list the sidebar shows; Ctrl+D pins to it
    string pinDrag;
    Point pinDragFrom;

    internal static List<QuickList> ParseQuickLists(IEnumerable<string> lines)
    {
        var lists = new List<QuickList>();
        foreach (var line in lines.Where(l => l.Trim() != ""))
            if (line.StartsWith("# ") && line[2..].Trim() is { Length: > 0 } name) lists.Add(new QuickList { Name = name });
            else
            {
                if (lists.Count == 0) lists.Add(new QuickList { Name = FirstQuickList });
                lists[^1].Folders.Add(line);
            }
        if (lists.Count == 0) lists.Add(new QuickList { Name = FirstQuickList });
        return lists;
    }

    internal static IEnumerable<string> QuickListLines(List<QuickList> lists) =>
        lists is [{ Name: FirstQuickList } only] ? only.Folders : lists.SelectMany(q => q.Folders.Prepend("# " + q.Name));

    void LoadQuickAccess()
    {
        if (File.Exists(QuickFile)) quickLists = ParseQuickLists(File.ReadAllLines(QuickFile));
        if (File.Exists(LumeFile)) lumePins = File.ReadAllLines(LumeFile).Where(l => l.Trim() != "").ToList();
        BuildQuickAccess();
        ShareQuickAccess();
    }

    // Lume (sister app) lists these folders, from every Quick access list, and what was pinned to it, in its search; copies,
    // since the write runs off the UI thread
    void ShareQuickAccess()
    {
        var folders = quickLists.SelectMany(q => q.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lume = lumePins.ToList();
        Task.Run(() => Sisters.WriteItems(folders, lume));
    }

    // Pin to Lume (1.0.50): files, folders and shortcuts that Lume then finds by name, one path per line in lume.txt in
    // the data folder. They reach Lume with Quick access, in data\filelabs.items.json (Sisters.WriteItems).
    static string LumeFile => Path.Combine(Settings.Dir, "lume.txt");
    List<string> lumePins = new();

    /// "Pin to Lume", or "Unpin from Lume" when all of them are pinned; null without Lume.
    MenuItem LumeItem(IReadOnlyList<string> paths)
    {
        if (SisterExe("lume") == null) return null;
        bool all = paths.All(x => lumePins.Contains(x, StringComparer.OrdinalIgnoreCase));
        return Item(all ? "Unpin from Lume" : "Pin to Lume", all ? "" : "", "", () =>
        {
            if (all) lumePins.RemoveAll(x => paths.Contains(x, StringComparer.OrdinalIgnoreCase));
            else lumePins.AddRange(paths.Where(x => !lumePins.Contains(x, StringComparer.OrdinalIgnoreCase)));
            Directory.CreateDirectory(Path.GetDirectoryName(LumeFile));
            Settings.WriteAllLines(LumeFile, lumePins);
            ShareQuickAccess();
            var names = string.Join(", ", paths.Select(x => Path.GetFileName(x.TrimEnd('\\')) is { Length: > 0 } n ? n : x));
            Status.Text = all ? $"Unpinned {names} from Lume" : $"Pinned {names} to Lume";
        });
    }

    void BuildQuickAccess()
    {
        QuickAccess.Children.Clear();
        foreach (var path in pinned)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            var b = SideButton(PaneView.IsRemote(path) || Directory.Exists(path) ? "\uE718" : "\uE7BA", name == "" ? path : name, path); // pin / warning if gone
            void Move(int by) { int i = pinned.IndexOf(path); if (i + by >= 0 && i + by < pinned.Count) { pinned.RemoveAt(i); pinned.Insert(i + by, path); SaveQuickAccess(); } }
            (string, Action)[] own = { ("Move &up", () => Move(-1)), ("Move &down", () => Move(1)), ("&Remove from Quick access", () => { pinned.Remove(path); SaveQuickAccess(); }) };
            var menu = new ContextMenu();
            foreach (var (text, run) in own) menu.Items.Add(Item(text.Replace("&", "_"), "", "", run));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Properties", "", "", () => Properties(path)));
            b.ContextMenu = menu;
            // Settings → Right-click menu → Windows: Windows' menu for the folder, with these three on top (Rico, 1.0.53)
            b.PreviewMouseRightButtonUp += (_, e) =>
            {
                if (!Settings.NativeMenu || !(PaneView.IsRemote(path) || Directory.Exists(path))) return;
                e.Handled = true;
                ShellMenu.Show(this, Path.GetDirectoryName(path.TrimEnd('\\')) ?? path, new[] { path }, rename: false, top: own);
            };
            // drag to reorder; the sidebar's drop handler places it (SidebarSections.cs)
            b.PreviewMouseLeftButtonDown += (_, e) => { pinDrag = path; pinDragFrom = e.GetPosition(QuickAccess); };
            b.PreviewMouseMove += (_, e) =>
            {
                if (pinDrag != path || e.LeftButton != MouseButtonState.Pressed) return;
                if (Math.Abs((e.GetPosition(QuickAccess) - pinDragFrom).Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pinDrag = null;
                DragDrop.DoDragDrop(b, new DataObject("FileLabsPin", path), DragDropEffects.Move);
            };
            QuickAccess.Children.Add(b);
        }
        QuickHint.Visibility = pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveQuickAccess()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QuickFile));
        Settings.WriteAllLines(QuickFile, QuickListLines(quickLists));
        BuildQuickAccess();
        ShareQuickAccess();
    }

    // ---- the lists themselves: from the button and the right-click on Quick access' header (SidebarSections.cs) ----
    ContextMenu QuickListsMenu()
    {
        var m = new ContextMenu();
        for (int i = 0; i < quickLists.Count; i++)
        {
            int index = i;
            bool shown = quickLists[i] == CurrentQuickList;
            var list = new MenuItem { Header = new TextBlock { Text = quickLists[i].Name }, IsChecked = shown, FontWeight = shown ? FontWeights.SemiBold : FontWeights.Normal, InputGestureText = $"{quickLists[i].Folders.Count}" };
            list.Click += (_, _) => SwitchQuickList(index);
            m.Items.Add(list);
        }
        m.Items.Add(new Separator());
        var current = CurrentQuickList;
        int at = quickLists.IndexOf(current);
        m.Items.Add(Item("New list…", "\uE710", "", () =>
        {
            if (PromptDialog.Ask(this, "New Quick access list", "Name of the list", "New list", "Create") is not { } name) return;
            quickLists.Insert(at + 1, new QuickList { Name = name });
            SwitchQuickList(at + 1);
        }));
        m.Items.Add(Item($"Rename “{current.Name}”…", "\uE8AC", "", () =>
        {
            if (PromptDialog.Ask(this, "Rename list", "Name of the list", current.Name, "Rename", selectAll: true) is not { } name) return;
            current.Name = name;
            SwitchQuickList(at);
        }));
        m.Items.Add(Item("Move list up", "\uE74A", "", () => MoveQuickList(at, at - 1), at > 0));
        m.Items.Add(Item("Move list down", "\uE74B", "", () => MoveQuickList(at, at + 1), at < quickLists.Count - 1));
        m.Items.Add(Item($"Delete “{current.Name}”…", "\uE74D", "", () =>
        {
            if (current.Folders.Count > 0 && PromptDialog.Choose(this, $"Delete the list “{current.Name}”?",
                    $"Its {current.Folders.Count} folder{(current.Folders.Count == 1 ? "" : "s")} leave Quick access; the folders themselves stay where they are.", "Delete", "Cancel") != 0) return;
            quickLists.Remove(current);
            SwitchQuickList(Math.Min(at, quickLists.Count - 1));
        }, quickLists.Count > 1));
        return m;
    }

    void SwitchQuickList(int index)
    {
        Settings.QuickList = index;
        Settings.Save();
        SaveQuickAccess();
        BuildSidebar(); // the section's title is the list's name
    }

    void MoveQuickList(int from, int to)
    {
        var list = quickLists[from];
        quickLists.RemoveAt(from);
        quickLists.Insert(to, list);
        SwitchQuickList(to);
    }

    // Pins the selected folders, or the current folder if no folder is selected.
    void PinSelected()
    {
        var dirs = active.Selected.Where(e => e.IsDir).Select(e => e.Path).DefaultIfEmpty(active.Dir);
        var added = dirs.Where(d => d != PaneView.ThisPC && !pinned.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList(); // This PC is in Favorites
        if (added.Count == 0) { Status.Text = $"Already in {CurrentQuickList.Name}"; return; }
        pinned.AddRange(added);
        SaveQuickAccess();
        Status.Text = $"Pinned {string.Join(", ", added.Select(Path.GetFileName))} to {CurrentQuickList.Name}";
    }

    // Windows' own icon for a path (folder, drive, network drive, special folder), cached per path;
    // the Fluent glyph only if the shell has nothing.
    static readonly Dictionary<string, ImageSource> shellIcons = new(StringComparer.OrdinalIgnoreCase);
    public static FrameworkElement ShellIcon(string path, string fallbackGlyph, double size, Thickness margin)
    {
        var box = new Grid { Width = size, Height = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = fallbackGlyph, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = Hex("#F5C451"), FontSize = size * 0.9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(glyph);

        void Show(ImageSource img)
        {
            if (img == null) return;
            var image = new Image { Source = img, Width = size, Height = size, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            box.Children.Add(image);
            glyph.Visibility = Visibility.Collapsed;
        }

        if (shellIcons.TryGetValue(path, out var cached)) { Show(cached); return box; }

        // Asking the shell blocks — on a network path for the best part of a second — so it happens
        // off the UI thread and the glyph stands in until the icon arrives.
        var dispatcher = box.Dispatcher;
        IconQueue.Add(() =>
        {
            var img = Thumbnails.Get(path, 32, iconOnly: true);
            dispatcher.BeginInvoke(() => { shellIcons[path] = img; Show(img); });
        });
        return box;
    }

    // One STA thread for every sidebar and tab icon. A thread each cost the UI thread 259 ms of Thread.Start across
    // ~13 icons at startup on a throttled CPU (1.0.44).
    // ponytail: one thread, so a slow network drive's icon holds up the icons queued after it; a small pool if that shows
    static readonly System.Collections.Concurrent.BlockingCollection<Action> IconQueue = StartIconThread();
    static System.Collections.Concurrent.BlockingCollection<Action> StartIconThread()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); }) { IsBackground = true, Name = "Shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return queue;
    }

    // Parsed once per colour and frozen: rows ask for their icon and age colours as they are built, and a fresh
    // parse per row was part of drawing the first lists (1.0.44). Frozen brushes are also cheaper to draw.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> hexBrushes = new();
    public static Brush Hex(string c) => hexBrushes.GetOrAdd(c, k => { var b = (Brush)new BrushConverter().ConvertFrom(k); b.Freeze(); return b; });

    public static string Fmt(long b)
    {
        string[] u = { "bytes", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} bytes" : $"{v:0.#} {u[i]}";
    }

    void SetActive(PaneView p)
    {
        active = p;
        Left.SetActive(p == Left);
        Right.SetActive(p == Right);
        UpdateStats();
        PreviewFollow();
    }

    string publishedFolder, shownDir;
    readonly System.Windows.Threading.DispatcherTimer driveTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    void UpdateStats()
    {
        if (active?.Dir is not { Length: > 0 } dir) return;
        // Only when the folder actually changed: the 10 s refresh would otherwise wipe whatever message
        // the status bar is showing ("Path copied", "Pinned …").
        if (dir != shownDir) { shownDir = dir; Status.Text = dir == PaneView.ThisPC ? "This PC" : dir; }
        bool bareServer = dir.StartsWith(@"\\") && !dir.Trim('\\').Contains('\\') || PaneView.IsVirtual(dir); // not a folder a dialog can open
        if (dir != publishedFolder && !bareServer && Settings.Profile == null && Settings.Portable == null) // a test or portable copy leaves the installed one's folder alone
        {
            // for Ctrl+G in Open/Save dialogs (Agent.cs)
            publishedFolder = dir;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Agent.StateKey);
            key.SetValue("CurrentFolder", dir);
        }
        ShowFolderStats();
        ShowSelectionStats();
        if (bin.Count > 0) ShowBinTarget();

        // The figures come from the drive rows, read off the UI thread every 10 s and after every copy or
        // delete: asking a network drive here cost up to 177 ms, once per folder whose size came in.
        var root = dir.StartsWith(@"\\") ? null : Path.GetPathRoot(dir);
        if (shownDrives.FirstOrDefault(r => r.Name.Equals(root, StringComparison.OrdinalIgnoreCase)) is not { } d)
        {
            DriveCard.Visibility = Visibility.Hidden; // a share has no drive figures
            return;
        }
        DriveCard.Visibility = Visibility.Visible;
        double used = d.Total > 0 ? (double)(d.Total - d.Free) / d.Total : 0;
        StatDrive.Text = d.Name.TrimEnd('\\') + (d.VolumeLabel != "" ? "  " + d.VolumeLabel : "");
        StatFs.Text = $"  ·  {d.Format}{(d.Network ? " · network" : "")}";
        StatFree.Text = Fmt(d.Free);
        StatUsed.Text = $"   ·   {Fmt(d.Total - d.Free)} of {Fmt(d.Total)} used";
        DrivePct.Text = $"{used * 100:0}% full";
        DriveBar.ColumnDefinitions[0].Width = new GridLength(used, GridUnitType.Star);
        DriveBar.ColumnDefinitions[1].Width = new GridLength(1 - used, GridUnitType.Star);
        bool full = used > 0.9; // the same warning as the sidebar's drive bars
        var colour = full ? Hex("#FF3347") : (Brush)FindResource("Accent");
        DriveFill.Background = DriveIcon.Foreground = colour;
        DrivePct.Foreground = full ? colour : (Brush)FindResource("Muted");
        DriveFill.Effect = full ? FullGlow : null;
    }

    readonly System.Windows.Media.Effects.DropShadowEffect FullGlow = new() { Color = Color.FromRgb(0xFF, 0x33, 0x47), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.75 };

    // The folder: its size, what it holds, and a bar of what that is by kind — the same colours as the
    // file icons in the list. Rebuilt only when the mix changes, not at every selection change.
    static readonly Dictionary<string, string> KindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#60A5FA"] = "Folders", ["#F472B6"] = "Images", ["#FB923C"] = "Video", ["#A78BFA"] = "Audio", ["#FACC15"] = "Archives",
        ["#34D399"] = "Programs", ["#38BDF8"] = "Code", ["#F87171"] = "PDF", ["#9CA3AF"] = "Other",
    };
    string kindsShown;

    void ShowFolderStats()
    {
        var all = active.Items.ToList();
        int folders = all.Count(e => e.IsDir);
        StatFolder.Text = Fmt(active.FolderTotal);
        StatFolderSub.Text = $"{folders} folder{(folders == 1 ? "" : "s")} · {all.Count - folders} file{(all.Count - folders == 1 ? "" : "s")}"
                             + (active.SizesPending ? " · measuring…" : "");
        var kinds = all.Where(e => e.Size > 0).GroupBy(e => e.KindColor).Select(g => (Color: g.Key, Bytes: g.Sum(e => e.Size)))
                       .OrderByDescending(k => k.Bytes).ToList();
        long total = kinds.Sum(k => k.Bytes);
        var signature = string.Join("|", kinds.Select(k => $"{k.Color}:{k.Bytes * 200 / Math.Max(1, total)}"));
        if (signature == kindsShown) return;
        kindsShown = signature;
        KindBar.Children.Clear();
        KindBar.ColumnDefinitions.Clear();
        foreach (var (color, bytes) in kinds.Where(k => k.Bytes * 100 >= total)) // slivers under 1% would be noise
        {
            KindBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var part = new Border { Background = Hex(color), Margin = new(0, 0, 1, 0) };
            Grid.SetColumn(part, KindBar.ColumnDefinitions.Count - 1);
            KindBar.Children.Add(part);
        }
        KindBar.ToolTip = total == 0 ? null : string.Join("\n", kinds.Select(k =>
            $"{(KindNames.TryGetValue(k.Color, out var n) ? n : "Other")}   {100.0 * k.Bytes / total:0}%   {Fmt(k.Bytes)}"));
    }

    // The selection when there is one; otherwise the most recent change in the folder — what you are
    // usually looking for in Downloads or a project folder.
    void ShowSelectionStats()
    {
        var sel = active.Selected;
        if (sel.Count > 0)
        {
            long bytes = sel.Sum(e => Math.Max(0, e.Size)), folder = active.FolderTotal;
            SelIcon.Text = "\uE8B3";
            SelTitle.Text = sel.Count == 1 ? "Selected" : $"{sel.Count} selected";
            SelValue.Text = sel.Any(e => e.Size == -1) ? (sel.Count == 1 ? "measuring…" : Fmt(bytes) + "+") : Fmt(bytes); // a folder still being measured
            SelSub.Text = sel.Count == 1 ? sel[0].Name : folder > 0 ? $"{100.0 * bytes / folder:0.#}% of this folder" : "";
            return;
        }
        var newest = active.Items.Where(e => e.Modified != default).MaxBy(e => e.Modified);
        SelIcon.Text = "\uE823"; // clock
        SelTitle.Text = "Latest change";
        SelValue.Text = newest == null ? "—" : Ago(newest.Modified);
        SelSub.Text = newest?.Name ?? "";
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 2 ? "yesterday"
            : d.TotalDays < 60 ? $"{(int)d.TotalDays} days ago"
            : t.ToString("d MMM yyyy");
    }

    // Copy/move through the Ferry engine; delete still goes to the Recycle Bin via the VB API.
    readonly System.Collections.ObjectModel.ObservableCollection<Job> jobs = new();

    void Transfer(bool move)
    {
        if (active.ArchiveRoot != null) { if (!move) ExtractFromArchive(active, Other.Dir); return; } // Ctrl+F5 in an archive extracts to the other pane
        Submit(active.Selected.Select(e => e.Path).ToList(), Other.Dir, move);
    }

    /// `ask`: a move that gathers items from several folders into one (search results dropped on the search's own
    /// folder, say) is confirmed first, naming where it all goes. Off where gathering is the point (Drop bin, Undo).
    /// `names`: the exact name each source gets there (Undo, putting back names changed by Keep both). `journal`: Ferry
    /// writes down each arrival as it happens, for Undo after a crash (FileOps.ReadJournal); off for Undo's own moves.
    internal Job Submit(List<string> sources, string dest, bool move, bool ask = true, IReadOnlyDictionary<string, string> names = null, bool journal = true)
    {
        if (sources.Count == 0 || PaneView.IsVirtual(dest)) return null; // nothing goes "into" This PC, nor into an archive this way (AddToArchive)
        if (move && ask && sources.Select(s => Path.GetDirectoryName(s.TrimEnd('\\'))).Distinct(StringComparer.OrdinalIgnoreCase).Count() is var folders and > 1
            && PromptDialog.Choose(this, $"Move {sources.Count:N0} items from {folders:N0} folders into one folder?", "All of them go into " + dest, "Move", "Cancel") != 0)
            return null;
        Settings.Bump(Settings.RecentDestinations, dest, Settings.DestinationsMax); // for Copy to / Move to
        var job = Ferry.Submit(sources, dest, move, names, journal ? Path.Combine(JournalDir, $"{Guid.NewGuid():N}.txt") : null);
        job.Seq = ++jobSeq;
        job.Hue = Hex(JobHues[(jobSeq - 1) % JobHues.Length]);
        jobs.Insert(0, job);
        WatchJobs();
        ShowTransfers(activate: false); // the keyboard stays in the list
        return job;
    }

    // ---- the Transfers window: every copy and move, apart from the panes (under them it took the files' room) ----
    // On top the total speed and a chart as wide as the window, each running job a band in its own colour; under it the
    // jobs in three sections, what needs you first, then what runs, then what is done (each card edged in its colour).
    Window transfersWindow;
    readonly HashSet<Job> askedForward = new(); // a job's conflicts bring the window forward once
    TotalChart totalChart;
    TextBlock totalSpeed, totalLegend;
    FrameworkElement transfersTop;
    System.Windows.Data.ListCollectionView jobView;
    readonly Dictionary<Job, int> shownState = new();
    int jobSeq;
    static readonly string[] JobHues = { "#4C8DFF", "#5DCAA5", "#F0997B", "#ED93B1", "#AFA9EC", "#FAC775" };

    void ShowTransfers(bool activate)
    {
        if (transfersWindow == null)
        {
            jobView = new System.Windows.Data.ListCollectionView(jobs);
            jobView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Job.StateGroup)));
            jobView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(Job.StateOrder), System.ComponentModel.ListSortDirection.Ascending));
            jobView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(Job.Seq), System.ComponentModel.ListSortDirection.Descending));
            var list = new ItemsControl { ItemTemplate = (DataTemplate)FindResource("JobCard"), ItemsSource = jobView };
            var header = new FrameworkElementFactory(typeof(TextBlock));
            header.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            header.SetValue(TextBlock.ForegroundProperty, FindResource("Muted"));
            header.SetValue(MarginProperty, new Thickness(2, 6, 0, 6));
            list.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });

            totalSpeed = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 12, 0) };
            totalLegend = new TextBlock { Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis };
            var top = new DockPanel { Margin = new(14, 12, 14, 0) };
            DockPanel.SetDock(totalSpeed, Dock.Left);
            top.Children.Add(totalSpeed); top.Children.Add(totalLegend);
            totalChart = new TotalChart { Height = 110, Margin = new(6, 6, 14, 4) }; // as wide as the window, whatever its size

            var root = new DockPanel { Background = Tint() };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(totalChart, Dock.Top);
            root.Children.Add(top); root.Children.Add(totalChart);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Padding = new(14, 6, 14, 8) });
            transfersTop = top;
            var w = transfersWindow = new Window
            {
                // as tall as its cards: one copy, one card's height; it grows as more come, up to most of the screen, then scrolls
                // (Rico and the user, 1.0.61: a big empty window around one card looked wrong). Resizing it by hand keeps that size.
                Title = "Transfers — File Labs", Owner = this, Width = 640, SizeToContent = SizeToContent.Height, MinWidth = 460, MinHeight = 120,
                MaxHeight = SystemParameters.WorkArea.Height * 0.8,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = true, Background = Brushes.Transparent,
                Foreground = Hex("#E8EEF6"), FontFamily = FontFamily, FontSize = 12, Content = root,
            };
            w.SourceInitialized += (_, _) => ApplyAcrylic(w);
            // closing only hides it: the jobs go on, and the Transfers button or the next copy brings it back
            w.Closing += (_, e) => { if (!quitting && IsLoaded) { e.Cancel = true; w.Hide(); } };
            w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { w.Hide(); e.Handled = true; } };
            jobs.CollectionChanged += (_, _) =>
            {
                TransfersButton.Visibility = jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                TransfersText.Text = jobs.Count > 1 ? $"Transfers ({jobs.Count})" : "Transfers";
                if (jobs.Count == 0) { w.Hide(); totalChart.Clear(); } // the last one dismissed
            };
            TransfersButton.Visibility = Visibility.Visible;
            UpdateTransfers();
        }
        if (transfersWindow.WindowState == WindowState.Minimized) transfersWindow.WindowState = WindowState.Normal;
        if (!transfersWindow.IsVisible) { transfersWindow.ShowActivated = activate; transfersWindow.Show(); }
        else if (activate) transfersWindow.Activate();
    }

    /// Ten times a second: the total and its chart, and each job moved to its section when its state changed.
    void UpdateTransfers()
    {
        if (transfersWindow == null) return;
        var running = jobs.Where(j => j.Copying).ToList();
        if (running.Count > 0) totalChart.Add(Environment.TickCount64, running);
        double total = running.Sum(j => j.Rate);
        // the speed and its chart while something runs (and the chart once it has something to draw); after that just the cards
        bool busy = jobs.Any(j => j.Active);
        transfersTop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        totalChart.Visibility = busy && totalChart.HasData ? Visibility.Visible : Visibility.Collapsed;
        totalSpeed.Text = running.Count > 0 ? $"{Fmt((long)total)}/s" : jobs.Any(j => j.HasConflicts) ? "Waiting for you" : jobs.Any(j => j.Active) ? "Waiting" : "Done";
        totalLegend.Inlines.Clear();
        foreach (var j in running)
        {
            if (totalLegend.Inlines.Count > 0) totalLegend.Inlines.Add(new System.Windows.Documents.Run("   "));
            totalLegend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = j.Hue });
            totalLegend.Inlines.Add(new System.Windows.Documents.Run($"{j.Where} {Fmt((long)j.Rate)}/s"));
        }
        bool moved = false;
        foreach (var j in jobs) if (!shownState.TryGetValue(j, out var was) || was != j.StateOrder) { shownState[j] = j.StateOrder; moved = true; }
        if (moved) jobView.Refresh();
    }

    void Transfers_Click(object s, RoutedEventArgs e) => ShowTransfers(activate: true);

    // The bars move every frame, the figures and the chart ten times a second. A 100 ms timer moved the bars in
    // visible jumps (9 steps a second, up to 390 ms apart while the UI thread was busy).
    bool watchingJobs;
    TimeSpan jobsRefreshed;
    void WatchJobs()
    {
        if (watchingJobs) return;
        watchingJobs = true;
        jobsRefreshed = TimeSpan.Zero;
        CompositionTarget.Rendering += JobFrame;
    }

    void JobFrame(object s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        bool full = now - jobsRefreshed >= TimeSpan.FromMilliseconds(100);
        if (full) jobsRefreshed = now;
        foreach (var j in jobs) if (full) j.Refresh(); else if (j.Active) j.RefreshBars();
        // a question waits (files already there): the window comes forward with it, once per job
        if (full) foreach (var j in jobs) if (j.HasConflicts && askedForward.Add(j)) ShowTransfers(activate: true);
        if (full) UpdateTransfers();
        if (full && !jobs.Any(j => j.Active)) { CompositionTarget.Rendering -= JobFrame; watchingJobs = false; }
    }

    void JobDone(Job job) => Dispatcher.BeginInvoke(() =>
    {
        job.Refresh();
        UpdateTransfers(); // into Done
        RememberJob(job);
        // what was pasted or dropped is selected where it arrived, the first of it in sight with the keyboard on it,
        // instead of somewhere among the folder's other hundred files (Rico, 1.0.61)
        if (job.Phase == Phase.Done)
        {
            var arrived = job.Sources.Select(s => Path.GetFileName(job.Duplicates.GetValueOrDefault(s) ?? s.TrimEnd('\\'))).Where(n => n != "").ToList();
            foreach (var pane in new[] { Left, Right })
                if (pane.Dir.TrimEnd('\\').Equals(job.Destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) pane.SelectAfterRefresh(arrived, focus: pane == active);
        }
        Left.Refresh(); Right.Refresh();
        RefreshDrives(); UpdateStats(); // a finished copy or move changes what's free
    });

    void JobCard_Loaded(object s, RoutedEventArgs e) => Motion.Enter((UIElement)s, rise: 10); // a new transfer slides up into place

    static Job JobOf(object sender) => (Job)((FrameworkElement)sender).DataContext;
    void JobPause_Click(object s, RoutedEventArgs e) { var j = JobOf(s); if (j.Paused) j.Resume(); else j.Pause(); }
    void JobCancel_Click(object s, RoutedEventArgs e) => JobOf(s).Cancel();
    void JobDismiss_Click(object s, RoutedEventArgs e) { var j = JobOf(s); jobs.Remove(j); askedForward.Remove(j); shownState.Remove(j); }
    void JobResolve_Click(object s, RoutedEventArgs e) => JobOf(s).Resolve(Enum.Parse<Choice>((string)((Button)s).CommandParameter));

    // Clipboard in Explorer's own format (CF_HDROP + Preferred DropEffect), so copy/paste works both ways.
    async void ClipboardPut(bool cut)
    {
        // inside an archive, Ctrl+C copies what is selected, taken out to a temp folder off the UI thread; never Ctrl+X
        if (active.ArchiveRoot != null)
        {
            if (cut) return;
            Status.Text = "Taking them out of the archive…";
            if (await active.ExtractForClipboard() is not { Length: > 0 } copies) { Status.Text = ""; return; }
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(copies);
            Clipboard.SetFileDropList(list);
            Status.Text = $"Copied {copies.Length} item(s) out of the archive";
            return;
        }
        var sel = active.Selected;
        if (sel.Count == 0) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(sel.Select(x => x.Path).ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
        // FilePilot/Explorer-style: a cut item's icon stays dimmed until it's pasted or something else is cut/copied
        PaneView.SetCut(cut ? sel.Select(x => x.Path) : Array.Empty<string>(), Left, Right);
        Status.Text = $"{(cut ? "Cut" : "Copied")} {sel.Count} item(s)";
    }

    /// Into the folder shown, or into `folder` (right-click → Paste on a folder or a shortcut to one, as in Explorer).
    void ClipboardPaste(string folder = null)
    {
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
        var into = folder ?? active.Dir;
        if (PaneView.ArchiveOf(into) != null) { AddToArchive(active, files, into); return; } // a paste into a ZIP adds to it
        Submit(files, into, move);
        if (move) { Clipboard.Clear(); PaneView.SetCut(Array.Empty<string>(), Left, Right); } // a cut is pasted once, like Explorer
    }

    // Shift+Delete skips the Recycle Bin. Always asked first: the shell only confirms when the user has
    // switched its delete confirmation on (off by default), and this one can't be taken back.
    void Delete(bool permanent = false)
    {
        if (active.ArchiveRoot != null) { DeleteFromArchive(active); return; } // out of the ZIP: asked there, never to the Recycle Bin
        var sel = active.Selected;
        if (sel.Count == 0) return;
        var question = $"Permanently delete {(sel.Count == 1 ? $"“{sel[0].Name}”" : $"these {sel.Count} items")}?";
        const string detail = "They will not go to the Recycle Bin.";
        // Shift+Delete, Enter deletes, as in Explorer: Yes is the default button, and the shell is not
        // asked to confirm a second time what was just confirmed here. With the sound off (Settings →
        // Files) the question is File Labs' own dialog, which is silent, not a box that plays a warning.
        if (permanent && !(Settings.DeleteSound
                ? MessageBox.Show(this, question + "\n\n" + detail, "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes
                : PromptDialog.Choose(this, question, detail, "Delete", "Cancel") == 0)) return;
        if (permanent) Recycle(sel.Select(e => e.Path).ToList(), UIOption.AllDialogs, permanent: true);
        else RecycleUndoably(sel.Select(e => e.Path).ToList(), UIOption.AllDialogs);
    }

    /// To the Recycle Bin, and what went there is one Undo entry: Ctrl+Z brings that batch back. Right after the delete,
    /// off the UI thread, each path is matched to the item it became in the bin (its $R file), so Undo restores exactly
    /// that one and never a later deletion from the same place. An item Windows deleted for good (too big for the bin) has none.
    internal void RecycleUndoably(List<string> paths, UIOption ui, Action done = null)
    {
        var when = DateTime.Now.AddSeconds(-2); // the $I record's clock and ours may differ a little
        Recycle(paths, ui, done: async () =>
        {
            var found = await Task.Run(() => RecycleBinWindow.Find(paths.Where(p => !Exists(p)), when)); // all, or as many as went before a Cancel
            if (found.Count > 0)
                Remember($"delete of {(found.Count == 1 ? Path.GetFileName(found.Keys.First().TrimEnd('\\')) : $"{found.Count:N0} items")}",
                         found.Select(f => new UndoStep("restore", f.Key, f.Value)), when);
            done?.Invoke();
        });
    }

    /// A permanent delete without the shell, which plays Windows' "Empty Recycle Bin" sound for one.
    /// Walked by hand: read-only items are cleared first (the shell deletes them, .NET refuses), and a
    /// junction is removed, never followed — .NET's own recursive delete fails on one without admin rights.
    internal static void DeleteQuietly(string path, Action<string> deleting = null)
    {
        deleting?.Invoke(path);
        var attributes = File.GetAttributes(path); // a folder's too
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        if (!attributes.HasFlag(FileAttributes.Directory)) { File.Delete(path); return; }
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            foreach (var child in Directory.GetFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                DeleteQuietly(child, deleting);
        Directory.Delete(path);
    }

    // Shift+Delete with the sound off: deleted by hand as above, in Windows' own progress window (silent,
    // with the item being deleted and Cancel), as the shell shows for every other delete.
    static void DeleteWithProgress(IntPtr owner, List<string> paths)
    {
        var dialog = (IProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F8383852-FCD3-11d1-A6B9-006097DF5BD4")));
        dialog.SetTitle("Deleting");
        dialog.StartProgressDialog(owner, null, PROGDLG_NOTIME | PROGDLG_MARQUEEPROGRESS, IntPtr.Zero);
        int done = 0;
        long shown = 0;
        try
        {
            foreach (var p in paths)
                DeleteQuietly(p, item =>
                {
                    done++;
                    if (Environment.TickCount64 - shown < 100) return; // each call waits on the dialog's thread: ten a second
                    shown = Environment.TickCount64;
                    if (dialog.HasUserCancelled()) throw new OperationCanceledException();
                    dialog.SetLine(1, item, true, IntPtr.Zero);
                    dialog.SetLine(2, $"{done:N0} items deleted", false, IntPtr.Zero);
                });
        }
        finally { dialog.StopProgressDialog(); Marshal.ReleaseComObject(dialog); }
    }

    // Shift+Delete with the sound on: the shell's delete with its progress window and error prompts, but not
    // its own confirmation (FOF_NOCONFIRMATION) — File Labs has just asked. Without FOF_ALLOWUNDO it's permanent.
    // To the Recycle Bin too, with FOF_ALLOWUNDO: the whole list in one operation, so 10,000 files are one
    // progress window and one question, not 10,000 of each. FOF_WANTNUKEWARNING: an item too big for the bin
    // is asked about even when the rest goes quietly, never deleted for good without a word.
    static string ShellDelete(IntPtr owner, List<string> paths, ushort flags)
    {
        var op = new SHFILEOPSTRUCT { hwnd = owner, wFunc = FO_DELETE, pFrom = string.Join("\0", paths) + "\0", fFlags = flags };
        int r = SHFileOperation(ref op);
        return r is 0 or ERROR_CANCELLED || op.fAnyOperationsAborted ? null : $"Not everything could be deleted (error {r}).";
    }

    const uint FO_DELETE = 3, PROGDLG_NOTIME = 0x4, PROGDLG_MARQUEEPROGRESS = 0x20;
    const int ERROR_CANCELLED = 1223;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom, pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("EBBC7C04-315E-11d2-B62F-006097DF5BD4")]
    interface IProgressDialog // only up to SetLine, in vtable order
    {
        void StartProgressDialog(IntPtr parent, [MarshalAs(UnmanagedType.IUnknown)] object modeless, uint flags, IntPtr reserved);
        void StopProgressDialog();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetAnimation(IntPtr instance, ushort id);
        [PreserveSig] bool HasUserCancelled();
        void SetProgress(uint completed, uint total);
        void SetProgress64(ulong completed, ulong total);
        void SetLine(uint line, [MarshalAs(UnmanagedType.LPWStr)] string text, bool compactPath, IntPtr reserved);
    }

    /// Deletes through the shell, to the Recycle Bin unless `permanent`. Also used by Undo, quietly. `done` runs on this
    /// thread when it has finished (Find duplicates takes the files that went out of its list).
    internal void Recycle(List<string> paths, UIOption ui, bool permanent = false, Action done = null)
    {
        if (paths.Count == 0) return;
        var owner = new WindowInteropHelper(this).Handle;
        // The Windows delete/recycle dialog runs its own loop on the calling thread and doesn't return
        // until it's done, so it gets a thread of its own (STA, as the shell dialog needs); the window
        // stays responsive and refreshes when the delete finishes. The watcher shows progress meanwhile.
        var t = new Thread(() =>
        {
            string error = null;
            try
            {
                if (permanent && Settings.DeleteSound) error = ShellDelete(owner, paths, FOF_NOCONFIRMATION);
                else if (permanent) DeleteWithProgress(owner, paths);
                else if (paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList() is { Count: > 0 } there) // gone already: nothing to do
                    error = ShellDelete(owner, there, (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING | (ui == UIOption.AllDialogs ? 0 : FOF_SILENT | FOF_NOCONFIRMATION)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error = ex.Message; }
            Dispatcher.BeginInvoke(() =>
            {
                if (error != null) MessageBox.Show(this, error, "Delete");
                Left.Refresh(); Right.Refresh();
                RefreshDrives(); UpdateStats();
                done?.Invoke();
            });
        }) { IsBackground = false }; // finishing a delete beats closing fast
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Num+ / Num-: select or deselect by pattern, pre-filled with the selected file's extension.
    void SelectByPattern(bool select)
    {
        var ext = active.List.SelectedItem is Entry { IsDir: false } cur ? Path.GetExtension(cur.Name) : "";
        var pattern = PromptDialog.Ask(this, select ? "Select" : "Deselect", "Files matching (several: *.jpg;*.png)", ext == "" ? "*.*" : "*" + ext, select ? "Select" : "Deselect", selectAll: true);
        if (pattern != null) active.SelectMatching(pattern, select);
    }

    void MkDir()
    {
        if (PaneView.IsVirtual(active.Dir)) return; // This PC holds drives, an archive its own entries
        var name = PromptDialog.Ask(this, "New folder", "Folder name", "New folder", "Create");
        if (name == null) return;
        var path = Path.Combine(active.Dir, name);
        bool existed = Directory.Exists(path);
        if (Run(() => Directory.CreateDirectory(path)) && !existed)
            Remember($"new folder {name}", new UndoStep("rmdir", path)); // only while still empty
        active.Navigate(active.Dir, name, record: false);
    }

    // Ctrl+N: pick a type (Explorer's own New list), then the name.
    void NewFile()
    {
        if (PaneView.IsVirtual(active.Dir)) return;
        var dialog = new NewFileDialog(this);
        if (dialog.ShowDialog() != true) return;
        string created = null;
        Run(() => created = dialog.Create(active.Dir));
        if (created == null) return;
        var path = Path.Combine(active.Dir, created);
        Remember($"new file {created}", Made(path));
        active.Navigate(active.Dir, created, record: false);
    }

    void EditSelected()
    {
        foreach (var e in active.Selected.Where(x => !x.IsDir && !x.IsUp).Take(10)) EditorWindow.Open(e.Path);
    }

    void Rename() { if (active.Selected.Count > 1) RenameMany(); else active.BeginRename(); }

    void RenameCommitted(PaneView pane, Entry entry, string name)
    {
        var renamed = Path.Combine(Path.GetDirectoryName(entry.Path), name);
        if (Run(() => RenamePath(entry.Path, name))) Remember($"rename to {name}", new UndoStep("rename", renamed, entry.Name, Ferry.Stamp(renamed)));
        if (pane.InSearch) pane.Refresh();
        else pane.Navigate(pane.Dir, name, record: false);
    }

    /// Runs a file operation, shows its error if it fails, refreshes both panes. True when it worked.
    bool Run(Action a)
    {
        bool ok = false;
        try { a(); ok = true; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(this, ex.Message, "Error"); }
        Left.Refresh(); Right.Refresh();
        RefreshDrives();
        return ok;
    }

    void OpenTerminal()
    {
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{active.Dir}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) // Windows Terminal not installed
        { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = active.Dir, UseShellExecute = true }); }
    }

    void ToggleHidden()
    {
        PaneView.ShowHidden = Settings.ShowHidden = !Settings.ShowHidden;
        Settings.Save();
        Left.Refresh(); Right.Refresh();
        Status.Text = PaneView.ShowHidden ? "Showing hidden files" : "Hiding hidden files";
    }

    void SwapPanes()
    {
        string l = Left.Dir, r = Right.Dir;
        Left.Navigate(r); Right.Navigate(l);
    }

    // Mouse side buttons: back / forward in the pane under the pointer.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        DependencyObject d = e.OriginalSource as DependencyObject;
        while (d != null && d is not PaneView) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        var pane = d as PaneView ?? active;
        if (e.ChangedButton == MouseButton.XButton1) pane.Back(); else pane.Forward();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (e.OriginalSource is TextBox)
        {
            // tab shortcuts work from the filter/path box too
            if (ctrl && key is Key.W or Key.T) { if (key == Key.W) active.CloseTab(); else active.NewTab(); e.Handled = true; return; }
            // Filter box only: arrows/Enter/Esc hand control back to the list. The path and rename
            // boxes handle their own Enter/Esc.
            if (e.OriginalSource is not TextBox { Name: "FilterBox" } filter) return;
            if (key == Key.Enter && filter.Text.Trim() is { Length: > 0 } query) { active.Search(query); active.FocusList(); e.Handled = true; }
            else if (key is Key.Down or Key.Enter) { active.FocusList(); e.Handled = true; }
            else if (key == Key.Escape) { active.ClearFilter(); active.FocusList(); e.Handled = true; }
            return;
        }

        switch (key)
        {
            case Key.Tab when ctrl: active.NextTab(shift ? -1 : 1); break;
            case Key.Tab: if (!Settings.SinglePane) Other.FocusList(); break;
            case Key.P when ctrl: SetSinglePane(!Settings.SinglePane); break;
            case Key.P when alt: TogglePreview(); break;
            case Key.F1: OpenHelp(); break;
            case Key.T when ctrl && shift: ReopenClosedTab(); break;
            case Key.T when ctrl: active.NewTab(); break;
            case Key.B when ctrl: if (active.Selected.Count > 0) AddToBin(active.Selected.Select(x => x.Path)); break;
            case Key.W when ctrl: active.CloseTab(); break;
            case Key.L when ctrl: active.EditPath(); break;
            case Key.F when ctrl: active.FocusFilter(); break;
            case Key.H when ctrl: ToggleHidden(); break;
            case Key.U when ctrl: SwapPanes(); break;
            case >= Key.D1 and <= Key.D5 when ctrl && shift:
                active.SetView(new[] { PaneView.ViewMode.Details, PaneView.ViewMode.List, PaneView.ViewMode.IconsM, PaneView.ViewMode.IconsL, PaneView.ViewMode.IconsXL }[key - Key.D1]); break;
            case Key.D when ctrl: PinSelected(); break;
            case Key.Enter when alt: Properties(active.Selected is { Count: > 0 } sel ? sel.Select(x => x.Path).ToList() : new List<string> { active.Dir }); break;
            case Key.OemComma when ctrl: OpenSettings(); break;
            case Key.C when ctrl && !shift: ClipboardPut(cut: false); break;
            case Key.X when ctrl: ClipboardPut(cut: true); break;
            case Key.V when ctrl: ClipboardPaste(); break;
            // F5 refreshes, as in other file managers; Ctrl+F5 copies to the other pane (below)
            case Key.R when ctrl: case Key.F5 when !ctrl: SizeCache.Clear(); active.Refresh(force: true); break;
            case Key.C when ctrl && shift:
                Clipboard.SetText(string.Join(Environment.NewLine, active.Selected.Select(x => x.Path)));
                Status.Text = "Path copied"; break;
            case Key.Oem3 when ctrl: OpenTerminal(); break;
            case Key.Left when alt: active.Back(); break;
            case Key.Right when alt: active.Forward(); break;
            case Key.Up when alt: case Key.Back: active.Up(); break;
            case Key.Space: QuickView(); break;
            case Key.Enter: case Key.F3: active.OpenSelected(); break;
            case Key.F2 when shift: ComparePanes(); break;
            case Key.F2: Rename(); break;
            case Key.F4 when !alt: EditSelected(); break; // Alt+F4 closes the window, it doesn't open the editor
            case Key.Z when ctrl: Undo(); break;
            // Ctrl+F5/F6 copy into the other pane, so with one pane they bring it back first instead of
            // copying somewhere the user cannot see (F5 alone was taken above)
            case Key.F5 or Key.F6 when Settings.SinglePane: SetSinglePane(false); break;
            case Key.F5: Transfer(false); break;
            case Key.F6: Transfer(true); break;
            case Key.F7: MkDir(); break;
            case Key.N when ctrl && shift: MkDir(); break;
            case Key.N when ctrl: NewFile(); break;
            case Key.Delete when shift: case Key.F8 when shift: Delete(permanent: true); break;
            case Key.F8: case Key.Delete: Delete(); break;
            case Key.Escape when active.InSearch: active.ClearFilter(); break;
            case Key.Add: SelectByPattern(true); break;
            case Key.Subtract: SelectByPattern(false); break;
            case Key.Multiply: active.InvertSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Open_Click(object s, RoutedEventArgs e) => active.OpenSelected();
    void Copy_Click(object s, RoutedEventArgs e) => Transfer(false);
    void Move_Click(object s, RoutedEventArgs e) => Transfer(true);
    void MkDir_Click(object s, RoutedEventArgs e) => MkDir();
    void NewFile_Click(object s, RoutedEventArgs e) => NewFile();
    void Delete_Click(object s, RoutedEventArgs e) => Delete();
    void Rename_Click(object s, RoutedEventArgs e) => Rename();
    void Terminal_Click(object s, RoutedEventArgs e) => OpenTerminal();
    void Hidden_Click(object s, RoutedEventArgs e) => ToggleHidden();
    void Panes_Click(object s, RoutedEventArgs e) { SetSinglePane(!Settings.SinglePane); active.FocusList(); }
}
