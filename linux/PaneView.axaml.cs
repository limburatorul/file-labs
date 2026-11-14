using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FileExplorer;

/// The Details columns' widths, shared by the header and every row.
public sealed class Columns : INotifyPropertyChanged
{
    double name = 280, type = 60, size = 90, modified = 190;
    public double Name { get => name; set { name = value; Changed(nameof(Name)); } }
    public double Type { get => type; set { type = value; Changed(nameof(Type)); } }
    public double Size { get => size; set { size = value; Changed(nameof(Size)); } }
    public double Modified { get => modified; set { modified = value; Changed(nameof(Modified)); } }
    public double Total => 6 + name + type + size + modified + 10;
    public event PropertyChangedEventHandler PropertyChanged;
    void Changed(string p) => PropertyChanged?.Invoke(this, new(p));
}

public partial class PaneView : UserControl
{
    sealed class Tab
    {
        public string Dir;
        public readonly List<string> Back = new(), Fwd = new();
        public string Selected; // the name selected when the tab was left, to put back when it comes back
    }

    readonly List<Tab> tabs = new();
    int activeTab;
    Tab T => tabs[activeTab];
    public string Dir => T.Dir;
    public Columns Cols { get; } = new();
    public ViewMode Mode { get; private set; } = ViewMode.Details;
    public static readonly StyledProperty<double> TileSizeProperty = AvaloniaProperty.Register<PaneView, double>(nameof(TileSize), 64);
    public static readonly StyledProperty<double> TileWidthProperty = AvaloniaProperty.Register<PaneView, double>(nameof(TileWidth), 92);
    public double TileSize { get => GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }
    public double TileWidth { get => GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    string sortKey = "Name"; bool sortDesc;
    List<Entry> items = new();
    public IReadOnlyList<Entry> Items => items;
    string searchQuery; CancellationTokenSource searchCts, loadCts, sizeCts, thumbCts;
    FileSystemWatcher watcher;
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public event Action Activated;
    public event Action<PaneView> Changed;              // folder, selection or counts: the stats cards follow
    public event Action<List<string>, string, bool> FilesDropped; // files, into, move
    public event Action<PaneView, Entry, Point> ContextRequested;
    public event Action<string> Opened;                 // a folder this pane went to, for Recent

    public PaneView()
    {
        InitializeComponent();
        List.ItemTemplate = (IDataTemplate)Resources["DetailsRow"];
        List.SelectionChanged += (_, _) => { UpdateFooter(); Changed?.Invoke(this); };
        List.DoubleTapped += (_, e) => { if (RowOf(e.Source) is { } r && !r.Renaming) OpenEntry(r); };
        List.AddHandler(PointerPressedEvent, List_PointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, List_PointerMoved, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, List_PointerReleased, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, Rename_KeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(LostFocusEvent, Rename_LostFocus, RoutingStrategies.Bubble);
        List.GotFocus += (_, _) => Activated?.Invoke();
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(), RoutingStrategies.Tunnel, handledEventsToo: true);

        BackButton.Click += (_, _) => GoBack();
        FwdButton.Click += (_, _) => GoForward();
        UpButton.Click += (_, _) => GoUp();
        NewTabButton.Click += (_, _) => NewTab(Dir);
        CrumbArea.PointerPressed += (_, e) => { if (e.Source == CrumbArea || e.Source == Crumbs) EditPath(); };
        PathBox.AddHandler(KeyDownEvent, PathBox_KeyDown, RoutingStrategies.Tunnel); // before the box eats Enter and Esc
        PathBox.LostFocus += (_, _) => EndEditPath();
        FilterBox.TextChanged += (_, _) => FilterChanged();
        FilterBox.AddHandler(KeyDownEvent, FilterBox_KeyDown, RoutingStrategies.Tunnel);
        FilterClear.Click += (_, _) => ClearFilter();
        FolderStrip.PointerPressed += (_, _) => { List.SelectedItems?.Clear(); FocusList(); };
        ViewButton.Click += (_, _) => ShowViewMenu(ViewButton);
        SortButton.Click += (_, _) => ShowSortMenu(SortButton);
        TabDragArea.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e); };
        TabDragArea.DoubleTapped += (_, _) => NewTab(Dir);

