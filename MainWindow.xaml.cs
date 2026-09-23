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
        PaneGrid.ColumnDefinitions[0].Width = new GridLength(Settings.Split, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[2].Width = new GridLength(1 - Settings.Split, GridUnitType.Star);
        // restore size/position only if it's still on a connected screen
        if (Settings.WindowBounds is { } wb && new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight).IntersectsWith(wb))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            ((Window)this).Left = wb.Left; Top = wb.Top; Width = wb.Width; Height = wb.Height;
        }
        if (Settings.Maximized) WindowState = WindowState.Maximized;
        Closing += (_, _) =>
        {
            Settings.WindowBounds = RestoreBounds;
            Settings.Maximized = WindowState == WindowState.Maximized;
            var cols = PaneGrid.ColumnDefinitions;
            double total = cols[0].ActualWidth + cols[2].ActualWidth;
            if (total > 0) Settings.Split = cols[0].ActualWidth / total; // closed before layout: keep the old value
            foreach (var p in new[] { Left, Right })
            {
                Settings.Tabs[p.Name] = p.OpenTabs.ToList();
                Settings.ActiveTab[p.Name] = p.ActiveTab;
            }
            Settings.Save();
        };
        SourceInitialized += (_, _) => { ApplyAcrylic(this); WatchShellChanges(); };
        StartUpdateChecks();

        foreach (var p in new[] { Left, Right })
        {
            p.Activated += () => SetActive(p);
            p.StatsChanged += () => { if (p == active) UpdateStats(); };
            p.Error += msg => Status.Text = msg;
            p.FilesDropped += (files, dest, move) => Submit(files, dest, move);
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
        // the shell's parsing name for the Recycle Bin: only used here to fetch its real icon
        Favorites.Children.Add(SideButton("", "Recycle Bin", "::{645FF040-5081-101B-9F08-00AA002F954E}", click: OpenRecycleBin));

        LoadQuickAccess();
        ApplyAppearance();
        Transfers.ItemsSource = jobs;
        jobTimer.Tick += JobTick;
        Ferry.Finished += JobDone;
        foreach (var p in new[] { Left, Right })
        {
            HookContextMenu(p);
        }

        RefreshDrives();
        driveTimer.Tick += (_, _) => { RefreshDrives(); UpdateStats(); }; // free space changes without us doing anything
        driveTimer.Start();
        BuildFolderTree();
        BuildSidebar();


        active = Left;
        // last session's tabs, then the folder asked for on the command line as a new tab
        Left.Navigate(user);
        Right.Navigate(DriveInfo.GetDrives().First(d => d.IsReady).Name);
        foreach (var p in new[] { Left, Right })
            if (Settings.Tabs.TryGetValue(p.Name, out var paths))
                p.Restore(paths, Settings.ActiveTab.TryGetValue(p.Name, out var a) ? a : 0);
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (arg != null && Directory.Exists(arg)) { Left.NewTab(); Left.Navigate(arg); }
        SetActive(Left);
        Loaded += (_, _) => Left.FocusList();
        // started from the Win+E agent or a folder double-click: make sure we open in front.
        // After the first paint — on Loaded the window isn't on screen yet and the flip does nothing.
        ContentRendered += (_, _) =>
        {
            // Started by the Win+E agent (--front): Windows' foreground lock keeps a freshly started
            // background-launched window behind the current one. A synthetic Alt tap counts as user
            // input, which lifts the lock for the SetForegroundWindow that follows.
            if (Environment.GetCommandLineArgs().Contains("--front"))
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(new WindowInteropHelper(this).Handle);
            }
            Topmost = true; Topmost = false; Activate(); Left.FocusList();
        };
    }

    // Acrylic behind the whole window (Branding → Aplicatii, WPF section).
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    struct Margins { public int L, R, T, B; }
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
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
        if (quick != null) SetBackdrop(quick);
    }

    public void RefreshPanes()
    {
        PaneView.ShowHidden = Settings.ShowHidden;
        Left.Refresh(); Right.Refresh();
    }

    // Shelf's schedule: 3 s after launch, then every 30 minutes — the app stays open for days.
    readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    UpdateDialog updateDialog;

    void StartUpdateChecks()
    {
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await ShowWhatsNew();
            await CheckForUpdate(manual: false);
        };
        first.Start();
        updateTimer.Tick += async (_, _) => await CheckForUpdate(manual: false);
        updateTimer.Start();
    }

    /// Returns what to tell the user (only used by the manual check in Settings).
    public async Task<string> CheckForUpdate(bool manual)
    {
        if (updateDialog != null) { updateDialog.Activate(); return null; }
        var release = await Updater.Check(manual);
        if (release == null) return manual ? $"File Labs {Updater.Current} is the latest version." : null;

        updateDialog = new UpdateDialog(this, release);
        updateDialog.Closed += (_, _) => { Updater.Dismiss(release); updateDialog = null; };
        updateDialog.Show();
        return null;
    }

    // First launch after an update: the release notes for the version now running.
    async Task ShowWhatsNew()
    {
        var current = Updater.Current.ToString();
        if (Settings.LastSeenVersion == current) return;
        bool upgraded = Settings.LastSeenVersion != "";       // empty = fresh install, nothing to show
        Settings.LastSeenVersion = current;
        Settings.Save();
        if (!upgraded) return;
        // Check() only returns something newer than us; right after updating, the latest IS us.
        var latest = await Updater.Latest();
        if (latest?.Version.ToString() == current) new UpdateDialog(this, latest, notesOnly: true).Show();
    }

    SettingsWindow settingsWindow;
    void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }
    void Settings_Click(object s, RoutedEventArgs e) => OpenSettings();

    // A second launch sent us a folder → new tab in the active pane. Sent nothing (Win+E, Start menu,
    // taskbar) → just bring the existing window to the front, like switching to it.
    public void OpenFromOutside(string path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) { active.NewTab(); active.Navigate(path); }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // a brief Topmost flip gets the window above others even when Windows refuses a plain Activate
        Topmost = true; Topmost = false;
        Activate();
        active.FocusList();
    }

    void SidebarSplitter_DragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Settings.SidebarWidth = SidebarColumn.ActualWidth;
        Settings.Save();
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

    Button SideButton(string icon, string text, string path, double? used = null, Action click = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(ShellIcon(path, icon, 16, new(0, 0, 10, 0)));
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (used is double u)
        {
            var bar = new Grid { Height = 2, Margin = new(0, 4, 0, 1) };
            bar.Children.Add(new Border { Background = Hex("#1AFFFFFF") });
            var fill = new Border { Background = u > 0.9 ? Hex("#F87171") : (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Left };
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * u;
            bar.Children.Add(fill);
            label.Children.Add(bar);
        }
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
    record DriveRow(string Name, string Label, string Format, long Total, long Free, bool Network);

    static List<DriveRow> ReadDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
            try
            {
                if (!d.IsReady) continue;
                bool net = d.DriveType == DriveType.Network;
                var label = net && GetNetworkPath(d.Name) is { } unc ? $"({unc})" : d.VolumeLabel;
                rows.Add(new DriveRow(d.Name, label, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, net));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // drive went away mid-read
        return rows;
    }

    List<DriveRow> shownDrives = new();

    void RefreshDrives() => Task.Run(ReadDrives).ContinueWith(t =>
    {
        var rows = t.Result;
        if (rows.SequenceEqual(shownDrives)) return; // nothing moved: leave the buttons alone
        shownDrives = rows;
        Drives.Children.Clear();
        foreach (var d in rows) Drives.Children.Add(DriveButton(d));
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
        var fill = new Border { Background = frac > 0.9 ? Hex("#F87171") : (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Left };
        bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * frac;
        bar.Children.Add(fill);
        Grid.SetColumn(name, 1); Grid.SetColumn(size, 2);
        Grid.SetRow(bar, 1); Grid.SetColumn(bar, 1); Grid.SetColumnSpan(bar, 2);
        g.Children.Add(icon); g.Children.Add(name); g.Children.Add(size); g.Children.Add(bar);
        var b = new Button { Content = g, Style = (Style)FindResource("Side"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 1, 0, 1), ToolTip = $"{d.Name}  {d.Format}  ·  {Fmt(d.Free)} free" };
        b.Click += (_, _) => { active.Navigate(d.Name); active.FocusList(); };
        b.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { active.OpenInBackgroundTab(d.Name); e.Handled = true; } };
        return b;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string local, System.Text.StringBuilder remote, ref int len);
    static string GetNetworkPath(string root)
    {
        var sb = new System.Text.StringBuilder(512); int len = sb.Capacity;
        return WNetGetConnection(root.TrimEnd((char)92), sb, ref len) == 0 ? sb.ToString() : null;
    }

    // Quick access: pinned folders, one path per line in %AppData%\FileLabs\quickaccess.txt
    static readonly string QuickFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLabs", "quickaccess.txt");
    List<string> pinned = new();

    void LoadQuickAccess()
    {
        if (File.Exists(QuickFile)) pinned = File.ReadAllLines(QuickFile).Where(l => l.Trim() != "").ToList();
        BuildQuickAccess();
    }

    void BuildQuickAccess()
    {
        QuickAccess.Children.Clear();
        foreach (var path in pinned)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            var b = SideButton(Directory.Exists(path) ? "\uE718" : "\uE7BA", name == "" ? path : name, path); // pin / warning if gone
            var up = new MenuItem { Header = "Move up" };
            var remove = new MenuItem { Header = "Remove from Quick access" };
            up.Click += (_, _) => { int i = pinned.IndexOf(path); if (i > 0) { pinned.RemoveAt(i); pinned.Insert(i - 1, path); SaveQuickAccess(); } };
            remove.Click += (_, _) => { pinned.Remove(path); SaveQuickAccess(); };
            b.ContextMenu = new ContextMenu { Items = { up, remove } };
            QuickAccess.Children.Add(b);
        }
        QuickHint.Visibility = pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveQuickAccess()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QuickFile));
        File.WriteAllLines(QuickFile, pinned);
        BuildQuickAccess();
    }

    // Pins the selected folders, or the current folder if no folder is selected.
    void PinSelected()
    {
        var dirs = active.Selected.Where(e => e.IsDir).Select(e => e.Path).DefaultIfEmpty(active.Dir);
        var added = dirs.Where(d => !pinned.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count == 0) { Status.Text = "Already in Quick access"; return; }
        pinned.AddRange(added);
        SaveQuickAccess();
        Status.Text = $"Pinned {string.Join(", ", added.Select(Path.GetFileName))}";
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
        var thread = new Thread(() =>
        {
            var img = Thumbnails.Get(path, 32, iconOnly: true);
            dispatcher.BeginInvoke(() => { shellIcons[path] = img; Show(img); });
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return box;
    }

    public static Brush Hex(string c) => (Brush)new BrushConverter().ConvertFrom(c);

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
    }

    string publishedFolder;
    readonly System.Windows.Threading.DispatcherTimer driveTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    void UpdateStats()
    {
        if (active?.Dir is not { Length: > 0 } dir) return;
        Status.Text = dir;
        bool bareServer = dir.StartsWith(@"\\") && !dir.Trim('\\').Contains('\\'); // not a folder a dialog can open
        if (dir != publishedFolder && !bareServer)
        {
            // for Ctrl+G in Open/Save dialogs (Agent.cs)
            publishedFolder = dir;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Agent.StateKey);
            key.SetValue("CurrentFolder", dir);
        }
        if (dir.StartsWith(@"\\")) return; // DriveInfo only takes drive letters; a UNC path threw here
        var d = new DriveInfo(Path.GetPathRoot(dir));
        if (!d.IsReady) return;
        StatTotal.Text = Fmt(d.TotalSize);
        StatUsed.Text = Fmt(d.TotalSize - d.AvailableFreeSpace);
        StatFree.Text = Fmt(d.AvailableFreeSpace);
        StatFreePct.Text = $"free · {100.0 * d.AvailableFreeSpace / d.TotalSize:0}% of the disk";
        StatFolder.Text = Fmt(active.FolderTotal);
        StatFolderSub.Text = active.SizesPending ? "this folder · calculating…" : $"this folder · {active.Items.Count()} items";
        StatLabel.Text = d.Name.TrimEnd('\\');
        StatFs.Text = $"{d.DriveFormat} · {(d.VolumeLabel == "" ? d.DriveType.ToString() : d.VolumeLabel)}";
        Status.Text = dir;
    }

    // Copy/move through the Ferry engine; delete still goes to the Recycle Bin via the VB API.
    readonly System.Collections.ObjectModel.ObservableCollection<Job> jobs = new();

    void Transfer(bool move) => Submit(active.Selected.Select(e => e.Path).ToList(), Other.Dir, move);

    Job Submit(List<string> sources, string dest, bool move)
    {
        if (sources.Count == 0) return null;
        var job = Ferry.Submit(sources, dest, move);
        jobs.Insert(0, job);
        jobTimer.Start();
        return job;
    }

    readonly System.Windows.Threading.DispatcherTimer jobTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    void JobTick(object s, EventArgs e)
    {
        foreach (var j in jobs) j.Refresh();
        if (!jobs.Any(j => j.Active)) jobTimer.Stop();
    }

    void JobDone(Job job) => Dispatcher.BeginInvoke(() =>
    {
        job.Refresh();
        RememberJob(job);
        Left.Refresh(); Right.Refresh();
        RefreshDrives(); UpdateStats(); // a finished copy or move changes what's free
    });

    static Job JobOf(object sender) => (Job)((FrameworkElement)sender).DataContext;
    void JobPause_Click(object s, RoutedEventArgs e) { var j = JobOf(s); if (j.Paused) j.Resume(); else j.Pause(); }
    void JobCancel_Click(object s, RoutedEventArgs e) => JobOf(s).Cancel();
    void JobDismiss_Click(object s, RoutedEventArgs e) => jobs.Remove(JobOf(s));
    void JobResolve_Click(object s, RoutedEventArgs e) => JobOf(s).Resolve(Enum.Parse<Choice>((string)((Button)s).CommandParameter));

    // Clipboard in Explorer's own format (CF_HDROP + Preferred DropEffect), so copy/paste works both ways.
    void ClipboardPut(bool cut)
    {
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

    void ClipboardPaste()
    {
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
        Submit(files, active.Dir, move);
        if (move) { Clipboard.Clear(); PaneView.SetCut(Array.Empty<string>(), Left, Right); } // a cut is pasted once, like Explorer
    }

    // Shift+Delete skips the Recycle Bin. Always asked first: the shell only confirms when the user has
    // switched its delete confirmation on (off by default), and this one can't be taken back.
    void Delete(bool permanent = false)
    {
        var sel = active.Selected;
        if (sel.Count == 0) return;
        if (permanent && MessageBox.Show(this,
                $"Permanently delete {(sel.Count == 1 ? $"“{sel[0].Name}”" : $"these {sel.Count} items")}?\n\nThey will not go to the Recycle Bin.",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Recycle(sel.Select(e => e.Path).ToList(), UIOption.AllDialogs, permanent);
    }

    /// Deletes through the shell, to the Recycle Bin unless `permanent`. Also used by Undo, quietly.
    void Recycle(List<string> paths, UIOption ui, bool permanent = false)
    {
        if (paths.Count == 0) return;
        var how = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
        // The Windows delete/recycle dialog runs its own loop on the calling thread and doesn't return
        // until it's done, so it gets a thread of its own (STA, as the shell dialog needs); the window
        // stays responsive and refreshes when the delete finishes. The watcher shows progress meanwhile.
        var t = new Thread(() =>
        {
            string error = null;
            try
            {
                foreach (var p in paths)
                    if (Directory.Exists(p)) VbFs.DeleteDirectory(p, ui, how, UICancelOption.ThrowException);
                    else if (File.Exists(p)) VbFs.DeleteFile(p, ui, how, UICancelOption.ThrowException);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error = ex.Message; }
            Dispatcher.BeginInvoke(() =>
            {
                if (error != null) MessageBox.Show(this, error, "Delete");
                Left.Refresh(); Right.Refresh();
                RefreshDrives(); UpdateStats();
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
        var name = PromptDialog.Ask(this, "New folder", "Folder name", "New folder", "Create");
        if (name == null) return;
        var path = Path.Combine(active.Dir, name);
        bool existed = Directory.Exists(path);
        if (Run(() => Directory.CreateDirectory(path)) && !existed)
            Remember($"new folder {name}", () => Directory.Delete(path)); // only while still empty: Delete refuses otherwise
        active.Navigate(active.Dir, name, record: false);
    }

    // Ctrl+N: pick a type (Explorer's own New list), then the name.
    void NewFile()
    {
        var dialog = new NewFileDialog(this);
        if (dialog.ShowDialog() != true) return;
        string created = null;
        Run(() => created = dialog.Create(active.Dir));
        if (created == null) return;
        var path = Path.Combine(active.Dir, created);
        Remember($"new file {created}", () => Recycle(new() { path }, UIOption.OnlyErrorDialogs));
        active.Navigate(active.Dir, created, record: false);
    }

    void Rename() { if (active.Selected.Count > 1) RenameMany(); else active.BeginRename(); }

    void RenameCommitted(PaneView pane, Entry entry, string name)
    {
        var renamed = Path.Combine(Path.GetDirectoryName(entry.Path), name);
        if (Run(() => RenamePath(entry.Path, name))) Remember($"rename to {name}", () => RenamePath(renamed, entry.Name));
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
            case Key.Tab: Other.FocusList(); break;
            case Key.T when ctrl: active.NewTab(); break;
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
            case Key.R when ctrl: SizeCache.Clear(); active.Refresh(); break;
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
            case Key.Z when ctrl: Undo(); break;
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
}
