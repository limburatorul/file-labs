using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileExplorer;

public class Entry : INotifyPropertyChanged
{
    public string Name { get; init; }
    public string Path { get; init; }
    public bool IsDir { get; init; }
    public DateTime Modified { get; init; }
    public bool IsUp => Name == "..";

    long size = -1; // -1 = folder size still computing
    public long Size { get => size; set { size = value; Changed(nameof(Size)); Changed(nameof(SizeText)); } }
    ImageSource thumb;
    public ImageSource Thumb { get => thumb; set { thumb = value; Changed(nameof(Thumb)); Changed(nameof(NoThumb)); } }
    public bool NoThumb => thumb == null;
    ImageSource smallIcon;
    public ImageSource SmallIcon { get => smallIcon; set { smallIcon = value; Changed(nameof(SmallIcon)); Changed(nameof(NoSmallIcon)); } }
    public bool NoSmallIcon => smallIcon == null;
    bool isCut;
    public bool IsCut { get => isCut; set { if (isCut != value) { isCut = value; Changed(nameof(IsCut)); } } }
    string version = "";
    public string Version { get => version; set { version = value; Changed(nameof(Version)); } }
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string SizeText => IsUp ? "" : size < 0 ? "…" : MainWindow.Fmt(size);

    public event PropertyChangedEventHandler PropertyChanged;
    void Changed(string p) => PropertyChanged?.Invoke(this, new(p));

    static readonly Dictionary<string, (string, string)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = ("", "#F472B6"), [".jpeg"] = ("", "#F472B6"), [".png"] = ("", "#F472B6"),
        [".gif"] = ("", "#F472B6"), [".webp"] = ("", "#F472B6"), [".svg"] = ("", "#F472B6"),
        [".mp4"] = ("", "#FB923C"), [".mkv"] = ("", "#FB923C"), [".avi"] = ("", "#FB923C"), [".mov"] = ("", "#FB923C"),
        [".mp3"] = ("", "#A78BFA"), [".flac"] = ("", "#A78BFA"), [".wav"] = ("", "#A78BFA"),
        [".zip"] = ("", "#FACC15"), [".rar"] = ("", "#FACC15"), [".7z"] = ("", "#FACC15"), [".iso"] = ("", "#FACC15"),
        [".exe"] = ("", "#34D399"), [".msi"] = ("", "#34D399"), [".bat"] = ("", "#34D399"), [".ps1"] = ("", "#34D399"),
        [".cs"] = ("", "#38BDF8"), [".js"] = ("", "#38BDF8"), [".ts"] = ("", "#38BDF8"), [".py"] = ("", "#38BDF8"),
        [".json"] = ("", "#38BDF8"), [".xml"] = ("", "#38BDF8"), [".html"] = ("", "#38BDF8"), [".xaml"] = ("", "#38BDF8"),
        [".pdf"] = ("", "#F87171"),
    };
    (string icon, string color) Kind => IsUp ? ("", "#9CA3AF") : IsDir ? ("", "#60A5FA")
        : Kinds.TryGetValue(System.IO.Path.GetExtension(Name), out var k) ? k : ("", "#9CA3AF");
    public string Icon => Kind.icon;
    public Brush IconBrush => MainWindow.Hex(Kind.color);

    // Recency at a glance, OneCommander-style: hot red for minutes, fading through
    // orange/yellow/teal to neutral grey as the item gets older (log time scale).
    static readonly (double hours, Color c)[] Stops =
    {
        (0, Color.FromRgb(0xFF, 0x4D, 0x5E)), (1, Color.FromRgb(0xFF, 0x8A, 0x3D)),
        (24, Color.FromRgb(0xFF, 0xD2, 0x3F)), (24 * 7, Color.FromRgb(0x3D, 0xDC, 0xC8)),
        (24 * 30, Color.FromRgb(0x5B, 0x8D, 0xB8)), (24 * 365, Color.FromRgb(0x4A, 0x4A, 0x4A)),
    };
    Color AgeColor
    {
        get
        {
            double h = Math.Max(0, (DateTime.Now - Modified).TotalHours), x = Math.Log(h + 1);
            for (int i = 1; i < Stops.Length; i++)
            {
                double a = Math.Log(Stops[i - 1].hours + 1), b = Math.Log(Stops[i].hours + 1);
                if (x > b) continue;
                double t = (x - a) / (b - a);
                Color p = Stops[i - 1].c, q = Stops[i].c;
                return Color.FromRgb((byte)(p.R + (q.R - p.R) * t), (byte)(p.G + (q.G - p.G) * t), (byte)(p.B + (q.B - p.B) * t));
            }
            return Stops[^1].c;
        }
    }
    public Brush AgeBrush => IsUp ? Brushes.Transparent : new SolidColorBrush(AgeColor);
    public Brush AgeTextBrush
    {
        get { var c = AgeColor; return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140 ? MainWindow.Hex("#E6111111") : MainWindow.Hex("#E6FFFFFF"); }
    }
    public string Age
    {
        get
        {
            if (IsUp) return "";
            var d = DateTime.Now - Modified;
            return d.TotalHours < 1 ? $"{Math.Max(0, (int)d.TotalMinutes)}'"
                : d.TotalDays < 1 ? $"{(int)d.TotalHours}h"
                : d.TotalDays < 60 ? $"{(int)d.TotalDays}d"
                : d.TotalDays < 730 ? $"{(int)(d.TotalDays / 30)}mo"
                : $"{(int)(d.TotalDays / 365)}y";
        }
    }
    public string DateText => IsUp ? "" : Modified.ToString("yyyy-MM-dd  HH:mm");
    public string FullDate => IsUp ? null : Modified.ToString("dddd, yyyy-MM-dd HH:mm:ss");
}