        DragDrop.AddDragOverHandler(List, Drop_Over);
        DragDrop.AddDropHandler(List, Drop_Drop);
        DragDrop.AddDragLeaveHandler(List, (_, _) => ShowDropRow(null));
        DragDrop.AddDragOverHandler(FolderStrip, (_, e) => { e.DragEffects = DropEffects(e, Dir); FolderStrip.Background = Fmt.Brush("#14FFFFFF"); e.Handled = true; });
        DragDrop.AddDragLeaveHandler(FolderStrip, (_, _) => FolderStrip.Background = Brushes.Transparent);
        DragDrop.AddDropHandler(FolderStrip, async (_, e) => { FolderStrip.Background = Brushes.Transparent; await DropInto(e, Dir); });

        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); Refresh(keepSelection: true); };
        // Name takes what the other columns leave, as on Windows (FitName): the date is never cut off
        List.SizeChanged += (_, _) => FitName();
        BuildHeader();
    }

    // ---- start, tabs ----
    public void Init(string name, string fallback)
    {
        var saved = Settings.Tabs.TryGetValue(name, out var t) ? t.Where(Directory.Exists).ToList() : new();
        if (saved.Count == 0) saved.Add(fallback);
        foreach (var d in saved) tabs.Add(new Tab { Dir = d });
        activeTab = Math.Clamp(Settings.ActiveTab.GetValueOrDefault(name), 0, tabs.Count - 1);
        SetView(Settings.Views.TryGetValue(name, out var v) ? v : ViewMode.Details, save: false);
        Load(T.Dir, null);
        BuildTabs();
    }

    public void SaveTabs()
    {
        Settings.Tabs[Name] = tabs.Select(t => t.Dir).ToList();
        Settings.ActiveTab[Name] = activeTab;
    }

    public void NewTab(string dir)
    {
        T.Selected = (List.SelectedItem as Entry)?.Name;
        tabs.Insert(activeTab + 1, new Tab { Dir = dir });
        activeTab++;
        Load(dir, null);
        BuildTabs();
        FocusList();
    }

    public void CloseTab(int i)
    {
        if (tabs.Count == 1) return;
        MainWindow.ClosedTabs.Push(tabs[i].Dir);
        tabs.RemoveAt(i);
        if (activeTab >= tabs.Count || i < activeTab) activeTab = Math.Max(0, activeTab - 1);
        Load(T.Dir, T.Selected);
        BuildTabs();
    }
    public void CloseTab() => CloseTab(activeTab);

    public void SwitchTab(int i)
    {
        if (i == activeTab || i < 0 || i >= tabs.Count) return;
        T.Selected = (List.SelectedItem as Entry)?.Name;
        activeTab = i;
        Load(T.Dir, T.Selected);
        BuildTabs();
    }
    public void NextTab(int by) => SwitchTab(((activeTab + by) % tabs.Count + tabs.Count) % tabs.Count);

    void BuildTabs()
    {
        TabStrip.Children.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i;
            bool on = i == activeTab;
            var name = new TextBlock { Text = DirName(tabs[i].Dir), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150 };
            var icon = new TextBlock { Text = Glyph.Folder, Classes = { "icon" }, Foreground = Fmt.Brush("#F5C451"), FontSize = 13, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var close = new Button { Classes = { "ghost" }, Padding = new(3), Margin = new(6, 0, 0, 0), IsVisible = on, Content = new TextBlock { Text = Glyph.Close, Classes = { "icon" }, FontSize = 10 } };
            close.Click += (_, e) => { CloseTab(index); e.Handled = true; };
            var row = new DockPanel { Children = { icon, close, name } };
            DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(close, Dock.Right);
            var tab = new Border
            {
                Child = row, Padding = new(10, 6, 6, 6), Margin = new(0, 4, 0, 0), CornerRadius = new(8, 8, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
                Background = on ? Fmt.Brush("#0DFFFFFF") : Brushes.Transparent,
                BorderThickness = new(0, 2, 0, 0), BorderBrush = on ? (IBrush)Application.Current!.FindResource("Accent") : Brushes.Transparent,
            };
            ToolTip.SetTip(tab, tabs[i].Dir);
            tab.PointerEntered += (_, _) => { close.IsVisible = true; if (!on) tab.Background = Fmt.Brush("#0DFFFFFF"); };
            tab.PointerExited += (_, _) => { close.IsVisible = on; if (!on) tab.Background = Brushes.Transparent; };
            tab.PointerPressed += (_, e) =>
            {
                var pt = e.GetCurrentPoint(tab).Properties;
                if (pt.IsMiddleButtonPressed) CloseTab(index);
                else if (pt.IsLeftButtonPressed) SwitchTab(index);
                e.Handled = true;
            };
            TabStrip.Children.Add(tab);
        }
    }

    static string DirName(string d) => d == "/" ? "/" : d == Home ? "Home" : Path.GetFileName(d.TrimEnd('/'));
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---- navigation ----
    public void Navigate(string path, string select = null, bool record = true)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (path.StartsWith("~")) path = Home + path[1..];
        if (File.Exists(path) && !Directory.Exists(path)) { select = Path.GetFileName(path); path = Path.GetDirectoryName(path); }
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        if (!Directory.Exists(path)) { MainWindow.Say($"{path} isn't there"); return; }
        if (record && path != T.Dir) { T.Back.Add(T.Dir); T.Fwd.Clear(); }
        Load(path, select);
        BuildTabs();
    }

    public void GoBack() { if (T.Back.Count == 0) return; T.Fwd.Add(T.Dir); var to = T.Back[^1]; T.Back.RemoveAt(T.Back.Count - 1); Load(to, DirName(T.Fwd[^1])); BuildTabs(); }
    public void GoForward() { if (T.Fwd.Count == 0) return; T.Back.Add(T.Dir); var to = T.Fwd[^1]; T.Fwd.RemoveAt(T.Fwd.Count - 1); Load(to, null); BuildTabs(); }
    public void GoUp() { if (searchQuery != null) { ClearFilter(); return; } if (T.Dir == "/") return; var from = DirName(T.Dir); Navigate(Path.GetDirectoryName(T.Dir) ?? "/", from); }

    void Load(string dir, string select)
    {
        bool sameFolder = T.Dir == dir;
        T.Dir = dir;
        if (!sameFolder) { FilterBox.Text = ""; searchQuery = null; searchCts?.Cancel(); }
        if (Settings.FolderSorts.TryGetValue(dir, out var s)) (sortKey, sortDesc) = s; else (sortKey, sortDesc) = ("Name", false);
        BuildCrumbs();
        Watch(dir);
        Opened?.Invoke(dir);
        Refresh(select: select);
    }

    /// Reads the folder again (off the UI thread), keeping the selection and the scroll where they make sense.
    public void Refresh(bool keepSelection = false, string select = null)
    {
        if (searchQuery != null) return; // search results stay until the box is cleared
        var dir = T.Dir;
        var keep = keepSelection ? List.SelectedItems?.Cast<Entry>().Select(e => e.Name).ToHashSet() : null;
        var focus = select ?? (keepSelection ? (List.SelectedItem as Entry)?.Name : null);
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        var old = items.ToDictionary(e => e.Name, StringComparer.Ordinal);
        Task.Run(() => ReadDir(dir, old), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || dir != T.Dir) return;
                items = t.Result;
                ShowItems(keep, focus);
                StartSizes();
                StartThumbs();
            });
        });
        UpdateDrive();
    }

    List<Entry> ReadDir(string dir, Dictionary<string, Entry> old)
    {
        var list = new List<Entry>();
        if (dir != "/") list.Add(new Entry { Name = "..", Path = Path.GetDirectoryName(dir) ?? "/", IsDir = true });
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (!Settings.ShowHidden && fsi.Name.StartsWith('.')) continue;
                bool link = fsi.LinkTarget != null;
                bool isDir = fsi is DirectoryInfo || link && Directory.Exists(fsi.FullName);
                var e = new Entry { Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, IsLink = link };
                try { e.Modified = fsi.LastWriteTime; } catch (IOException) { }
                if (!isDir) { try { e.Size = ((FileInfo)fsi).Length; } catch (IOException) { e.Size = 0; } }
                else if (old.TryGetValue(fsi.Name, out var was) && was.Size >= 0) e.Size = was.Size;
                if (old.TryGetValue(fsi.Name, out var prev) && prev.Thumb != null && prev.Modified == e.Modified) { e.Thumb = prev.Thumb; e.ThumbSize = prev.ThumbSize; }
                list.Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dispatcher.UIThread.Post(() => MainWindow.Say(ex.Message)); }
        return list;
    }

    string filterText = "";
    void ShowItems(HashSet<string> keep = null, string focus = null)
    {
        IEnumerable<Entry> view = items;
        if (filterText != "" && searchQuery == null)
        {
            var rx = Wildcard(filterText);
            view = view.Where(e => e.IsUp || rx.IsMatch(e.Name));
        }
        var sorted = view.ToList();
        sorted.Sort(new EntrySort(sortKey, sortDesc));
        List.ItemsSource = sorted;
        EmptyText.Text = searchQuery != null ? "Searching…" : filterText != "" ? $"Nothing here matches “{filterText}”" : "This folder is empty";
        EmptyText.IsVisible = sorted.Count(e => !e.IsUp) == 0;
        if (keep is { Count: > 0 })
            foreach (var e in sorted.Where(e => keep.Contains(e.Name))) List.SelectedItems?.Add(e);
        var f = focus != null ? sorted.FirstOrDefault(e => e.Name == focus) : null;
        f ??= List.SelectedItems?.Count > 0 ? null : sorted.FirstOrDefault(e => !e.IsUp) ?? sorted.FirstOrDefault();
        if (f != null) { if (keep == null || !keep.Contains(f.Name)) List.SelectedItem = f; List.ScrollIntoView(f); }
        UpdateFooter();
        UpdateSortButton();
        Changed?.Invoke(this);
    }

    static Regex Wildcard(string text)
    {
        var t = text.Trim();
        var pattern = t.Contains('*') || t.Contains('?') ? "^" + Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$" : Regex.Escape(t);
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    void Watch(string dir)
    {
        watcher?.Dispose(); watcher = null;
        try
        {
            watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => Dispatcher.UIThread.Post(() => { refreshTimer.Stop(); refreshTimer.Start(); });
            watcher.Created += h; watcher.Deleted += h; watcher.Changed += h; watcher.Renamed += (s, e) => h(s, e);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { watcher = null; }
    }

    // ---- the path: crumbs, and the box ----
    void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var parts = new List<(string Name, string Path)>();
        if (searchQuery != null) parts.Add(($"Search “{searchQuery}”", null));
        var dir = T.Dir;
        var home = Home;
        string start;
        if (dir == home || dir.StartsWith(home + "/")) { parts.Insert(0, ("Home", home)); start = home; }
        else { parts.Insert(0, ("/", "/")); start = "/"; }
        int at = searchQuery != null ? parts.Count - 1 : parts.Count;
        if (dir.Length > start.Length)
        {
            var acc = start;
            foreach (var p in dir[start.Length..].Trim('/').Split('/'))
            {
                acc = acc == "/" ? "/" + p : acc + "/" + p;
                parts.Insert(at++, (p, acc));
            }
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var (name, path) = parts[i];
            var b = new Button { Classes = { "ghost" }, Content = name, FontSize = 12, Padding = new(6, 3), MaxWidth = 220 };
            if (path != null)
            {
                b.Click += (_, _) => Navigate(path);
                b.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(b).Properties.IsMiddleButtonPressed) { NewTab(path); e.Handled = true; } }, RoutingStrategies.Tunnel);
                DragDrop.SetAllowDrop(b, true);
                DragDrop.AddDragOverHandler(b, (_, e) => { e.DragEffects = DropEffects(e, path); e.Handled = true; });
                DragDrop.AddDropHandler(b, async (_, e) => await DropInto(e, path));
            }
            Crumbs.Children.Add(b);
            if (i < parts.Count - 1) Crumbs.Children.Add(new TextBlock { Text = Glyph.ChevronRight, Classes = { "icon" }, FontSize = 10, Foreground = Fmt.Brush("#80FFFFFF"), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    public void EditPath()
    {
        PathBox.Text = T.Dir;
        Crumbs.IsVisible = false; PathBox.IsVisible = true;
        PathBox.Focus();
        PathBox.SelectAll();
    }
    void EndEditPath() { PathBox.IsVisible = false; Crumbs.IsVisible = true; }
    void PathBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { var p = PathBox.Text?.Trim(); EndEditPath(); Navigate(p); FocusList(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEditPath(); FocusList(); e.Handled = true; }
    }

    // ---- filter and search ----
    public bool InSearch => searchQuery != null;
    public bool Filtered => filterText != "";
    void FilterChanged()
    {
        var t = FilterBox.Text ?? "";
        FilterHint.IsVisible = t == "";
        FilterClear.IsVisible = t != "" || searchQuery != null;
        if (searchQuery != null) return; // a search's box is edited for the next Enter
        filterText = t;
        ShowItems(focus: (List.SelectedItem as Entry)?.Name);
    }

    /// Letters typed anywhere in the window land here (MainWindow), as on Windows.
    public void TypeInFilter(string text)
    {
        FilterBox.Focus();
        FilterBox.Text = (FilterBox.Text ?? "") + text;
        FilterBox.CaretIndex = FilterBox.Text.Length;
    }

    void FilterBox_KeyDown(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when (FilterBox.Text ?? "").Trim() != "": Search(FilterBox.Text.Trim()); e.Handled = true; break;
            case Key.Down or Key.Enter: LeaveFilter(); e.Handled = true; break;
            case Key.Escape: ClearFilter(); FocusList(); e.Handled = true; break;
        }
    }

    public void LeaveFilter()
    {
        if (List.SelectedItem is not Entry { IsUp: false }) List.SelectedItem = (List.ItemsSource as List<Entry>)?.FirstOrDefault(x => !x.IsUp);
        FocusList();
    }

    public void ClearFilter()
    {
        searchCts?.Cancel();
        bool wasSearch = searchQuery != null;
        searchQuery = null; filterText = "";
        FilterBox.Text = "";
        if (wasSearch) { BuildCrumbs(); Refresh(); } else ShowItems(focus: (List.SelectedItem as Entry)?.Name);
    }

    /// Every subfolder, names matching the box; results arrive as they are found, up to Settings.SearchLimit.
    void Search(string query)
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        searchQuery = query; filterText = "";
        BuildCrumbs();
        FilterClear.IsVisible = true;
        items = new List<Entry>();
        ShowItems();
        var root = T.Dir; var rx = Wildcard(query);
        var found = new List<Entry>();
        int limit = Settings.SearchLimit;
        Task.Run(() =>
        {
            var batch = new List<Entry>();
            long last = Environment.TickCount64;
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0 && !cts.IsCancellationRequested)
            {
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> children;
                try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var fsi in children)
                {
                    if (!Settings.ShowHidden && fsi.Name.StartsWith('.')) continue;
                    bool isDir = fsi is DirectoryInfo && fsi.LinkTarget == null;
                    if (isDir && !(dir == "/" && fsi.Name is "proc" or "sys" or "dev" or "run")) stack.Push(fsi.FullName);
                    if (!rx.IsMatch(fsi.Name)) continue;
                    var rel = Path.GetRelativePath(root, Path.GetDirectoryName(fsi.FullName) ?? root);
                    var e = new Entry { Name = fsi.Name, Path = fsi.FullName, IsDir = fsi is DirectoryInfo, Location = rel == "." ? "" : rel };
                    try { e.Modified = fsi.LastWriteTime; if (fsi is FileInfo f) e.Size = f.Length; else e.Size = -2; } catch (IOException) { }
                    batch.Add(e);
                }
                if (batch.Count > 0 && (Environment.TickCount64 - last > 200 || stack.Count == 0))
                {
                    var add = batch.ToList(); batch.Clear(); last = Environment.TickCount64;
                    Dispatcher.UIThread.Post(() => { if (cts.IsCancellationRequested) return; found.AddRange(add); items = found.ToList(); ShowItems(focus: (List.SelectedItem as Entry)?.Name); });
                }
                if (limit > 0 && found.Count + batch.Count >= limit) break;
            }
            var rest = batch.ToList();
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                found.AddRange(rest); items = found.ToList(); ShowItems();
                EmptyText.Text = $"Nothing under {DirName(root)} matches “{query}”";
                MainWindow.Say(limit > 0 && found.Count >= limit ? $"Stopped at {limit:N0} results (Settings → Files)" : $"{found.Count:N0} found");
                StartThumbs();
            });
        }, cts.Token);
    }

    // ---- views ----
    static readonly (ViewMode mode, string name, string glyph)[] Modes =
    {
        (ViewMode.IconsXL, "XL Icons", Glyph.IconsView), (ViewMode.IconsL, "L Icons", Glyph.IconsView), (ViewMode.IconsM, "M Icons", Glyph.IconsView),
        (ViewMode.List, "List", Glyph.ListView), (ViewMode.Details, "Details", Glyph.DetailsView),
    };
    public string ViewModeName => Modes.First(x => x.mode == Mode).name;

    public void SetView(ViewMode mode, bool save = true)
    {
        Mode = mode;
        bool tiles = mode is ViewMode.IconsM or ViewMode.IconsL or ViewMode.IconsXL;
        List.ItemTemplate = (IDataTemplate)Resources[mode == ViewMode.Details ? "DetailsRow" : mode == ViewMode.List ? "ListRow" : "TileRow"];
        List.ItemsPanel = mode == ViewMode.Details ? new FuncTemplate<Panel>(() => new VirtualizingStackPanel())
                        : new FuncTemplate<Panel>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        List.Classes.Set("tiles", tiles);
        Header.IsVisible = mode == ViewMode.Details;
        ViewIcon.Text = Modes.First(x => x.mode == mode).glyph;
        ViewName.Text = ViewModeName;
        TileSize = mode switch { ViewMode.IconsXL => 192, ViewMode.IconsL => 112, _ => 64 };
        TileWidth = TileSize + 28;
        if (save) { Settings.Views[Name] = mode; StartThumbs(); }
    }

    void ShowViewMenu(Control at)
    {
        var menu = new ContextMenu();
        int n = 5;
        foreach (var m in Modes)
        {
            var mode = m.mode;
            var mi = new MenuItem { Header = m.name, InputGesture = new KeyGesture(Key.D0 + n--, KeyModifiers.Control | KeyModifiers.Shift), Icon = new TextBlock { Text = m.glyph, Classes = { "icon" } }, FontWeight = mode == Mode ? FontWeight.SemiBold : FontWeight.Normal };
            mi.Click += (_, _) => SetView(mode);
            menu.Items.Add(mi);
        }
        menu.Open(at);
    }

    // ---- sorting ----
    public void SetSort(string key, bool desc)
    {
        sortKey = key; sortDesc = desc;
        if (key == "Name" && !desc) Settings.FolderSorts.Remove(T.Dir); else Settings.FolderSorts[T.Dir] = (key, desc);
        ShowItems(focus: (List.SelectedItem as Entry)?.Name);
        BuildHeader();
    }

    void UpdateSortButton()
    {
        SortName.Text = sortKey;
        SortIcon.Text = sortDesc ? Glyph.Down : Glyph.Up;
    }

    void ShowSortMenu(Control at)
    {
        var menu = new ContextMenu();
        foreach (var k in Entry.SortKeys)
        {
            var key = k;
            var mi = new MenuItem { Header = k, FontWeight = k == sortKey ? FontWeight.SemiBold : FontWeight.Normal };
            mi.Click += (_, _) => SetSort(key, key == sortKey ? sortDesc : key is "Size" or "Modified");
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var rev = new MenuItem { Header = sortDesc ? "Ascending" : "Descending", Icon = new TextBlock { Text = sortDesc ? Glyph.Up : Glyph.Down, Classes = { "icon" } } };
        rev.Click += (_, _) => SetSort(sortKey, !sortDesc);
        menu.Items.Add(rev);
        menu.Open(at);
    }

    void BuildHeader()
    {
        HeaderCells.Children.Clear();
        HeaderCells.Children.Add(new Border { Width = 6 });
        void Cell(string title, string key, Func<double> width, Action<double> set, bool right = false)
        {
            var text = new TextBlock { Text = title, Foreground = Fmt.Brush("#9AA6B8"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = right ? new(0, 0, 16, 0) : new(0) };
            var arrow = new TextBlock { Text = sortDesc ? Glyph.Down : Glyph.Up, Classes = { "icon" }, FontSize = 10, Foreground = Fmt.Brush("#9AA6B8"), Margin = new(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = sortKey == key };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Children = { text, arrow }, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            var grip = new Border { Width = 9, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, Cursor = new Cursor(StandardCursorType.SizeWestEast), Margin = new(0, 0, -5, 0),
                                    Child = new Border { Width = 1, Margin = new(0, 7), Background = Fmt.Brush("#2EFFFFFF"), HorizontalAlignment = HorizontalAlignment.Center } };
            var cell = new Grid { Width = width(), Background = Brushes.Transparent, Children = { label, grip }, Cursor = new Cursor(StandardCursorType.Hand) };
            if (title != "Name") cell.Margin = new(title == "Modified" ? 10 : 0, 0, 0, 0);
            if (title == "Type") label.Margin = new(10, 0, 0, 0);
            cell.PointerPressed += (_, e) => { if (e.Source is not Border b || b != grip && b.Parent != grip) SetSort(key, key == sortKey ? !sortDesc : key is "Size" or "Modified"); };
            double startX = 0, startW = 0; bool dragging = false;
            grip.PointerPressed += (_, e) => { startX = e.GetPosition(this).X; startW = width(); dragging = true; e.Pointer.Capture(grip); e.Handled = true; };
            grip.PointerMoved += (_, e) => { if (!dragging) return; var w = Math.Max(40, startW + e.GetPosition(this).X - startX); set(w); cell.Width = w; };
            grip.PointerReleased += (_, e) => { dragging = false; e.Pointer.Capture(null); };
            grip.DoubleTapped += (_, e) => { e.Handled = true; };
            HeaderCells.Children.Add(cell);
        }
        Cell("Name", "Name", () => Cols.Name, w => Cols.Name = w);
        Cell("Type", "Type", () => Cols.Type, w => Cols.Type = w);
        Cell("Size", "Size", () => Cols.Size, w => Cols.Size = w, right: true);
        Cell("Modified", "Modified", () => Cols.Modified, w => Cols.Modified = w);
    }

    void FitName()
    {
        if (Mode != ViewMode.Details || List.Bounds.Width <= 0) return;
        var w = Math.Max(140, List.Bounds.Width - (Cols.Total - Cols.Name) - 34);
        if (Math.Abs(w - Cols.Name) < 1) return;
        Cols.Name = w;
        BuildHeader();
    }

    // ---- footer, drive ----
    void UpdateFooter()
    {
        var shown = (List.ItemsSource as List<Entry>) ?? new();
        int dirs = shown.Count(e => e.IsDir && !e.IsUp), files = shown.Count(e => !e.IsDir);
        var sel = Selected;
        string what = $"{dirs:N0} folder{(dirs == 1 ? "" : "s")} · {files:N0} file{(files == 1 ? "" : "s")}";
        if (filterText != "" && searchQuery == null) what = $"{shown.Count(e => !e.IsUp):N0} of {items.Count(e => !e.IsUp):N0} match “{filterText}”";
        if (sel.Count > 0)
        {
            long bytes = sel.Where(e => e.Size > 0).Sum(e => e.Size);
            what += $"   ·   {sel.Count:N0} selected" + (bytes > 0 ? $" · {Fmt.Size(bytes)}" : "");
        }
        Footer.Text = what;
    }

    void UpdateDrive()
    {
        var m = Mounts.MountOf(T.Dir);
        var (total, free) = m.Space;
        if (total <= 0) { DriveInfoText.Text = ""; UsedBar.Width = 0; return; }
        DriveInfoText.Text = $"{Fmt.Size(free)} free of {Fmt.Size(total)}";
        FolderStrip.SizeChanged -= StripSized; FolderStrip.SizeChanged += StripSized;
        usedFraction = 1 - (double)free / total;
        StripSized(null, null);
    }
    double usedFraction;
    void StripSized(object s, SizeChangedEventArgs e)
    {
        var w = Math.Max(0, FolderStrip.Bounds.Width - DriveInfoText.Bounds.Width - 30);
        UsedBar.Width = w * usedFraction;
        UsedBar.Background = usedFraction > 0.9 ? Fmt.Brush("#F87171") : (IBrush)Application.Current!.FindResource("Accent");
    }

    // ---- selection, focus, activity ----
    public List<Entry> Selected => List.SelectedItems?.Cast<Entry>().Where(e => !e.IsUp).ToList() ?? new();
    public Entry Current => List.SelectedItem as Entry;
    public void FocusList()
    {
        var c = List.SelectedItem is { } s ? List.ContainerFromItem(s) : null;
        if (c != null) c.Focus(); else List.Focus();
    }

    bool isActive;
    public void SetActive(bool on)
    {
        isActive = on;
        Card.BorderBrush = on ? (IBrush)Application.Current!.FindResource("AccentEdge") : Fmt.Brush("#1AFFFFFF");
        List.Classes.Set("inactive", !on);
    }

    public void SelectAll() => List.SelectAll();
    public void SelectNames(IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.Ordinal);
        List.SelectedItems?.Clear();
        foreach (var e in (List.ItemsSource as List<Entry>) ?? new()) if (set.Contains(e.Name)) List.SelectedItems?.Add(e);
        if (List.SelectedItems?.Count > 0) List.ScrollIntoView(List.SelectedItems[0]);
    }

    public void OpenSelected()
    {
        var sel = Selected;
        if (sel.Count == 0 && Current is { IsUp: true }) { GoUp(); return; }
        if (sel.Count == 1 && sel[0].IsDir) { OpenEntry(sel[0]); return; }
        foreach (var e in sel) OpenEntry(e);
    }

    void OpenEntry(Entry e)
    {
        if (e.IsUp) { GoUp(); return; }
        if (e.IsDir) { Navigate(e.Path); return; }
        if (Backups.Named(e.Path) || e.Ext is "zip") { MainWindow.Instance?.ShowQuickView(e); return; }
        Shell.Open(e.Path);
        MainWindow.Say($"Opened {e.Name}");
    }

    // ---- folder sizes, in the background: the same cache as on Windows ----
    void StartSizes()
    {
        sizeCts?.Cancel();
        var cts = sizeCts = new CancellationTokenSource();
        var dirs = items.Where(e => e.IsDir && !e.IsUp && !e.IsLink).ToList();
        if (dirs.Count == 0) return;
        bool network = Mounts.MountOf(T.Dir).Network;
        Task.Run(() =>
        {
            foreach (var e in dirs)
            {
                if (cts.IsCancellationRequested) return;
                long size = network ? -2 : Measure(e.Path, cts.Token, depth: 0);
                if (cts.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() => { e.Size = size; if (sortKey == "Size") ShowItems(focus: (List.SelectedItem as Entry)?.Name); });
            }
            Dispatcher.UIThread.Post(() => { UpdateFooter(); Changed?.Invoke(this); SizeCache.SaveIfChanged(); });
        }, cts.Token);
    }

    static long Measure(string dir, CancellationToken token, int depth)
    {
        if (token.IsCancellationRequested || depth > 64) return 0;
        DateTime stamp;
        try { stamp = Directory.GetLastWriteTimeUtc(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        var cached = SizeCache.Get(dir, stamp);
        if (cached >= 0) return cached;
        long total = 0;
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (token.IsCancellationRequested) return 0;
                if (fsi.LinkTarget != null) continue;
                if (fsi is FileInfo f) total += f.Length;
                else if (!(dir == "/" && fsi.Name is "proc" or "sys" or "dev" or "run")) total += Measure(fsi.FullName, token, depth + 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!token.IsCancellationRequested) SizeCache.Set(dir, stamp, total);
        return total;
    }

    // ---- thumbnails: pictures decoded small, everything else from the desktop's thumbnail cache when it has one ----
    static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    void StartThumbs()
    {
        thumbCts?.Cancel();
        var cts = thumbCts = new CancellationTokenSource();
        int size = Mode switch { ViewMode.IconsXL => 256, ViewMode.IconsL => 128, ViewMode.IconsM => 96, _ => 32 };
        var todo = ((List.ItemsSource as List<Entry>) ?? items).Where(e => !e.IsDir && (e.Thumb == null || e.ThumbSize < size)).Take(3000).ToList();
        Task.Run(() =>
        {
            foreach (var e in todo)
            {
                if (cts.IsCancellationRequested) return;
                var bmp = Thumbs.Get(e.Path, size, Pictures.Contains(Path.GetExtension(e.Name)));
                if (bmp != null) Dispatcher.UIThread.Post(() => { e.ThumbSize = size; e.Thumb = bmp; });
            }
        }, cts.Token);
    }

    // ---- inline rename (F2), as on Windows: the name without its extension selected ----
    Entry renaming;
    public void BeginRename()
    {
        var e = Current;
        if (e == null || e.IsUp) return;
        renaming = e;
        e.Renaming = true;
        List.ScrollIntoView(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromItem(e) is not Control c) return;
            var box = c.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("rename"));
            if (box == null) return;
            box.Text = e.Name;
            box.Focus();
            int dot = e.IsDir ? -1 : e.Name.LastIndexOf('.');
            box.SelectionStart = 0; box.SelectionEnd = dot > 0 ? dot : e.Name.Length;
        }, DispatcherPriority.Background);
    }

    void Rename_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Source is not TextBox box || !box.Classes.Contains("rename")) return;
        if (e.Key == Key.Enter) { CommitRename(box); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(); FocusList(); e.Handled = true; }
    }
    void Rename_LostFocus(object s, RoutedEventArgs e) { if (e.Source is TextBox box && box.Classes.Contains("rename") && renaming != null) CommitRename(box); }

    public event Action<PaneView, Entry, string> RenameCommitted;
    void CommitRename(TextBox box)
    {
        var e = renaming;
        if (e == null) return;
        var name = box.Text?.Trim() ?? "";
        EndRename();
        if (name != "" && name != e.Name) RenameCommitted?.Invoke(this, e, name);
        FocusList();
    }
    // cleared first: hiding the box takes its focus away, and that must not commit the name a second time
    void EndRename() { var e = renaming; renaming = null; if (e != null) e.Renaming = false; }

    // ---- pointer: drag out, the selection rectangle, middle click, right click ----
    Point? pressAt; PointerPressedEventArgs pressArgs; Entry pressRow; bool marquee;
    static Entry RowOf(object source) => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as Entry;
    bool PastColumns(double x) => Mode == ViewMode.Details && x > Cols.Total;

    void List_PointerPressed(object s, PointerPressedEventArgs e)
    {
        Activated?.Invoke();
        var pt = e.GetCurrentPoint(List);
        var row = PastColumns(e.GetPosition(List).X) ? null : RowOf(e.Source);
        if (e.Source is TextBox) return;
        if (pt.Properties.IsMiddleButtonPressed) { if (row?.IsDir == true && !row.IsUp) NewTab(row.Path); e.Handled = true; return; }
        if (pt.Properties.IsRightButtonPressed)
        {
            if (row != null && !(List.SelectedItems?.Contains(row) ?? false)) List.SelectedItem = row;
            if (row == null) List.SelectedItems?.Clear();
            ContextRequested?.Invoke(this, row, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        pressAt = e.GetPosition(List); pressArgs = e; pressRow = row;
        marquee = row == null;
        if (row != null && (List.SelectedItems?.Contains(row) ?? false) && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1)
            e.Handled = true; // a press on a selected row keeps the selection, so all of it can be dragged
        if (row == null && e.KeyModifiers == KeyModifiers.None) { List.SelectedItems?.Clear(); e.Handled = true; }
        if (row == null) { e.Pointer.Capture(List); e.Handled = true; }
    }

    async void List_PointerMoved(object s, PointerEventArgs e)
    {
        if (pressAt is not { } start) return;
        var at = e.GetPosition(List);
        if (Math.Abs(at.X - start.X) < 5 && Math.Abs(at.Y - start.Y) < 5) return;
        if (marquee) { UpdateMarquee(start, at, e.KeyModifiers.HasFlag(KeyModifiers.Control)); return; }
        var args = pressArgs; pressAt = null;
        if (pressRow != null && !(List.SelectedItems?.Contains(pressRow) ?? false)) List.SelectedItem = pressRow;
        var paths = Selected.Select(x => x.Path).ToList();
        if (paths.Count == 0 || args == null) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var data = new DataTransfer();
        foreach (var p in paths)
        {
            IStorageItem item = Directory.Exists(p) ? await storage.TryGetFolderFromPathAsync(p) : await storage.TryGetFileFromPathAsync(p);
            if (item != null) data.Add(DataTransferItem.CreateFile(item));
        }
        MainWindow.DraggedFrom = this;
        try { await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Copy | DragDropEffects.Move); }
        finally { MainWindow.DraggedFrom = null; ShowDropRow(null); }
    }

    void List_PointerReleased(object s, PointerReleasedEventArgs e)
    {
        if (pressAt != null && !marquee && pressRow != null && e.KeyModifiers == KeyModifiers.None && e.InitialPressMouseButton == MouseButton.Left)
            List.SelectedItem = pressRow; // a click (no drag) on a selected row selects just that one
        if (marquee) { Marquee.IsVisible = false; e.Pointer.Capture(null); }
        pressAt = null; marquee = false; pressRow = null;
    }

    void UpdateMarquee(Point a, Point b, bool add)
    {
        var r = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        Marquee.IsVisible = true;
        Canvas.SetLeft(Marquee, r.X); Canvas.SetTop(Marquee, r.Y); Marquee.Width = r.Width; Marquee.Height = r.Height;
        var hit = new List<Entry>();
        foreach (var c in List.GetRealizedContainers())
            if (c.DataContext is Entry en && !en.IsUp && c.TranslatePoint(new Point(0, 0), List) is { } p)
            {
                var w = Mode == ViewMode.Details ? Cols.Total : c.Bounds.Width;
                if (new Rect(p, new Size(w, c.Bounds.Height)).Intersects(r)) hit.Add(en);
            }
        if (!add) List.SelectedItems?.Clear();
        foreach (var en in hit) if (!(List.SelectedItems?.Contains(en) ?? false)) List.SelectedItems?.Add(en);
    }

    // ---- drops ----
    Entry dropRow;
    void ShowDropRow(Entry row)
    {
        if (dropRow == row) return;
        if (dropRow != null && List.ContainerFromItem(dropRow) is Control old) old.Classes.Remove("drop");
        dropRow = row;
        if (row != null && List.ContainerFromItem(row) is Control c) c.Classes.Add("drop");
    }

    // a drop on a folder's row goes into it; past the columns, on empty space or anywhere else, into this folder
    Entry DropRow(DragEventArgs e) => PastColumns(e.GetPosition(List).X) ? null : RowOf(e.Source) is { IsDir: true, IsUp: false } r ? r : null;

    DragDropEffects DropEffects(DragEventArgs e, string into)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return DragDropEffects.None;
        if (MainWindow.DraggedFrom == this && into == T.Dir) return DragDropEffects.None; // onto its own folder: nothing to do
        // Ctrl copies, Shift moves; otherwise a move on one drive, a copy between drives, as on Windows
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return DragDropEffects.Copy;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return DragDropEffects.Move;
        var from = MainWindow.DraggedFrom?.Dir;
        return from != null && Mounts.MountOf(from).Point == Mounts.MountOf(into).Point ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    void Drop_Over(object s, DragEventArgs e)
    {
        var row = DropRow(e);
        var into = row?.Path ?? T.Dir;
        e.DragEffects = DropEffects(e, into);
        ShowDropRow(e.DragEffects != DragDropEffects.None ? row : null);
        e.Handled = true;
    }

    async void Drop_Drop(object s, DragEventArgs e)
    {
        var row = DropRow(e);
        ShowDropRow(null);
        await DropInto(e, row?.Path ?? T.Dir);
    }

    async Task DropInto(DragEventArgs e, string into)
    {
        var effect = DropEffects(e, into);
        if (effect == DragDropEffects.None) return;
        var files = e.DataTransfer.TryGetFiles();
        var paths = files?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList() ?? new();
        if (paths.Count == 0) return;
        FilesDropped?.Invoke(paths, into, effect == DragDropEffects.Move);
        e.DragEffects = DragDropEffects.Copy; // the move is ours: a source told "moved" might delete the originals itself
        e.Handled = true;
    }
}

