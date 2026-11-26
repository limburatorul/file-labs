using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using F = FileExplorer.Fmt;

namespace FileExplorer;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }
    public static PaneView DraggedFrom;
    public static readonly Stack<string> ClosedTabs = new();
    public static string Version => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0";

    PaneView active, other;
    PaneView Other => active == Left ? Right : Left;

    public MainWindow(string[] args)
    {
        Instance = this;
        InitializeComponent();
        VersionText.Text = Version;
        ApplyAccent();
        ApplyBackground();

        var home = PaneView.Home;
        Left.Init("Left", args.FirstOrDefault(Directory.Exists) ?? home);
        Right.Init("Right", UserDir("DOWNLOAD", "Downloads"));
        foreach (var p in new[] { Left, Right })
        {
            var pane = p;
            pane.Activated += () => SetActive(pane);
            pane.Changed += x => { if (x == active) UpdateStats(); if (Settings.Preview && x == active) ShowPreview(); };
            pane.FilesDropped += (files, into, move) => Submit(files, into, move);
            pane.ContextRequested += (x, row, at) => ShowContextMenu(x, row);
            pane.RenameCommitted += (x, e, name) => Rename(x, e, name);
            pane.Opened += d => { Settings.AddRecent(Settings.RecentFolders, d, Settings.RecentMax); if (pane == active) SyncTree(d); };
        }
        SetActive(Left);
        ApplyPanes();
        BuildSidebar();
        BuildCommandBar();
        WireWindow();
        BuildBin();
        StartUpdates();
        Ferry.Finished += job => Dispatcher.UIThread.Post(() => JobFinished(job));
        jobTimer.Tick += (_, _) =>
        {
            foreach (var j in jobs.Where(j => j.Active)) j.Refresh();
            // a question waits (files already there): the window comes up with it, once per job; under the panes it is on the card
            if (Settings.TransfersShow != "bottom") foreach (var j in jobs.Where(j => j.HasConflicts).ToList()) if (askedForward.Add(j)) ShowTransfers();
            UpdateLamp();
        };
        mountTimer.Tick += (_, _) => { var now = MountKey(); if (now != mountKey) { mountKey = now; BuildSidebar(); } };
        mountKey = MountKey(); mountTimer.Start();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            Left.FocusList();
            if (Settings.LastSeenVersion != Version && Settings.LastSeenVersion != "") Dispatcher.UIThread.Post(() => Open(() => new WhatsNewWindow()));
            Settings.LastSeenVersion = Version;
        };
        Closing += (_, _) => SaveState();
    }

    // ---- small things the panes and dialogs need ----
    public static void Say(string text) { if (Instance != null) Instance.Status.Text = text; }

    static string UserDir(string key, string fallback)
    {
        var home = PaneView.Home;
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "user-dirs.dirs");
            if (File.Exists(f))
                foreach (var line in File.ReadAllLines(f))
                    if (line.StartsWith($"XDG_{key}_DIR=")) { var v = line[(key.Length + 9)..].Trim('"').Replace("$HOME", home); if (Directory.Exists(v)) return v; }
        }
        catch (IOException) { }
        var d = Path.Combine(home, fallback);
        return Directory.Exists(d) ? d : home;
    }

    void SetActive(PaneView p)
    {
        if (active == p) return;
        active = p;
        Left.SetActive(p == Left);
        Right.SetActive(p == Right);
        UpdateStats();
        if (Settings.Preview) ShowPreview();
        SyncTree(p.Dir);
    }

    public void ApplyAccent()
    {
        var c = Color.Parse(Settings.Accent);
        var res = Application.Current!.Resources;
        double k = Settings.AccentStrength;
        byte A(double a) => (byte)Math.Clamp(a * k, 0, 255);
        res["Accent"] = new SolidColorBrush(c);
        res["AccentEdge"] = new SolidColorBrush(Color.FromArgb(A(0xA6), c.R, c.G, c.B));
        res["AccentSoft"] = new SolidColorBrush(Color.FromArgb(A(0x45), c.R, c.G, c.B));
        // the selection: the accent mixed onto the panes' dark surface, opaque; dark text on a light accent
        double t = Math.Clamp(0.55 * k, 0.15, 1);
        var bg = Color.FromRgb(0x1A, 0x1E, 0x27);
        var sel = Color.FromRgb((byte)(bg.R + (c.R - bg.R) * t), (byte)(bg.G + (c.G - bg.G) * t), (byte)(bg.B + (c.B - bg.B) * t));
        res["AccentSelect"] = new SolidColorBrush(sel);
        res["AccentSelectText"] = 0.299 * sel.R + 0.587 * sel.G + 0.114 * sel.B > 150 ? Brushes.Black : Brushes.White;
        res["RowPad"] = new Thickness(8, Settings.RowPad, 8, Settings.RowPad);
        Left?.SetActive(active == Left); Right?.SetActive(active == Right);
    }

    public void ApplyBackground()
    {
        var path = Settings.BackgroundImage;
        if (path != "" && File.Exists(path))
        {
            try { BgImage.Source = new Avalonia.Media.Imaging.Bitmap(path); } catch (Exception) { BgImage.Source = null; }
            BgImage.IsVisible = BgImage.Source != null;
            BgImage.Opacity = Settings.ImageOpacity / 100;
            var b = Settings.ImageBrightness;
            BgShade.Background = new SolidColorBrush(b < 0 ? Color.FromArgb((byte)(-b * 2.55), 0, 0, 0) : Color.FromArgb((byte)(b * 2.55), 255, 255, 255));
            BgShade.IsVisible = b != 0;
        }
        else { BgImage.IsVisible = false; BgShade.IsVisible = false; }
    }

    void ApplyPanes()
    {
        var g = PaneGrid.ColumnDefinitions;
        if (Settings.SinglePane) { g[1].Width = new GridLength(0); g[2].Width = new GridLength(0); Right.IsVisible = false; PaneSplitter.IsVisible = false; if (active == Right) SetActive(Left); }
        else
        {
            g[0].Width = new GridLength(Settings.Split, GridUnitType.Star); g[1].Width = new GridLength(10); g[2].Width = new GridLength(1 - Settings.Split, GridUnitType.Star);
            Right.IsVisible = true; PaneSplitter.IsVisible = true;
        }
        var a = PaneArea.ColumnDefinitions;
        a[1].Width = new GridLength(Settings.Preview ? 10 : 0);
        a[2].Width = new GridLength(Settings.Preview ? Settings.PreviewWidth : 0);
        PreviewPane.IsVisible = PreviewSplitter.IsVisible = Settings.Preview;
        StatsRow.IsVisible = Settings.Stats > 0;
        // the window's buttons sit over the right pane's tab row (or the left one's, with one pane)
        (Settings.SinglePane ? Left : Right).CaptionSpace.Width = 190;
        (Settings.SinglePane ? Right : Left).CaptionSpace.Width = 0;
    }

    void SaveState()
    {
        Left.SaveTabs(); Right.SaveTabs();
        Settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) Settings.WindowBounds = (Position.X, Position.Y, Width, Height);
        Settings.SidebarWidth = Root.ColumnDefinitions[0].ActualWidth;
        var g = PaneGrid.ColumnDefinitions;
        if (!Settings.SinglePane && g[0].ActualWidth + g[2].ActualWidth > 0) Settings.Split = Math.Clamp(g[0].ActualWidth / (g[0].ActualWidth + g[2].ActualWidth), 0.15, 0.85);
        if (Settings.Preview && PaneArea.ColumnDefinitions[2].ActualWidth > 0) Settings.PreviewWidth = PaneArea.ColumnDefinitions[2].ActualWidth;
        Settings.Save();
        SizeCache.Save();
    }

    // ---- the window without a system frame: move by the empty tab row and the app's name, resize by the edges ----
    void WireWindow()
    {
        if (Settings.WindowBounds is { } b) { Position = new PixelPoint((int)b.X, (int)b.Y); Width = b.W; Height = b.H; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Settings.Maximized) WindowState = WindowState.Maximized;
        Root.ColumnDefinitions[0].Width = new GridLength(Settings.SidebarWidth);
        Caption.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { if (e.ClickCount == 2) ToggleMax(); else BeginMoveDrag(e); } };
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMax();
        CloseButton.Click += (_, _) => Close();
        HistoryButton.Click += (_, _) => ShowHistory(HistoryButton);
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) { MaxButton.Content = WindowState == WindowState.Maximized ? Glyph.Unmaximize : Glyph.Maximize; } };
        void Grip(Control c, WindowEdge edge) => c.PointerPressed += (_, e) => { if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(edge, e); };
        Grip(GripW, WindowEdge.West); Grip(GripE, WindowEdge.East); Grip(GripN, WindowEdge.North); Grip(GripS, WindowEdge.South);
        Grip(GripSE, WindowEdge.SouthEast); Grip(GripSW, WindowEdge.SouthWest);
        SettingsButton.Click += (_, _) => Dialog(() => new SettingsWindow());
        HelpButton.Click += (_, _) => Open(() => new HelpWindow());
        WhatsNewButton.Click += (_, _) => Open(() => new WhatsNewWindow());
        PizzaButton.Click += (_, _) => Shell.Open("https://buymeacoffee.com/protagonistlabs?utm_source=filelabs-linux&utm_medium=app&utm_campaign=pizza");
        PreviewClose.Click += (_, _) => TogglePreview();
    }
    /// A window owned by this one, waited for; what goes wrong shows in the status line instead of vanishing in the task.
    /// Each is activated as it opens: on KDE an owned window could otherwise open behind its maximized owner.
    async void Dialog(Func<Window> make)
    {
        try { var w = make(); w.Opened += (_, _) => w.Activate(); await w.ShowDialog(this); }
        catch (Exception ex) { Say(ex.Message); Console.Error.WriteLine(ex); }
    }

    void Open(Func<Window> make)
    {
        try { var w = make(); w.Opened += (_, _) => w.Activate(); w.Show(this); }
        catch (Exception ex) { Say(ex.Message); Console.Error.WriteLine(ex); }
    }

    void ToggleMax() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---- sidebar: Quick access, places, drives ----
    readonly DispatcherTimer mountTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    string mountKey;
    static string MountKey() { try { return File.ReadAllText("/proc/self/mounts"); } catch (IOException) { return ""; } }

    void BuildSidebar()
    {
        SidebarHost.Children.Clear();
        var quick = new StackPanel();
        foreach (var path in Settings.QuickAccess)
        {
            var p = path;
            var b = SideButton(Directory.Exists(p) ? Glyph.Pin : Glyph.Warning, Path.GetFileName(p.TrimEnd('/')) is { Length: > 0 } n ? n : p, p, "#F5C451");
            var unpin = new MenuItem { Header = "Unpin from Quick access", Icon = new TextBlock { Text = Glyph.Unpin, Classes = { "icon" } } };
            unpin.Click += (_, _) => { Settings.QuickAccess.Remove(p); Settings.Save(); BuildSidebar(); };
            var up = new MenuItem { Header = "Move up" }; up.Click += (_, _) => MovePin(p, -1);
            var down = new MenuItem { Header = "Move down" }; down.Click += (_, _) => MovePin(p, 1);
            b.ContextMenu = new ContextMenu { Items = { unpin, up, down } };
            quick.Children.Add(b);
        }
        if (Settings.QuickAccess.Count == 0) quick.Children.Add(new TextBlock { Text = "Ctrl+D pins a folder here", Foreground = F.Brush("#59FFFFFF"), FontSize = 11, Margin = new(8, 2, 0, 0) });
        // Quick access lists (as on Windows): switch, add, rename, reorder and delete them from the button on the header
        var lists = new Button { Classes = { "ghost" }, Padding = new(6, 1), Content = new TextBlock { Text = Glyph.ListView, Classes = { "icon" }, FontSize = 11, Foreground = F.Brush("#8A97AA") } };
        ToolTip.SetTip(lists, "Quick access lists: switch, add, rename, reorder, delete");
        lists.Click += (_, _) => QuickListsMenu().Open(lists);
        if (!Settings.SidebarHidden.Contains("quick")) SidebarHost.Children.Add(Section("quick", Settings.CurrentQuickList.Name, quick, lists));

        var home = PaneView.Home;
        var places = new StackPanel();
        foreach (var (glyph, name, path) in new[]
        {
            (Glyph.Home, "Home", home), (Glyph.Desktop, "Desktop", UserDir("DESKTOP", "Desktop")), (Glyph.Document, "Documents", UserDir("DOCUMENTS", "Documents")),
            (Glyph.Download, "Downloads", UserDir("DOWNLOAD", "Downloads")), (Glyph.Image, "Pictures", UserDir("PICTURES", "Pictures")),
            (Glyph.Music, "Music", UserDir("MUSIC", "Music")), (Glyph.Video, "Videos", UserDir("VIDEOS", "Videos")),
        }.Where(x => x.Item3 != home || x.Item2 == "Home").Where(x => !Settings.SidebarHidden.Contains(x.Item2)).DistinctBy(x => x.Item3))
            places.Children.Add(SideButton(glyph, name, path, "#60A5FA"));
        var trash = SideButton(Glyph.Trash, "Trash", null, "#9CA3AF");
        trash.Click += (_, _) => Open(() => new TrashWindow());
        if (!Settings.SidebarHidden.Contains("Trash")) places.Children.Add(trash);
        if (!Settings.SidebarHidden.Contains("favorites")) SidebarHost.Children.Add(Section("favorites", "Favorites", places));

        var drives = new StackPanel();
        foreach (var m in Mounts.Places())
        {
            var (total, free) = m.Space;
            var row = new StackPanel();
            var top = new DockPanel();
            var size = new TextBlock { Text = total > 0 ? $"{F.Size(free)} free" : "", FontSize = 11, Foreground = F.Brush("#8CFFFFFF"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(size, Dock.Right);
            top.Children.Add(size);
            top.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = m.Network ? Glyph.NetworkDrive : m.Removable ? Glyph.Usb : Glyph.Drive, Classes = { "icon" }, FontSize = 15, Foreground = F.Brush("#9AA6B8"), Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = m.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 10, 0) },
                },
            });
            row.Children.Add(top);
            if (total > 0)
            {
                double used = 1 - (double)free / total;
                var bar = new Grid { Height = 3, Margin = new(24, 5, 0, 0) };
                bar.Children.Add(new Border { Background = F.Brush("#12FFFFFF"), CornerRadius = new(1.5) });
                var fill = new Border { CornerRadius = new(1.5), HorizontalAlignment = HorizontalAlignment.Left, Background = used > 0.9 ? F.Brush("#F87171") : (IBrush)Application.Current!.FindResource("Accent") };
                bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * used;
                bar.Children.Add(fill);
                row.Children.Add(bar);
            }
            var b = new Button { Classes = { "ghost", "side" }, Content = row, Padding = new(8, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(b, $"{m.Point}  ·  {m.Type}{(total > 0 ? $"  ·  {F.Size(total - free)} of {F.Size(total)} used" : "")}");
            var point = m.Point;
            b.Click += (_, _) => active.Navigate(point);
            AllowDropOn(b, point);
            drives.Children.Add(b);
        }
        if (!Settings.SidebarHidden.Contains("drives")) SidebarHost.Children.Add(Section("drives", "Drives", drives));
        if (!Settings.SidebarHidden.Contains("folders")) SidebarHost.Children.Add(Section("folders", "Folders", BuildTree()));
        if (active != null) Dispatcher.UIThread.Post(() => SyncTree(active.Dir, reveal: false), DispatcherPriority.Background);
    }

    ContextMenu QuickListsMenu()
    {
        var m = new ContextMenu();
        var lists = Settings.QuickLists;
        var current = Settings.CurrentQuickList;
        int at = lists.IndexOf(current);
        for (int i = 0; i < lists.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = lists[i].Name, FontWeight = lists[i] == current ? FontWeight.SemiBold : FontWeight.Normal,
                                      Icon = lists[i] == current ? new TextBlock { Text = Glyph.Check, Classes = { "icon" } } : null };
            ToolTip.SetTip(item, $"{lists[i].Folders.Count} folder{(lists[i].Folders.Count == 1 ? "" : "s")}");
            item.Click += (_, _) => SwitchQuickList(index);
            m.Items.Add(item);
        }
        m.Items.Add(new Separator());
        MenuItem Do(string header, string glyph, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Text = glyph, Classes = { "icon" } } };
            item.Click += (_, _) => run();
            m.Items.Add(item);
            return item;
        }
        Do("New list…", Glyph.Add, async () =>
        {
            if (await Dialogs.Ask(this, "New Quick access list", "Name of the list", "New list") is not { Length: > 0 } name) return;
            lists.Insert(at + 1, new Settings.QuickList { Name = name.Trim() });
            SwitchQuickList(at + 1);
        });
        Do($"Rename “{current.Name}”…", Glyph.Rename, async () =>
        {
            if (await Dialogs.Ask(this, "Rename list", "Name of the list", current.Name) is not { Length: > 0 } name) return;
            current.Name = name.Trim();
            SwitchQuickList(at);
        });
        Do("Move list up", Glyph.Up, () => MoveQuickList(at, at - 1), at > 0);
        Do("Move list down", Glyph.Down, () => MoveQuickList(at, at + 1), at < lists.Count - 1);
        Do($"Delete “{current.Name}”…", Glyph.Delete, async () =>
        {
            if (current.Folders.Count > 0 && !await Dialogs.Confirm(this, $"Delete the list “{current.Name}”?",
                    $"Its {current.Folders.Count} folder{(current.Folders.Count == 1 ? "" : "s")} leave Quick access; the folders themselves stay where they are.")) return;
            lists.Remove(current);
            SwitchQuickList(Math.Min(at, lists.Count - 1));
        }, lists.Count > 1);
        return m;
    }

    void SwitchQuickList(int index) { Settings.ShownQuickList = index; Settings.Save(); BuildSidebar(); }

    void MoveQuickList(int from, int to)
    {
        var list = Settings.QuickLists[from];
        Settings.QuickLists.RemoveAt(from);
        Settings.QuickLists.Insert(to, list);
        SwitchQuickList(to);
    }

    void MovePin(string p, int by)
    {
        int i = Settings.QuickAccess.IndexOf(p), j = i + by;
        if (i < 0 || j < 0 || j >= Settings.QuickAccess.Count) return;
        (Settings.QuickAccess[i], Settings.QuickAccess[j]) = (Settings.QuickAccess[j], Settings.QuickAccess[i]);
        Settings.Save(); BuildSidebar();
    }

    Control Section(string key, string title, Control content, Control extra = null)
    {
        bool collapsed = Settings.SidebarCollapsed.Contains(key);
        var chevron = new TextBlock { Text = collapsed ? Glyph.ChevronRight : Glyph.ChevronDown, Classes = { "icon" }, FontSize = 10, Foreground = F.Brush("#8A97AA"), Margin = new(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var header = new Border
        {
            Classes = { "sideHeader" }, Padding = new(8, 5), Margin = new(0, 0, 0, 2), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, new TextBlock { Text = title, Foreground = (IBrush)Application.Current!.FindResource("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis } } },
        };
        if (extra != null)
        {
            extra.HorizontalAlignment = HorizontalAlignment.Right; extra.VerticalAlignment = VerticalAlignment.Center;
            var row = header.Child; header.Child = null;
            header.Child = new Grid { Children = { row, extra } };
        }
        content.IsVisible = !collapsed;
        header.PointerPressed += (_, _) =>
        {
            content.IsVisible = !content.IsVisible;
            chevron.Text = content.IsVisible ? Glyph.ChevronDown : Glyph.ChevronRight;
            if (content.IsVisible) Settings.SidebarCollapsed.Remove(key); else Settings.SidebarCollapsed.Add(key);
        };
        return new Border
        {
            Background = F.Brush("#08FFFFFF"), BorderBrush = F.Brush("#12FFFFFF"), BorderThickness = new(1), CornerRadius = new(8), Padding = new(4), Margin = new(0, 0, 0, 10),
            Child = new StackPanel { Children = { header, content } },
        };
    }

    Button SideButton(string glyph, string name, string path, string color)
    {
        var b = new Button
        {
            Classes = { "ghost", "side" }, Padding = new(8, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 15, Foreground = F.Brush(color), Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        if (path != null)
        {
            ToolTip.SetTip(b, path);
            b.Click += (_, _) => active.Navigate(path);
            b.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(b).Properties.IsMiddleButtonPressed) { active.NewTab(path); e.Handled = true; } }, RoutingStrategies.Tunnel);
            AllowDropOn(b, path);
        }
        return b;
    }

    void AllowDropOn(Control c, string into)
    {
        DragDrop.SetAllowDrop(c, true);
        DragDrop.AddDragOverHandler(c, (_, e) => { e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None; e.Handled = true; });
        DragDrop.AddDropHandler(c, async (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            var paths = files?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            if (paths is { Count: > 0 }) Submit(paths, into, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        });
    }

    // ---- the command bar ----
    void BuildCommandBar()
    {
        Avalonia.Controls.Documents.Run Cmd(string key, string text, Action run, string tip = null)
        {
            var b = new Button { Classes = { "ghost", "bar" }, FontSize = 11 };
            var t = new TextBlock();
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run(key) { Foreground = F.Brush("#8CFFFFFF") });
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + text));
            b.Content = t;
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => run();
            CommandBar.Children.Add(b);
            return (Avalonia.Controls.Documents.Run)t.Inlines[0];
        }
        Cmd("F2", "Rename", () => active.BeginRename());
        Cmd("F3", "Open", () => active.OpenSelected());
        copyKey = Cmd(Settings.F5Copies ? "F5" : "Ctrl+F5", "Copy", () => Transfer(false), "Copy to the other pane");
        Cmd("F6", "Move", () => Transfer(true));
        Cmd("F7", "New folder", NewFolder, "New folder (F7 or Ctrl+Shift+N)");
        Cmd("Ctrl+N", "New file", NewFile);
        Cmd("F8", "Delete", () => Delete(permanent: false), "Move to the Trash (F8 or Del). Shift+Del deletes for good.");
        Cmd("Ctrl+`", "Terminal", () => Shell.Terminal(active.Dir));
        Cmd("Ctrl+H", "Hidden", ToggleHidden);
        Cmd("Ctrl+P", "One / two panes", TogglePanes);
        Cmd("Alt+P", "Preview", TogglePreview);
        // always there (as on Windows): it lights up while a copy or move runs, with a thin bar of how far they all are
        transfersButton = new Button { Classes = { "ghost", "bar" }, FontSize = 11 };
        transfersBar = new ProgressBar { Height = 2, MinHeight = 0, MinWidth = 0, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(-5, 0, -5, -7), IsVisible = false, IsHitTestVisible = false };
        transfersBar.Bind(ProgressBar.ForegroundProperty, transfersBar.GetResourceObservable("Accent"));
        // the lamp is a border of its own: the button's style paints its own background over the button's
        transfersLamp = new Border { CornerRadius = new(5), BorderThickness = new(1), BorderBrush = Brushes.Transparent, Padding = new(6, 1), Margin = new(-6, -2),
            Child = new Grid { Children = { new TextBlock { Inlines = { new Avalonia.Controls.Documents.Run(Glyph.Transfers) { Classes = { "icon" } }, new Avalonia.Controls.Documents.Run(" Transfers") } }, transfersBar } } };
        transfersButton.Content = transfersLamp;
        transfersLamp.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(850) } };
        ToolTip.SetTip(transfersButton, "Copies and moves: progress, history and Undo");
        transfersButton.Click += (_, _) => ShowTransfers();
        CommandBar.Children.Add(transfersButton);
    }
    Button transfersButton;
    ProgressBar transfersBar;
    Border transfersLamp;
    Avalonia.Controls.Documents.Run copyKey;

    // ---- keyboard: the same keys as on Windows ----
    void OnKeyDown(object s, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool inText = FocusManager?.GetFocusedElement() is TextBox;
        if (inText && !(e.Key is Key.F5 or Key.F6 or Key.F7 or Key.F1 or Key.Tab) && !(ctrl && e.Key is Key.T or Key.W or Key.L)) return;
        bool handled = true;
        switch (e.Key)
        {
            case Key.F1: Open(() => new HelpWindow()); break;
            case Key.F2 when shift: ComparePanes(); break;
            case Key.F2: active.BeginRename(); break;
            case Key.F3: active.OpenSelected(); break;
            case Key.F4 when !ctrl: if (active.Current is { IsDir: false } f) new EditorWindow(f.Path).Show(); break;
            case Key.F5 when ctrl: Transfer(false); break;
            case Key.F5: if (Settings.F5Copies) Transfer(false); else active.Refresh(keepSelection: true); break; // Settings → Transfers
            case Key.F6: Transfer(true); break;
            case Key.F7: NewFolder(); break;
            case Key.N when ctrl && shift: NewFolder(); break;
            case Key.N when ctrl: NewFile(); break;
            case Key.F8 or Key.Delete: Delete(permanent: shift); break;
            case Key.Enter when alt: Properties(); break;
            case Key.Enter: active.OpenSelected(); break;
            case Key.Back: active.GoUp(); break;
            case Key.Left when alt: active.GoBack(); break;
            case Key.Right when alt: active.GoForward(); break;
            case Key.Up when alt: active.GoUp(); break;
            case Key.Tab when ctrl: active.NextTab(shift ? -1 : 1); break;
            case Key.Tab when !Settings.SinglePane: SetActive(Other); active.FocusList(); break;
            case Key.T when ctrl && shift: if (ClosedTabs.Count > 0) active.NewTab(ClosedTabs.Pop()); break;
            case Key.T when ctrl: active.NewTab(active.Dir); break;
            case Key.W when ctrl: active.CloseTab(); break;
            case Key.L when ctrl: active.EditPath(); break;
            case Key.A when ctrl: active.SelectAll(); break;
            case Key.C when ctrl && shift: CopyText(string.Join("\n", active.Selected.Select(x => x.Path))); break;
            case Key.C when ctrl: ClipboardPut(cut: false); break;
            case Key.X when ctrl: ClipboardPut(cut: true); break;
            case Key.V when ctrl: ClipboardPaste(active.Dir); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.H when ctrl: ToggleHidden(); break;
            case Key.D when ctrl: Pin(); break;
            case Key.B when ctrl: if (active.Selected.Count > 0) AddToBin(active.Selected.Select(x => x.Path)); break;
            case Key.Add when !ctrl: SelectByPattern(true); break; // the keypad's + and -, as on Windows: the others type into the filter
            case Key.Subtract when !ctrl: SelectByPattern(false); break;
            case Key.P when ctrl: TogglePanes(); break;
            case Key.P when alt: TogglePreview(); break;
            case Key.R when ctrl: SizeCache.Clear(); active.Refresh(keepSelection: true); break;
            case Key.Space: if (active.Current is { IsUp: false } q) ShowQuickView(q); break;
            case Key.OemComma when ctrl: Dialog(() => new SettingsWindow()); break;
            case Key.Oem3 when ctrl: Shell.Terminal(active.Dir); break;
            case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 when ctrl && shift:
                active.SetView(new[] { ViewMode.Details, ViewMode.List, ViewMode.IconsM, ViewMode.IconsL, ViewMode.IconsXL }[e.Key - Key.D1]); break;
            case Key.Escape when active.InSearch || active.Filtered: active.ClearFilter(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // letters typed anywhere land in the active pane's filter, as on Windows (1.0.68)
    void OnTextInput(object s, TextInputEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox || string.IsNullOrEmpty(e.Text) || e.Text == " " || e.Text.Any(char.IsControl)) return;
        active.TypeInFilter(e.Text);
        e.Handled = true;
    }

    void ToggleHidden() { Settings.ShowHidden = !Settings.ShowHidden; Left.Refresh(keepSelection: true); Right.Refresh(keepSelection: true); Say(Settings.ShowHidden ? "Showing hidden files" : "Hiding hidden files"); }
    void TogglePanes() { Settings.SinglePane = !Settings.SinglePane; ApplyPanes(); }
    void TogglePreview() { Settings.Preview = !Settings.Preview; ApplyPanes(); if (Settings.Preview) ShowPreview(); }

    void Pin()
    {
        var dirs = active.Selected.Where(x => x.IsDir).Select(x => x.Path).ToList();
        if (dirs.Count == 0) dirs.Add(active.Dir);
        foreach (var d in dirs) if (!Settings.QuickAccess.Contains(d)) Settings.QuickAccess.Add(d);
        Settings.Save();
        BuildSidebar();
        Say(dirs.Count == 1 ? $"Pinned {Path.GetFileName(dirs[0])} to {Settings.CurrentQuickList.Name}" : $"Pinned {dirs.Count} folders to {Settings.CurrentQuickList.Name}");
    }

    void ShowHistory(Control at)
    {
        var menu = new ContextMenu();
        if (ClosedTabs.Count > 0)
        {
            var reopen = new MenuItem { Header = "Reopen closed tab", InputGesture = new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift), Icon = new TextBlock { Text = Glyph.Undo, Classes = { "icon" } } };
            reopen.Click += (_, _) => active.NewTab(ClosedTabs.Pop());
            menu.Items.Add(reopen);
            menu.Items.Add(new Separator());
        }
        foreach (var d in Settings.RecentFolders.Where(Directory.Exists).Take(15))
        {
            var dir = d;
            var mi = new MenuItem { Header = dir, Icon = new TextBlock { Text = Glyph.Folder, Classes = { "icon" }, Foreground = F.Brush("#F5C451") } };
            mi.Click += (_, _) => active.Navigate(dir);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent folders yet", IsEnabled = false });
        menu.Open(at);
    }

    // ---- the cards under the panes ----
    void UpdateStats()
    {
        if (active == null) return;
        var m = Mounts.MountOf(active.Dir);
        var (total, free) = m.Space;
        StatDrive.Text = m.Name;
        StatFs.Text = $"  ·  {m.Type}";
        StatFree.Text = total > 0 ? F.Size(free) : "—";
        StatUsed.Text = total > 0 ? $"  ·  {F.Size(total - free)} of {F.Size(total)} used" : "";
        double used = total > 0 ? 1 - (double)free / total : 0;
        DrivePct.Text = total > 0 ? $"{used * 100:0}% full" : "";
        DriveIcon.Text = m.Network ? Glyph.NetworkDrive : m.Removable ? Glyph.Usb : Glyph.Drive;
        if (DriveFill.Parent is Grid g) DriveFill.Width = g.Bounds.Width * used;
        DriveFill.Background = used > 0.9 ? F.Brush("#F87171") : (IBrush)Application.Current!.FindResource("Accent");

        var all = active.Items.Where(e => !e.IsUp).ToList();
        long bytes = all.Where(e => e.Size > 0).Sum(e => e.Size);
        StatFolder.Text = F.Size(bytes) + (all.Any(e => e.IsDir && e.Size == -1) ? "…" : "");
        int dirs = all.Count(e => e.IsDir), files = all.Count - dirs;
        StatFolderSub.Text = $"{dirs:N0} folder{(dirs == 1 ? "" : "s")} · {files:N0} file{(files == 1 ? "" : "s")}";
        // what the folder is made of, by size: one band per kind, in its colour
        KindBar.Children.Clear(); KindBar.ColumnDefinitions.Clear();
        if (bytes > 0)
        {
            int col = 0;
            foreach (var k in all.Where(e => e.Size > 0).GroupBy(e => e.KindColor).OrderByDescending(x => x.Sum(e => e.Size)))
            {
                KindBar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(k.Sum(e => e.Size), GridUnitType.Star)));
                var band = new Border { Background = F.Brush(k.Key) };
                Grid.SetColumn(band, col++);
                KindBar.Children.Add(band);
            }
        }

        var sel = active.Selected;
        if (sel.Count > 0)
        {
            long sb = sel.Where(e => e.Size > 0).Sum(e => e.Size);
            SelTitle.Text = "Selected";
            SelValue.Text = F.Size(sb);
            SelSub.Text = sel.Count == 1 ? sel[0].Name : $"{sel.Count:N0} items";
            SelIcon.Text = Glyph.Selection;
        }
        else if (all.Count > 0)
        {
            var newest = all.MaxBy(e => e.Modified);
            SelTitle.Text = "Latest change";
            SelValue.Text = newest.Age;
            SelSub.Text = newest.Name;
            SelIcon.Text = Glyph.Clock;
        }
        else { SelTitle.Text = ""; SelValue.Text = ""; SelSub.Text = ""; }
    }

    // ---- file operations ----
    readonly List<Job> jobs = new();
    readonly DispatcherTimer jobTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    TransfersWindow transfers;
    static readonly string[] Hues = { "#60A5FA", "#F472B6", "#34D399", "#FBBF24", "#A78BFA", "#FB923C", "#38BDF8", "#F87171" };

    void Transfer(bool move)
    {
        if (Settings.SinglePane) { Say("Two panes are needed (Ctrl+P)"); return; }
        var sel = active.Selected;
        if (sel.Count == 0) { Say("Nothing selected"); return; }
        Submit(sel.Select(x => x.Path).ToList(), Other.Dir, move);
    }

    public void Submit(List<string> files, string into, bool move, IReadOnlyDictionary<string, string> names = null, bool journal = true)
    {
        if (files.Count == 0) return;
        var j = Ferry.Submit(files, into, move, names, journal ? Path.Combine(Settings.Dir, "journal", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt") : null);
        j.Hue = F.Brush(Hues[jobs.Count % Hues.Length]);
        jobs.Insert(0, j);
        Settings.AddRecent(Settings.RecentDestinations, into, Settings.DestinationsMax);
        jobTimer.Start();
        if (Settings.TransfersShow == "window") ShowTransfers();
        RebuildBottom();
        UpdateLamp();
    }

    readonly HashSet<Job> askedForward = new();
    bool transfersFailed;
    int lampTick;

    /// The Transfers button's lamp: the accent while something runs, breathing; red when a transfer failed while nothing
    /// showed it, until the window is opened; off otherwise. The bar under it: bytes done of all that runs.
    void UpdateLamp()
    {
        bool busy = jobs.Any(j => j.Active);
        long total = 0, done = 0;
        foreach (var j in jobs) if (j.Active) { total += j.TotalBytes; done += j.DoneBytes; }
        transfersBar.IsVisible = busy;
        transfersBar.Value = total > 0 ? Math.Clamp(done / (double)total, 0, 1) : 0;
        Color? glow = transfersFailed ? Color.Parse("#E54848") : busy ? Color.Parse(Settings.Accent) : null;
        transfersLamp.Background = glow is Color g ? new SolidColorBrush(Color.FromArgb(0x48, g.R, g.G, g.B)) : Brushes.Transparent;
        transfersLamp.BorderBrush = glow is Color e ? new SolidColorBrush(e) : Brushes.Transparent;
        lampTick++;
        transfersLamp.Opacity = busy && !transfersFailed && Settings.Animations && lampTick / 9 % 2 == 1 ? 0.55 : 1;
    }

    /// Settings → Transfers "Under the panes": a card per job below the file lists.
    void RebuildBottom()
    {
        BottomJobs.Children.Clear();
        bool show = Settings.TransfersShow == "bottom" && jobs.Count > 0;
        BottomTransfers.IsVisible = show;
        if (show) foreach (var j in jobs) BottomJobs.Children.Add(TransfersWindow.Card(j, this, RebuildBottom));
    }

    /// What Settings changed that the window shows: the copy key's label, the sidebar, the age badges, where transfers go.
    public void ApplyOptions()
    {
        copyKey.Text = Settings.F5Copies ? "F5" : "Ctrl+F5";
        BuildSidebar();
        Left.Refresh(keepSelection: true); Right.Refresh(keepSelection: true);
        if (Settings.TransfersShow != "window") transfers?.Close();
        RebuildBottom();
        UpdateLamp();
    }

    /// What Settings → Appearance can leave out of the sidebar: its sections, then the places in Favorites.
    public static readonly (string Key, string Title)[] SidebarParts =
    {
        ("quick", "Quick access"), ("favorites", "Favorites"), ("drives", "Drives"), ("folders", "Folders"),
        ("Home", "Home"), ("Desktop", "Desktop"), ("Documents", "Documents"), ("Downloads", "Downloads"), ("Pictures", "Pictures"),
        ("Music", "Music"), ("Videos", "Videos"), ("Trash", "Trash"),
    };

    void ShowTransfers()
    {
        transfersFailed = false; // seen now
        if (transfers == null || !transfers.IsVisible)
        {
            transfers = new TransfersWindow(jobs, this);
            transfers.Show(this);
        }
        else transfers.Rebuild();
        transfers.Activate();
    }

    void JobFinished(Job job)
    {
        job.Refresh();
        if (job.Journal != null) try { File.Delete(job.Journal); } catch (IOException) { }
        var steps = new List<UndoStep>();
        foreach (var (from, to, moved, stamp) in job.Completed)
            steps.Add(moved ? new UndoStep("move", to, from, stamp) : new UndoStep("trash", to, null, stamp));
        lock (job.CreatedDirs) foreach (var d in job.CreatedDirs) steps.Add(new UndoStep("rmdir", d, null, 0));
        if (steps.Count > 0)
        {
            int n = job.Completed.Count;
            var entry = Remember($"{(job.IsMove ? "move" : "copy")} of {(job.Sources.Count == 1 ? Path.GetFileName(job.Sources[0].TrimEnd('/')) : $"{n:N0} items")}", steps.ToArray());
            jobUndo[job] = entry;
            job.CanUndo = true;
        }
        if (!jobs.Any(j => j.Active)) jobTimer.Stop();
        Left.Refresh(keepSelection: true); Right.Refresh(keepSelection: true);
        // what was pasted or dropped is selected where it landed
        var target = new[] { Left, Right }.FirstOrDefault(p => p.Dir == job.Destination);
        if (target != null && job.Phase == Phase.Done) Dispatcher.UIThread.Post(() => target.SelectNames(job.Completed.Select(c => Path.GetRelativePath(job.Destination, c.To).Split('/')[0]).Distinct()), DispatcherPriority.Background);
        Say(job.Title);
        transfers?.Rebuild();
        // a failure nobody saw: under the panes it is on the card, in an open window on screen
        if ((!job.Errors.IsEmpty || job.FailedFiles > 0) && Settings.TransfersShow != "bottom" && transfers?.IsVisible != true) transfersFailed = true;
        // the window goes away a moment after everything ended well (Settings → Transfers, window mode)
        if (Settings.TransfersShow == "window" && Settings.CloseTransfersWhenDone && job.Errors.IsEmpty && job.FailedFiles == 0 && job.Phase == Phase.Done)
            DispatcherTimer.RunOnce(() => { if (!jobs.Any(j => j.Active)) transfers?.Close(); }, TimeSpan.FromSeconds(1.5));
        UpdateLamp();
    }

    public void DismissJob(Job j) { jobs.Remove(j); askedForward.Remove(j); transfers?.Rebuild(); if (jobs.Count == 0) transfers?.Close(); RebuildBottom(); }
    public void UndoJob(Job j) { if (jobUndo.TryGetValue(j, out var e)) Undo(e); }

    async void Delete(bool permanent)
    {
        var sel = active.Selected;
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"these {sel.Count:N0} items";
        if (permanent && !await Dialogs.Confirm(this, "Delete for good", $"Delete {what} for good? They don't go to the Trash, so this can't be undone.")) return;
        var paths = sel.Select(x => x.Path).ToList();
        var steps = new List<UndoStep>();
        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var p in paths)
                try
                {
                    if (permanent) { if (Directory.Exists(p) && !Trash.IsLink(p)) Directory.Delete(p, true); else File.Delete(p); }
                    else steps.Add(new UndoStep("restore", Trash.Put(p), p, 0));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(p)}: {ex.Message}"); }
        });
        if (steps.Count > 0) Remember($"delete of {(steps.Count == 1 ? Path.GetFileName(paths[0]) : $"{steps.Count:N0} items")}", steps.ToArray());
        active.Refresh(keepSelection: false);
        if (Other.Dir == active.Dir) Other.Refresh(keepSelection: true);
        Say(errors.Count > 0 ? errors[0] : permanent ? $"Deleted {what}" : $"Moved {what} to the Trash · Ctrl+Z puts it back");
    }

    async void NewFolder()
    {
        var name = await Dialogs.Ask(this, "New folder", $"A new folder in {active.Dir}", "New folder");
        if (name == null) return;
        var path = Path.Combine(active.Dir, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) { Say($"{name} is already there"); return; }
            Directory.CreateDirectory(path);
            Remember($"new folder {name}", new UndoStep("trash", path, null, Ferry.Stamp(path)));
            active.Refresh(select: name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
    }

    async void NewFile()
    {
        var name = await Dialogs.Ask(this, "New file", $"A new, empty file in {active.Dir}", "New file.txt", selectStem: true);
        if (name == null) return;
        var path = Path.Combine(active.Dir, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) { Say($"{name} is already there"); return; }
            using (File.Create(path)) { }
            Remember($"new file {name}", new UndoStep("trash", path, null, Ferry.Stamp(path)));
            active.Refresh(select: name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
    }

    void Rename(PaneView pane, Entry e, string name)
    {
        if (name.Contains('/')) { Say("A name can't contain /"); return; }
        var to = Path.Combine(Path.GetDirectoryName(e.Path) ?? "/", name);
        try
        {
            if ((File.Exists(to) || Directory.Exists(to)) && !string.Equals(to, e.Path, StringComparison.OrdinalIgnoreCase)) { Say($"{name} is already there"); return; }
            if (e.IsDir && !e.IsLink) Directory.Move(e.Path, to); else File.Move(e.Path, to);
            Remember($"rename to {name}", new UndoStep("rename", to, e.Name, Ferry.Stamp(to)));
            pane.Refresh(select: name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
    }

    async void Properties()
    {
        var sel = active.Selected;
        if (sel.Count == 0) return;
        var lines = new List<string>();
        if (sel.Count == 1)
        {
            var e = sel[0];
            lines.Add(e.Path);
            lines.Add($"Type: {F.TypeName(e.Name, e.IsDir)}{(e.IsLink ? " (link)" : "")}");
            if (e.Size >= 0) lines.Add($"Size: {F.Size(e.Size)} ({e.Size:N0} bytes)");
            lines.Add($"Modified: {e.Modified:yyyy-MM-dd HH:mm:ss}");
            try { lines.Add($"Permissions: {File.GetUnixFileMode(e.Path)}"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (e.IsLink) try { lines.Add($"Points to: {new FileInfo(e.Path).LinkTarget}"); } catch (IOException) { }
        }
        else
        {
            lines.Add($"{sel.Count:N0} items in {active.Dir}");
            lines.Add($"Size: {F.Size(sel.Where(e => e.Size > 0).Sum(e => e.Size))}");
        }
        await Dialogs.Info(this, "Properties", string.Join("\n", lines));
    }

    // ---- clipboard: the desktop's own formats, so Dolphin and Nautilus paste what File Labs copied, and back ----
    static readonly DataFormat<string> GnomeFiles = DataFormat.CreateStringPlatformFormat("x-special/gnome-copied-files");
    static readonly DataFormat<string> UriList = DataFormat.CreateStringPlatformFormat("text/uri-list");
    static readonly DataFormat<string> KdeCut = DataFormat.CreateStringPlatformFormat("application/x-kde-cutselection");
    List<string> cutPaths = new();

    async void ClipboardPut(bool cut)
    {
        var paths = active.Selected.Select(x => x.Path).ToList();
        if (paths.Count == 0 || Clipboard == null) return;
        var uris = string.Join("\r\n", paths.Select(p => new Uri(p).AbsoluteUri));
        var item = new DataTransferItem();
        item.Set(UriList, uris + "\r\n");
        item.Set(GnomeFiles, (cut ? "cut\n" : "copy\n") + string.Join("\n", paths.Select(p => new Uri(p).AbsoluteUri)));
        item.Set(KdeCut, cut ? "1" : "0");
        item.Set(DataFormat.Text, string.Join("\n", paths));
        var data = new DataTransfer(); data.Add(item);
        await Clipboard.SetDataAsync(data);
        foreach (var e in Left.Items.Concat(Right.Items)) e.IsCut = false;
        cutPaths = cut ? paths : new();
        if (cut) foreach (var e in active.Selected) e.IsCut = true;
        Say(cut ? $"Cut {paths.Count:N0} item(s) · Ctrl+V moves them" : $"Copied {paths.Count:N0} item(s)");
    }

    async void ClipboardPaste(string into)
    {
        if (Clipboard == null) return;
        bool cut = false; List<string> paths = null;
        try
        {
            var gnome = await Clipboard.TryGetValueAsync(GnomeFiles);
            if (!string.IsNullOrEmpty(gnome))
            {
                var lines = gnome.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                cut = lines[0].Trim() == "cut";
                paths = lines.Skip(1).Select(l => new Uri(l.Trim()).LocalPath).ToList();
            }
            if (paths == null)
            {
                var files = await Clipboard.TryGetFilesAsync();
                paths = files?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
                var kde = await Clipboard.TryGetValueAsync(KdeCut);
                cut = kde?.Trim() == "1";
            }
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException) { paths = null; }
        if (paths is not { Count: > 0 }) { Say("Nothing to paste"); return; }
        Submit(paths, into, cut);
        if (cut) { await Clipboard.ClearAsync(); cutPaths.Clear(); }
    }

    async void CopyText(string text) { if (Clipboard != null) { await Clipboard.SetTextAsync(text); Say("Path copied"); } }

    // ---- Undo: each operation remembers how to take itself back ----
    public record UndoStep(string Kind, string Path, string To, long Stamp);
    public record UndoEntry(string What, UndoStep[] Steps);
    readonly List<UndoEntry> undo = new();
    readonly Dictionary<Job, UndoEntry> jobUndo = new();
    UndoEntry Remember(string what, params UndoStep[] steps) { var e = new UndoEntry(what, steps); undo.Add(e); if (undo.Count > 50) undo.RemoveAt(0); return e; }

    void Undo() => Undo(undo.Count > 0 ? undo[^1] : null);

    async void Undo(UndoEntry entry)
    {
        if (entry == null || !undo.Contains(entry)) { Say("Nothing to undo"); return; }
        undo.Remove(entry);
        foreach (var (j, e) in jobUndo.Where(x => x.Value == entry).ToList()) { j.CanUndo = false; jobUndo.Remove(j); }
        Say($"Undoing {entry.What}…");
        int changed = 0; string error = null;
        var moves = new List<UndoStep>();
        await Task.Run(() =>
        {
            foreach (var s in entry.Steps)
            {
                bool exists = File.Exists(s.Path) || Directory.Exists(s.Path) || Trash.IsLink(s.Path);
                // something put there since is left alone
                if (s.Stamp != 0 && exists && Ferry.Stamp(s.Path) != s.Stamp && s.Kind != "rmdir") { changed++; continue; }
                try
                {
                    switch (s.Kind)
                    {
                        case "rename" when exists: var back = Path.Combine(Path.GetDirectoryName(s.Path)!, s.To); if (Directory.Exists(s.Path) && !Trash.IsLink(s.Path)) Directory.Move(s.Path, back); else File.Move(s.Path, back); break;
                        case "restore": Trash.Restore(s.Path); break;
                        case "trash" when exists: Trash.Put(s.Path); break;
                        case "move" when exists: lock (moves) moves.Add(s); break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
            }
            foreach (var s in entry.Steps.Where(x => x.Kind == "rmdir").OrderByDescending(x => x.Path.Length))
                try { if (Directory.Exists(s.Path) && !Directory.EnumerateFileSystemEntries(s.Path).Any()) Directory.Delete(s.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        // moves go back through Ferry, each to its exact old place and name
        foreach (var g in moves.GroupBy(m => Path.GetDirectoryName(m.To)))
            Submit(g.Select(m => m.Path).ToList(), g.Key!, move: true, g.ToDictionary(m => m.Path, m => Path.GetFileName(m.To)), journal: false);
        Left.Refresh(keepSelection: true); Right.Refresh(keepSelection: true);
        Say(error != null ? $"Partly undone: {error}" : $"Undone: {entry.What}" + (changed > 0 ? $" ({changed} changed since, left alone)" : ""));
    }

    // ---- right-click ----
    void ShowContextMenu(PaneView p, Entry row)
    {
        SetActive(p);
        var sel = p.Selected;
        var menu = new ContextMenu();
        MenuItem Item(string header, string glyph, string gesture, Action run, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 14 } };
            // a key Avalonia can name shows as the gesture; others ("Num +") as text, never a crash
            if (gesture != null) { try { mi.InputGesture = KeyGesture.Parse(gesture); } catch (ArgumentException) { mi.Header = $"{header}   ({gesture})"; } }
            mi.Click += (_, _) => run();
            return mi;
        }
        if (sel.Count == 0)
        {
            menu.Items.Add(Item("Paste", Glyph.Paste, "Ctrl+V", () => ClipboardPaste(p.Dir)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("New folder", Glyph.NewFolder, "F7", NewFolder));
            menu.Items.Add(Item("New file", Glyph.NewFile, "Ctrl+N", NewFile));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open terminal here", Glyph.Terminal, null, () => Shell.Terminal(p.Dir)));
            menu.Items.Add(Item("Pin to Quick access", Glyph.Pin, "Ctrl+D", Pin));
            if (!Settings.SinglePane) menu.Items.Add(Item("Compare with the other pane", Glyph.Compare, "Shift+F2", ComparePanes));
            menu.Items.Add(Item("Select by pattern…", Glyph.SelectAll, "Num +", () => SelectByPattern(true)));
            menu.Items.Add(Item("Find duplicates in this folder…", Glyph.Duplicates, null, () => Open(() => new DuplicatesWindow(p.Dir))));
            menu.Items.Add(Item(Settings.ShowHidden ? "Hide hidden files" : "Show hidden files", Glyph.Eye, "Ctrl+H", ToggleHidden));
            menu.Items.Add(Item("Refresh", Glyph.Refresh, "F5", () => p.Refresh(keepSelection: true)));
            menu.Open(p);
            return;
        }
        var first = sel[0];
        bool one = sel.Count == 1, dir = one && first.IsDir;
        menu.Items.Add(Item("Open", Glyph.OpenWith, "Enter", p.OpenSelected));
        if (sel.All(x => !x.IsDir)) menu.Items.Add(OpenWithMenu(sel));
        if (dir) menu.Items.Add(Item("Open in new tab", Glyph.NewTab, null, () => p.NewTab(first.Path)));
        if (dir && !Settings.SinglePane) menu.Items.Add(Item("Open in other pane", Glyph.OtherPane, null, () => Other.Navigate(first.Path)));
        if (one && !first.IsDir) menu.Items.Add(Item("Quick View", Glyph.Eye, "Space", () => ShowQuickView(first)));
        if (sel.Any(x => !x.IsDir)) menu.Items.Add(Item("Edit", Glyph.Rename, "F4", () => { foreach (var f in sel.Where(x => !x.IsDir)) new EditorWindow(f.Path).Show(); }));
        if (p.InSearch && one) menu.Items.Add(Item("Open file location", Glyph.FolderOpen, null, () => { p.ClearFilter(); p.Navigate(Path.GetDirectoryName(first.Path), first.Name); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cut", Glyph.Cut, "Ctrl+X", () => ClipboardPut(true)));
        menu.Items.Add(Item("Copy", Glyph.Copy, "Ctrl+C", () => ClipboardPut(false)));
        if (dir) menu.Items.Add(Item("Paste into folder", Glyph.Paste, null, () => ClipboardPaste(first.Path)));
        menu.Items.Add(Item("Copy path", Glyph.Link, "Ctrl+Shift+C", () => CopyText(string.Join("\n", sel.Select(x => x.Path)))));
        menu.Items.Add(Item("Add to Drop bin", Glyph.Bin, "Ctrl+B", () => AddToBin(sel.Select(x => x.Path))));
        if (!Settings.SinglePane)
        {
            menu.Items.Add(Item("Copy to other pane", Glyph.Copy, "Ctrl+F5", () => Transfer(false)));
            menu.Items.Add(Item("Move to other pane", Glyph.Move, "F6", () => Transfer(true)));
        }
        menu.Items.Add(new Separator());
        if (one) menu.Items.Add(Item("Rename", Glyph.Rename, "F2", p.BeginRename));
        if (sel.Count > 1) menu.Items.Add(Item("Rename all…", Glyph.Rename, null, () => Dialog(() => new MultiRenameWindow(p, sel))));
        menu.Items.Add(Item("Move to Trash", Glyph.Delete, "Delete", () => Delete(false)));
        menu.Items.Add(Item("Delete for good", Glyph.EmptyBin, "Shift+Delete", () => Delete(true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Compress to ZIP", Glyph.Zip, null, () => Compress(p, sel)));
        if (one && Archives.CanExtract(first.Path)) menu.Items.Add(Item("Extract here", Glyph.FolderOpen, null, () => Extract(p, first)));
        if (one && !first.IsDir) menu.Items.Add(Item("Checksum (SHA-256)", Glyph.Check, null, () => Checksum(first)));
        if (sel.Any(x => x.IsDir)) menu.Items.Add(Item("Pin to Quick access", Glyph.Pin, "Ctrl+D", Pin));
        if (dir) menu.Items.Add(Item("Find duplicates in folder…", Glyph.Duplicates, null, () => Open(() => new DuplicatesWindow(first.Path))));
        menu.Items.Add(Item("Open terminal here", Glyph.Terminal, null, () => Shell.Terminal(dir ? first.Path : p.Dir)));
        menu.Items.Add(Item("Properties", Glyph.Info, "Alt+Enter", Properties));
        menu.Open(p);
    }

    async void Compress(PaneView p, List<Entry> sel)
    {
        var name = sel.Count == 1 ? Path.GetFileNameWithoutExtension(sel[0].Name) + ".zip" : Path.GetFileName(p.Dir) + ".zip";
        var target = Archives.Unique(Path.Combine(p.Dir, name));
        Say($"Compressing to {Path.GetFileName(target)}…");
        try { await Task.Run(() => Archives.Zip(sel.Select(x => x.Path).ToList(), target)); Remember($"zip {Path.GetFileName(target)}", new UndoStep("trash", target, null, Ferry.Stamp(target))); p.Refresh(select: Path.GetFileName(target)); Say($"Made {Path.GetFileName(target)}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
    }

    async void Extract(PaneView p, Entry e)
    {
        var target = Archives.Unique(Path.Combine(p.Dir, Archives.Stem(e.Name)));
        Say($"Extracting {e.Name}…");
        try { await Task.Run(() => Archives.Extract(e.Path, target)); Remember($"extract {e.Name}", new UndoStep("trash", target, null, 0)); p.Refresh(select: Path.GetFileName(target)); Say($"Extracted to {Path.GetFileName(target)}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Say(ex.Message); }
    }

    async void Checksum(Entry e)
    {
        Say($"Reading {e.Name}…");
        var hash = await Task.Run(() => { using var s = File.OpenRead(e.Path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant(); });
        CopyText(hash);
        await Dialogs.Info(this, "SHA-256", $"{e.Name}\n\n{hash}\n\n(copied)");
    }

    // ---- Quick View and the preview pane ----
    public void ShowQuickView(Entry e) => Open(() => new QuickViewWindow(active, e));

    void ShowPreview()
    {
        if (!Settings.Preview || active == null) return;
        var e = active.Current;
        PreviewTitle.Text = e == null || e.IsUp ? "" : $"{e.Name}  ·  {e.SizeText}";
        PreviewBody.Content = e == null || e.IsUp ? null : QuickViewWindow.Body(e, small: true);
    }
}