// Folders first, ".." always on top, then by the chosen column.
class EntrySort(string key, bool desc) : IComparer
{
    public int Compare(object a, object b)
    {
        var x = (Entry)a; var y = (Entry)b;
        if (x.IsUp != y.IsUp) return x.IsUp ? -1 : 1;
        if (x.IsDir != y.IsDir) return x.IsDir ? -1 : 1;
        int r = key switch
        {
            "Size" => x.Size.CompareTo(y.Size),
            "Type" => StringComparer.OrdinalIgnoreCase.Compare(x.Ext, y.Ext) is var t && t != 0 ? t : StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name),
            "Modified" => x.Modified.CompareTo(y.Modified),
            _ => StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name),
        };
        return desc ? -r : r;
    }
}

public partial class PaneView : UserControl
{
    public static bool ShowHidden;
    static readonly HashSet<string> CutPaths = new(StringComparer.OrdinalIgnoreCase);

    // Marks exactly these paths as cut in every pane; an empty list clears the mark.
    public static void SetCut(IEnumerable<string> paths, params PaneView[] panes)
    {
        CutPaths.Clear();
        CutPaths.UnionWith(paths);
        foreach (var p in panes)
            foreach (var e in p.items) e.IsCut = CutPaths.Contains(e.Path);
    }

    public string Dir => tabs[tab];
    public event Action Activated, StatsChanged;
    public event Action<string> Error;
    public event Action<List<string>, string, bool> FilesDropped; // sources, destination, move