/// Small pictures for the list: a picture is decoded at the size asked; anything else comes from the freedesktop
/// thumbnail cache (~/.cache/thumbnails), where Dolphin and Nautilus keep theirs, when it has one.
static class Thumbs
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> cache = new(StringComparer.Ordinal);
    static readonly string CacheDir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(PaneView.Home, ".cache"), "thumbnails");

    public static Bitmap Get(string path, int size, bool picture)
    {
        long stamp; long length;
        try { var f = new FileInfo(path); stamp = f.LastWriteTimeUtc.Ticks; length = f.Length; } catch (IOException) { return null; }
        var key = $"{path}|{size}|{stamp}";
        if (cache.TryGetValue(key, out var hit)) return hit;
        Bitmap bmp = null;
        try
        {
            if (picture && length < 200 << 20)
            {
                using var s = File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(s, size, BitmapInterpolationMode.MediumQuality);
                if (bmp.PixelSize.Height > size * 2) { s.Position = 0; bmp.Dispose(); bmp = Bitmap.DecodeToHeight(s, size, BitmapInterpolationMode.MediumQuality); }
            }
            else
            {
                var uri = new Uri(path).AbsoluteUri;
                var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
                foreach (var folder in size > 128 ? new[] { "x-large", "large", "normal" } : new[] { "large", "normal", "x-large" })
                {
                    var t = Path.Combine(CacheDir, folder, md5 + ".png");
                    if (File.Exists(t)) { using var s = File.OpenRead(t); bmp = Bitmap.DecodeToWidth(s, size, BitmapInterpolationMode.MediumQuality); break; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException) { bmp = null; }
        catch (Exception) { bmp = null; } // a broken picture throws whatever its decoder throws; the glyph stays
        if (bmp != null && cache.Count < 4000) cache[key] = bmp;
        return bmp;
    }
}