    readonly List<string> tabs = new() { "" };
    int tab;
    readonly Stack<string> back = new(), fwd = new();
    List<Entry> items = new();
    ListCollectionView view;
    string sortKey = "Name"; bool sortDesc;
    CancellationTokenSource sizing;
    FileSystemWatcher watcher;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public PaneView()
    {
        InitializeComponent();
        details = List.View;
        ShowSortArrow();
        Loaded += (_, _) => SetView(Settings.Views.TryGetValue(Name, out var v) ? v : ViewMode.Details);
        List.GotKeyboardFocus += (_, _) => Activated?.Invoke();
        PreviewMouseDown += (_, _) => Activated?.Invoke(); // anywhere in the pane: tabs, path, list
        List.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: Entry }) OpenSelected(); };
        List.SelectionChanged += (_, _) => { UpdateFooter(); StatsChanged?.Invoke(); };
        List.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        List.AllowDrop = true;
        List.PreviewMouseLeftButtonDown += Drag_Down;
        List.PreviewMouseLeftButtonUp += Drag_Up;
        List.PreviewMouseMove += Drag_Move;
        List.DragOver += Drop_Over;
        List.Drop += Drop_Drop;
        debounce.Tick += (_, _) => { debounce.Stop(); Refresh(); };

        // Name fills whatever the other columns leave: shrinks with the pane, and absorbs
        // width when you resize another column. Dragging Name itself sticks until the pane resizes.
        List.SizeChanged += (_, _) => { if (Mode == ViewMode.Details) FitName(); };
        var widthProp = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var c in ((GridView)List.View).Columns.Where(c => c != NameColumn))
        {
            // widths survive restarts (saved with the other settings on exit)
            if (Settings.ColumnWidths.TryGetValue(colKey[c], out var w)) c.Width = w;
            widthProp.AddValueChanged(c, (_, _) => { Settings.ColumnWidths[colKey[c]] = c.Width; FitName(); });
        }
    }

    public IEnumerable<Entry> Items => items.Where(e => !e.IsUp);
    public List<Entry> Selected => List.SelectedItems.Cast<Entry>().Where(e => !e.IsUp).ToList();
    public long FolderTotal => Items.Sum(e => Math.Max(0, e.Size));
    public bool SizesPending => Items.Any(e => e.Size < 0);

    // Active pane: lighter surface and accent edge. Inactive: darker and slightly faded, so it recedes.
    public void SetActive(bool on)
    {
        Card.Background = MainWindow.Hex(on ? "#12FFFFFF" : "#0A000000");
        Card.BorderBrush = MainWindow.Hex(on ? "#664C8DFF" : "#0FFFFFFF");
        List.Opacity = on ? 1 : 0.88;
    }

    int navVersion; // a newer Navigate wins; a slow share must not overwrite the folder you moved on to

    public void Navigate(string dir, string select = null, bool record = true)
    {
        DirectoryInfo di;
        try
        {
            di = new DirectoryInfo(dir);
            if (!di.Exists) throw new DirectoryNotFoundException($"{dir} does not exist");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            Error?.Invoke(ex.Message);
            return;
        }

        if (record && tabs[tab] != "" && !tabs[tab].Equals(di.FullName, StringComparison.OrdinalIgnoreCase)) { back.Push(tabs[tab]); fwd.Clear(); }
        tabs[tab] = di.FullName;
        BuildCrumbs();
        BuildTabs();
        int v = ++navVersion;

        // Reading the folder and asking the drive for free space both block — on a network share for
        // the best part of a second — so they happen off the UI thread and the list is handed over
        // when it's ready. The window never freezes on a folder change.
        Task.Run(() =>
        {
            var list = new List<Entry>();
            string error = null;
            try
            {
                foreach (var fsi in di.EnumerateFileSystemInfos())
                {
                    if (!ShowHidden && fsi.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                    bool isDir = fsi is DirectoryInfo;
                    var e = new Entry { Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, Modified = fsi.LastWriteTime, IsCut = CutPaths.Contains(fsi.FullName) };
                    e.Size = isDir ? SizeCache.Get(fsi.FullName, fsi.LastWriteTimeUtc) : ((FileInfo)fsi).Length;
                    list.Add(e);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                error = ex.Message;
            }

            double used = -1; string driveText = null;
            try
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(di.FullName));
                if (drive.IsReady)
                {
                    used = 1 - (double)drive.AvailableFreeSpace / drive.TotalSize;
                    driveText = $"{MainWindow.Fmt(drive.AvailableFreeSpace)} free of {MainWindow.Fmt(drive.TotalSize)}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

            Dispatcher.BeginInvoke(() =>
            {
                if (v != navVersion) return; // the user has already moved on
                if (error != null) { Error?.Invoke(error); return; }
                items = list;
                view = new ListCollectionView(items) { CustomSort = new EntrySort(sortKey, sortDesc) };
                ApplyFilter();
                List.ItemsSource = view;
                List.SelectedItem = items.FirstOrDefault(e => e.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? view.Cast<Entry>().FirstOrDefault();
                if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);

                Watch();
                StartSizing();
                StartVersions();
                StartThumbs();
                StartIcons();

                if (driveText != null)
                {
                    UsedBar.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = UsedBar.Parent, Converter = new Scale(used) });
                    DriveInfoText.Text = driveText;
                }
                UpdateFooter();
                StatsChanged?.Invoke();
            });
        });
    }

    class Scale(double f) : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => (double)v * f;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    public void Refresh() => Navigate(Dir, (List.SelectedItem as Entry)?.Name, record: false);

    // Folder sizes: walk each subfolder in the background, fill cells as they finish.
    void StartSizing()
    {
        sizing?.Cancel();
        var cts = sizing = new CancellationTokenSource();
        var todo = items.Where(e => e.IsDir && !e.IsUp && e.Size < 0).ToList();
        if (todo.Count == 0) return;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Task.Run(() =>
        {
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token }, e =>
            {
                long total = 0;
                try { foreach (var f in new DirectoryInfo(e.Path).EnumerateFiles("*", opts)) { total += f.Length; cts.Token.ThrowIfCancellationRequested(); } }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                SizeCache.Set(e.Path, Directory.GetLastWriteTimeUtc(e.Path), total);
                Dispatcher.BeginInvoke(() => { e.Size = total; UpdateFooter(); StatsChanged?.Invoke(); });
            });
        }, cts.Token).ContinueWith(_ => { }); // swallow cancellation
    }

    // File versions for exe/dll (OneCommander-style column), off the UI thread: network drives can be slow.
    static readonly HashSet<string> Versioned = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys", ".ocx", ".msi" };
    void StartVersions()
    {
        var todo = items.Where(e => !e.IsDir && Versioned.Contains(System.IO.Path.GetExtension(e.Name))).ToList();
        if (todo.Count == 0) return;
        var token = sizing.Token; // same lifetime as the folder listing
        Task.Run(() =>
        {
            foreach (var e in todo)
            {
                if (token.IsCancellationRequested) return;
                string v;
                try
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(e.Path);
                    v = (fvi.FileVersion ?? fvi.ProductVersion ?? "").Split(' ')[0]; // some add " (build info)"
                }
                catch (FileNotFoundException) { continue; }
                if (v != "") Dispatcher.BeginInvoke(() => e.Version = v);
            }
        }, token);
    }

    void Watch()
    {
        watcher?.Dispose();
        try
        {
            watcher = new FileSystemWatcher(Dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
            watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { watcher = null; } // some drives can't be watched; manual refresh still works
    }

    void UpdateFooter()
    {
        var sel = Selected;
        int folders = Items.Count(e => e.IsDir), files = Items.Count() - folders;
        Footer.Text = sel.Count > 0
            ? $"{sel.Count} selected  ·  {MainWindow.Fmt(sel.Sum(e => Math.Max(0, e.Size)))}"
            : $"{folders} folders  ·  {files} files  ·  {MainWindow.Fmt(FolderTotal)}{Pending()}";

        string Pending()
        {
            int left = Items.Count(e => e.Size < 0);
            if (left == 0) return "";
            int cached = Items.Count(e => e.IsDir && e.Size >= 0);
            return cached > 0 ? $"  ·  measuring {left} folders ({cached} cached)" : $"  ·  measuring {left} folders";
        }
    }

    void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var parts = new List<DirectoryInfo>();
        for (var d = new DirectoryInfo(Dir); d != null; d = d.Parent) parts.Insert(0, d);
        foreach (var d in parts)
        {
            if (Crumbs.Children.Count > 0)
                Crumbs.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 8, Foreground = MainWindow.Hex("#66FFFFFF"), VerticalAlignment = VerticalAlignment.Center, Margin = new(1, 1, 1, 0) });
            var b = new Button { Content = d.Parent == null ? d.Name.TrimEnd('\\') : d.Name, Style = (Style)FindResource("Ghost") };
            var path = d.FullName;
            b.Click += (_, _) => { Navigate(path); List.Focus(); };
            Crumbs.Children.Add(b);
        }
    }

    // Browser-style tabs: folder icon + name + close (x), active one lifted with an accent underline.
    void BuildTabs()
    {
        TabStrip.Children.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            int idx = i;
            var name = System.IO.Path.GetFileName(tabs[i].TrimEnd((char)92));
            var close = new Button { Style = (Style)FindResource("TabClose"), ToolTip = "Close tab (Ctrl+W)" };
            if (tabs.Count == 1) close.Visibility = Visibility.Collapsed; // the last tab can't be closed
            close.Click += (_, e) => { CloseTab(idx); e.Handled = true; };
            var row = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
            row.Children.Add(MainWindow.ShellIcon(tabs[i], "\uE8B7", 16, new(0, 0, 7, 0)));
            row.Children.Add(new TextBlock { Text = name == "" ? tabs[i] : name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, Style = (Style)FindResource("BrowserTab"), Tag = i == tab ? "on" : null, Uid = i == tab - 1 ? "beforeActive" : "", ToolTip = tabs[i] };
            b.Click += (_, _) => SwitchTab(idx);
            b.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(idx); };
            TabStrip.Children.Add(b);
        }
        FitTabs();
    }

    // Each tab gets up to 170 px; when they don't all fit, they shrink evenly so the + stays visible.
    const double TabMax = 170;
    void FitTabs()
    {
        double room = TabBand.ActualWidth - 4 /* left edge */ - NewTabBox.ActualWidth - 8 /* keep some strip visible */;
        if (room > 0) TabStrip.Width = Math.Min(tabs.Count * TabMax, room);
    }
    void TabBand_SizeChanged(object s, SizeChangedEventArgs e) => FitTabs();

    public void NewTab() { tabs.Insert(tab + 1, Dir); tab++; Navigate(Dir, record: false); }
    public void CloseTab(int i = -1)
    {
        if (tabs.Count == 1) return;
        int idx = i < 0 ? tab : i;
        tabs.RemoveAt(idx);
        if (idx < tab || tab == tabs.Count) tab--;
        Navigate(Dir, record: false);
        List.Focus();
    }
    public void SwitchTab(int i) { tab = (i + tabs.Count) % tabs.Count; back.Clear(); fwd.Clear(); Navigate(Dir, record: false); List.Focus(); }
    public void NextTab(int step) => SwitchTab(tab + step);

    public void Back() { if (back.Count > 0) { fwd.Push(Dir); Navigate(back.Pop(), record: false); } }
    public void Forward() { if (fwd.Count > 0) { back.Push(Dir); Navigate(fwd.Pop(), record: false); } }
    public void Up() { if (Directory.GetParent(Dir) is { } p) Navigate(p.FullName, System.IO.Path.GetFileName(Dir)); }

    public void OpenSelected()
    {
        if (List.SelectedItem is not Entry e) return;
        if (e.IsUp) Up();
        else if (e.IsDir) Navigate(e.Path);
        else
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { Error?.Invoke(ex.Message); } // e.g. no app associated
    }

    public void EditPath()
    {
        PathBox.Text = Dir;
        PathBox.Visibility = Visibility.Visible;
        Crumbs.Visibility = Visibility.Hidden;
        PathBox.Focus(); PathBox.SelectAll();
    }
    void EndEditPath() { PathBox.Visibility = Visibility.Collapsed; Crumbs.Visibility = Visibility.Visible; }
    void Crumbs_Click(object s, MouseButtonEventArgs e) { if (e.OriginalSource is Border) EditPath(); }
    void PathBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndEditPath(); Navigate(Environment.ExpandEnvironmentVariables(PathBox.Text)); List.Focus(); }
        else if (e.Key == Key.Escape) { EndEditPath(); List.Focus(); }
    }
    void PathBox_Lost(object s, KeyboardFocusChangedEventArgs e) => EndEditPath();

    public void FocusFilter() { FilterBox.Focus(); FilterBox.SelectAll(); }
    public void FocusList() => List.Focus();
    void Filter_Changed(object s, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }
    void ApplyFilter()
    {
        if (view == null) return;
        var q = FilterBox.Text;
        view.Filter = q == "" ? null : o => o is Entry { IsUp: true } || ((Entry)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Header_Click(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col } || !colKey.TryGetValue(col, out var key)) return;
        sortDesc = key == sortKey ? !sortDesc : key != "Name"; // size/date: biggest/newest first
        sortKey = key;
        view.CustomSort = new EntrySort(sortKey, sortDesc);
        ShowSortArrow();
    }

    void FitName()
    {
        const double scrollbar = 14, padding = 14;
        var others = ((GridView)List.View).Columns.Where(c => c != NameColumn).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        NameColumn.Width = Math.Max(120, List.ActualWidth - others - scrollbar - padding);
    }

    // ---- View modes (FilePilot-style): Details, List, and M/L/XL icons with shell thumbnails ----
    public enum ViewMode { Details, List, IconsM, IconsL, IconsXL }
    static readonly (ViewMode mode, string name, string glyph, int size)[] Modes =
    {
        (ViewMode.IconsXL, "XL Icons", "\uE8B9", 192), (ViewMode.IconsL, "L Icons", "\uE8B9", 112),
        (ViewMode.IconsM, "M Icons", "\uE8B9", 64), (ViewMode.List, "List", "\uE8FD", 0), (ViewMode.Details, "Details", "\uE8EF", 0),
    };
    ViewBase details;
    public ViewMode Mode { get; private set; } = ViewMode.Details;

    public void SetView(ViewMode mode)
    {
        Mode = mode;
        var m = Modes.First(x => x.mode == mode);
        ViewIcon.Text = m.glyph; ViewName.Text = m.name;
        Settings.Views[Name] = mode;
        var sel = List.SelectedItem;
        if (mode == ViewMode.Details)
        {
            List.View = details;
            List.ClearValue(ItemsControl.ItemTemplateProperty);
            List.ClearValue(ItemsControl.ItemsPanelProperty);
            List.ClearValue(ItemsControl.ItemContainerStyleProperty);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        }
        else
        {
            List.View = null;
            bool list = mode == ViewMode.List;
            List.Tag = (double)m.size;
            List.ItemTemplate = (DataTemplate)FindResource(list ? "ListCell" : "TileCell");
            var style = new Style(typeof(ListViewItem), (Style)FindResource(list ? "ListViewItem.Plain" : "TileItem"));
            if (!list) style.Setters.Add(new Setter(WidthProperty, m.size + 28.0));
            List.ItemContainerStyle = style;
            // List flows top→bottom then sideways; icons wrap left→right then down
            var panel = new FrameworkElementFactory(typeof(WrapPanel));
            panel.SetValue(WrapPanel.OrientationProperty, list ? Orientation.Vertical : Orientation.Horizontal);
            List.ItemsPanel = new ItemsPanelTemplate(panel);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, list ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(List, list ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            StartThumbs();
        }
        if (mode == ViewMode.Details) ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        List.SelectedItem = sel;
        if (sel != null) List.ScrollIntoView(sel);
    }

    void ViewButton_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        int n = 0;
        foreach (var m in Modes)
        {
            var mode = m.mode;
            var mi = new MenuItem
            {
                Header = m.name, InputGestureText = $"Ctrl+Shift+{Modes.Length - n++}",
                Icon = new TextBlock { Text = m.glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 },
                FontWeight = mode == Mode ? FontWeights.SemiBold : FontWeights.Normal,
            };
            mi.Click += (_, _) => SetView(mode);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // Real Windows icons for Details/List rows (instead of outline glyphs). Plain files share one icon
    // per extension, so those are looked up once; exe/lnk/ico and folders carry their own.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> IconByExt = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".appref-ms" };
    CancellationTokenSource icons;
    void StartIcons()
    {
        icons?.Cancel();
        var cts = icons = new CancellationTokenSource();
        var todo = view?.Cast<Entry>().Where(e => !e.IsUp).Take(5000).ToList() ?? new();
        var t = new Thread(() =>
        {
            foreach (var e in todo)
            {
                if (cts.IsCancellationRequested) return;
                var ext = System.IO.Path.GetExtension(e.Name);
                bool shared = !e.IsDir && !OwnIcon.Contains(ext);
                ImageSource img = shared && IconByExt.TryGetValue(ext, out var cached) ? cached : Thumbnails.Get(e.Path, 32, iconOnly: true);
                if (img == null) continue;
                if (shared) IconByExt.TryAdd(ext, img);
                Dispatcher.BeginInvoke(() => e.SmallIcon = img);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Thumbnails load on one STA thread (shell thumbnail handlers expect STA), top of the list first.
    CancellationTokenSource thumbs;
    void StartThumbs()
    {
        thumbs?.Cancel();
        if (Mode is ViewMode.Details or ViewMode.List) return;
        var cts = thumbs = new CancellationTokenSource();
        int size = (int)Math.Min(256, Modes.First(x => x.mode == Mode).size * 1.5);
        var todo = view?.Cast<Entry>().Where(e => !e.IsUp).Take(3000).ToList() ?? new();
        var t = new Thread(() =>
        {
            foreach (var e in todo)
            {
                if (cts.IsCancellationRequested) return;
                var img = Thumbnails.Get(e.Path, size);
                if (img != null) Dispatcher.BeginInvoke(() => e.Thumb = img);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Arrow on the sorted column: ↑ ascending, ↓ descending.
    readonly Dictionary<GridViewColumn, string> colKey = new();

    void ShowSortArrow()
    {
        foreach (var c in ((GridView)List.View).Columns)
        {
            if (!colKey.TryGetValue(c, out var key)) colKey[c] = key = (string)c.Header;
            var row = new DockPanel { LastChildFill = true };
            if (key == sortKey)
            {
                var arrow = new TextBlock { Text = sortDesc ? "\uE74B" : "\uE74A", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 10, Margin = new(6, 2, 0, 0) };
                DockPanel.SetDock(arrow, Dock.Right);
                row.Children.Add(arrow);
            }
            row.Children.Add(new TextBlock { Text = key, TextTrimming = TextTrimming.CharacterEllipsis });
            c.Header = row;
        }
    }

    // ---- Drag & drop: between panes, and to/from Explorer (CF_HDROP). Default is copy; Shift = move. ----
    Point dragStart;
    bool dragArmed;
    Entry keepSelection; // click on an already multi-selected row: keep the selection so it can be dragged

    public static Entry FindRow(DependencyObject d)
    {
        while (d != null && d is not ListViewItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListViewItem)?.DataContext as Entry;
    }

    void Drag_Down(object s, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        dragArmed = row is { IsUp: false };
        dragStart = e.GetPosition(List);
        keepSelection = null;
        if (row != null && e.ClickCount == 1 && List.SelectedItems.Count > 1 && List.SelectedItems.Contains(row) && Keyboard.Modifiers == ModifierKeys.None)
        {
            keepSelection = row;
            e.Handled = true;
            List.Focus();
        }
    }

    void Drag_Up(object s, MouseButtonEventArgs e)
    {
        if (keepSelection != null) { List.SelectedItem = keepSelection; keepSelection = null; } // it was a plain click after all
        dragArmed = false;
    }

    void Drag_Move(object s, MouseEventArgs e)
    {
        if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(List) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragArmed = false; keepSelection = null;
        var paths = Selected.Select(x => x.Path).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    // Drop on a folder row → into that folder; anywhere else → the folder this pane shows.
    string DropTarget(DragEventArgs e) => FindRow(e.OriginalSource as DependencyObject) is { IsDir: true } row ? row.Path : Dir;

    void Drop_Over(object s, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.None
            : e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) ? DragDropEffects.Move
            : DragDropEffects.Copy;
        // no dropping a folder onto itself / into the folder it's already in
        if (e.Effects != DragDropEffects.None && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var target = DropTarget(e);
            if (files.Any(f => f.Equals(target, StringComparison.OrdinalIgnoreCase)
                            || System.IO.Path.GetDirectoryName(f)?.Equals(target, StringComparison.OrdinalIgnoreCase) == true))
                e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    void Drop_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        Drop_Over(s, e);
        if (e.Effects == DragDropEffects.None) return;
        FilesDropped?.Invoke(files.ToList(), DropTarget(e), e.Effects == DragDropEffects.Move);
        // Report Copy back to the source even for a move: Ferry does the move itself, and a source
        // like Explorer that sees Move might delete the originals while Ferry is still copying.
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void NewTab_Click(object s, RoutedEventArgs e) => NewTab();
    void Back_Click(object s, RoutedEventArgs e) => Back();
    void Fwd_Click(object s, RoutedEventArgs e) => Forward();
    void Up_Click(object s, RoutedEventArgs e) => Up();
}
