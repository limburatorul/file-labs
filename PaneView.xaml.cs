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
    public string Location { get; init; } = ""; // search results: the folder it was found in, relative to where the search started
    public string Where => Location == "" ? "" : "   in " + Location;

    long size = -1; // -1 = folder size still computing, -2 = not measured (network shares)
    public long Size { get => size; set { size = value; Changed(nameof(Size)); Changed(nameof(SizeText)); } }
    ImageSource thumb;
    public ImageSource Thumb { get => thumb; set { thumb = value; Changed(nameof(Thumb)); Changed(nameof(NoThumb)); } }
    public bool NoThumb => thumb == null;
    ImageSource smallIcon;
    public ImageSource SmallIcon { get => smallIcon; set { smallIcon = value; Changed(nameof(SmallIcon)); Changed(nameof(NoSmallIcon)); } }
    public bool NoSmallIcon => smallIcon == null;
    ImageSource overlay;
    public ImageSource Overlay { get => overlay; set { if (overlay != value) { overlay = value; Changed(nameof(Overlay)); } } } // sync badge
    bool renaming;
    public bool Renaming { get => renaming; set { renaming = value; Changed(nameof(Renaming)); Changed(nameof(NotRenaming)); } }
    public bool NotRenaming => !renaming;
    bool dimmed;
    public bool Dimmed { get => dimmed; set { dimmed = value; Changed(nameof(Dimmed)); } } // the other rows, while one is being renamed
    bool isCut;
    public bool IsCut { get => isCut; set { if (isCut != value) { isCut = value; Changed(nameof(IsCut)); } } }
    string version = "";
    public string Version { get => version; set { version = value; Changed(nameof(Version)); } }
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string SizeText => IsUp || size == -2 ? "" : size < 0 ? "…" : MainWindow.Fmt(size);
    bool NoDate => IsUp || Modified == default; // network shares have no date

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
    public Brush AgeBrush => NoDate ? Brushes.Transparent : new SolidColorBrush(AgeColor);
    public Brush AgeTextBrush
    {
        get { var c = AgeColor; return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140 ? MainWindow.Hex("#E6111111") : MainWindow.Hex("#E6FFFFFF"); }
    }
    public string Age
    {
        get
        {
            if (NoDate) return "";
            var d = DateTime.Now - Modified;
            return d.TotalHours < 1 ? $"{Math.Max(0, (int)d.TotalMinutes)}'"
                : d.TotalDays < 1 ? $"{(int)d.TotalHours}h"
                : d.TotalDays < 60 ? $"{(int)d.TotalDays}d"
                : d.TotalDays < 730 ? $"{(int)(d.TotalDays / 30)}mo"
                : $"{(int)(d.TotalDays / 365)}y";
        }
    }
    public string DateText => NoDate ? "" : Modified.ToString("yyyy-MM-dd  HH:mm");
    public string FullDate => NoDate ? null : Modified.ToString("dddd, yyyy-MM-dd HH:mm:ss");
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
    IList<Entry> items = new List<Entry>(); // an ObservableCollection while search results stream in
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
        // FindRow, not "OriginalSource is FrameworkElement": a double-click on the name lands on a Run
        List.MouseDoubleClick += (_, e) => { if (FindRow(e.OriginalSource as DependencyObject) != null) OpenSelected(); };
        List.SelectionChanged += (_, _) => { if (bulkSelect) return; UpdateFooter(); StatsChanged?.Invoke(); };
        // Middle click, as in a browser: a folder opens in a tab behind this one, a tab closes.
        List.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || FindRow(e.OriginalSource as DependencyObject) is not { IsDir: true } row) return;
            OpenInBackgroundTab(row.Path);
            e.Handled = true;
        };
        TabStrip.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || TabIndexAt(e.GetPosition(TabStrip)) is not (>= 0 and var i)) return;
            CloseTab(i);
            e.Handled = true;
        };
        List.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        List.AllowDrop = true;
        List.PreviewMouseLeftButtonDown += Drag_Down;
        List.PreviewMouseLeftButtonUp += Drag_Up;
        List.PreviewMouseMove += Drag_Move;
        List.DragOver += Drop_Over;
        List.Drop += Drop_Drop;
        debounce.Tick += (_, _) => { debounce.Stop(); Refresh(); };

        // Drags start from the strip, not from a tab button: the strip is rebuilt whenever the tabs
        // change, and a button that disappears mid-press takes the mouse capture with it.
        TabStrip.PreviewMouseLeftButtonDown += (_, e) => { tabDragFrom = e.GetPosition(TabStrip); tabDragIndex = TabIndexAt(tabDragFrom); };
        TabStrip.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || tabDragIndex < 0 || dragTab != null) return;
            var moved = e.GetPosition(TabStrip) - tabDragFrom;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (tabDragIndex >= tabs.Count) return;
            dragTab = (this, tabs[tabDragIndex]);
            tabDragIndex = -1;
            try { DragDrop.DoDragDrop(TabStrip, new DataObject("FileLabsTab", ""), DragDropEffects.Move); }
            finally { dragTab = null; }
        };
        TabStrip.PreviewMouseLeftButtonUp += (_, _) => tabDragIndex = -1;

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
    public bool SizesPending => Items.Any(e => e.Size == -1);

    // Active pane: lighter surface and accent edge. Inactive: darker and slightly faded, so it recedes.
    public void SetActive(bool on)
    {
        Card.Background = MainWindow.Hex(on ? "#12FFFFFF" : "#0A000000");
        Card.BorderBrush = MainWindow.Hex(on ? "#664C8DFF" : "#0FFFFFFF");
        List.Opacity = on ? 1 : 0.88;
    }

    readonly Dictionary<string, List<Entry>> listCache = new(StringComparer.OrdinalIgnoreCase);
    int navVersion; // a newer Navigate wins; a slow share must not overwrite the folder you moved on to

    public void Navigate(string dir, string select = null, bool record = true)
    {
        if (IsServer(dir)) { NavigateServer(dir.TrimEnd('\\'), select, record); return; }
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

        int v = GoTo(di.FullName, record);

        // Show the folder as we last saw it, immediately; the read below replaces it a moment later.
        // Without this, every tab switch waits on the disk — measured at 1.2 s for a cold folder.
        if (listCache.TryGetValue(di.FullName, out var cached)) ShowItems(cached, select);

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
                listCache[di.FullName] = list;
                if (listCache.Count > 24) listCache.Remove(listCache.Keys.First()); // a few folders is plenty
                ShowItems(list, select);

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

    /// Makes `path` the current tab's folder (history, crumbs, tabs) and leaves any search.
    /// Returns the navigation number that later results must still match.
    int GoTo(string path, bool record)
    {
        if (record && tabs[tab] != "" && !tabs[tab].Equals(path, StringComparison.OrdinalIgnoreCase)) { back.Push(tabs[tab]); fwd.Clear(); }
        tabs[tab] = path;
        if (searchQuery != null) { searchQuery = null; FilterBox.Text = ""; } // the query would hide most of the new folder
        BuildCrumbs();
        BuildTabs();
        return ++navVersion;
    }

    void ShowItems(IList<Entry> list, string select)
    {
        items = list;
        view = new ListCollectionView((IList)items) { CustomSort = new EntrySort(sortKey, sortDesc) };
        ApplyFilter();
        List.ItemsSource = view;
        List.SelectedItem = items.FirstOrDefault(e => e.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? view.Cast<Entry>().FirstOrDefault();
        if (select != null && List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        UpdateFooter();
        StatsChanged?.Invoke();
    }

    // ---- \\server: its shares, as folders (DirectoryInfo can't open a bare server name) ----
    static bool IsServer(string p) => p.StartsWith(@"\\") && p.Trim('\\').Length > 0 && !p.Trim('\\').Contains('\\');
    static string ServerOf(string unc) => @"\\" + unc.TrimStart('\\').Split('\\')[0];

    void NavigateServer(string server, string select, bool record)
    {
        int v = GoTo(server, record);
        watcher?.Dispose(); watcher = null;
        sizing?.Cancel();
        Task.Run(() =>
        {
            var shares = NetShares(server);
            Dispatcher.BeginInvoke(() =>
            {
                if (v != navVersion) return;
                if (shares == null) { Error?.Invoke($"Can't list the shares on {server}"); return; }
                ShowItems(shares.Select(s => new Entry { Name = s, Path = $@"{server}\{s}", IsDir = true, Size = -2 }).ToList(), select);
                DriveInfoText.Text = "";
                UsedBar.Width = 0;
                StartIcons();
            });
        });
    }

    [System.Runtime.InteropServices.DllImport("netapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int NetShareEnum(string server, int level, out IntPtr buf, int prefMax, out int read, out int total, IntPtr resume);
    [System.Runtime.InteropServices.DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);

    /// Disk shares on a server (no admin shares like C$), or null if it can't be reached.
    static List<string> NetShares(string server)
    {
        if (NetShareEnum(server, 1, out var buf, -1, out int read, out _, IntPtr.Zero) != 0) return null;
        try
        {
            var list = new List<string>();
            int size = IntPtr.Size * 3; // SHARE_INFO_1: name, type, remark
            for (int i = 0; i < read; i++)
            {
                var row = buf + i * size;
                var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(row));
                int type = System.Runtime.InteropServices.Marshal.ReadInt32(row + IntPtr.Size);
                if (type == 0 && name != null) list.Add(name); // STYPE_DISKTREE; special shares carry a high bit
            }
            return list;
        }
        finally { NetApiBufferFree(buf); }
    }

    // ---- search in subfolders: Enter in the filter box ----
    string searchQuery; // set while the list shows search results instead of the folder
    bool searching;
    public bool InSearch => searchQuery != null;
    const int MaxResults = 10_000;

    public void Search(string query)
    {
        if (IsServer(Dir)) return;
        searchQuery = query;
        searching = true;
        int v = ++navVersion;
        sizing?.Cancel();
        watcher?.Dispose(); watcher = null; // results span the whole subtree; a refresh runs the search again
        var results = new System.Collections.ObjectModel.ObservableCollection<Entry>();
        ShowItems(results, null);
        string root = Dir;
        bool wild = query.IndexOfAny(new[] { '*', '?' }) >= 0, hidden = ShowHidden;
        Task.Run(() =>
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | (hidden ? 0 : FileAttributes.Hidden),
            };
            var batch = new List<Entry>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int found = 0;
            void Flush()
            {
                var b = batch; batch = new();
                Dispatcher.BeginInvoke(() => { if (v != navVersion) return; foreach (var e in b) results.Add(e); UpdateFooter(); });
            }
            try
            {
                foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
                {
                    if (v != navVersion) return;
                    if (!(wild ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(query, fsi.Name)
                               : fsi.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
                    bool isDir = fsi is DirectoryInfo;
                    var where = System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(fsi.FullName));
                    batch.Add(new Entry
                    {
                        Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, Modified = fsi.LastWriteTime, Location = where == "." ? "" : where,
                        IsCut = CutPaths.Contains(fsi.FullName),
                        Size = isDir ? SizeCache.Get(fsi.FullName, fsi.LastWriteTimeUtc) : ((FileInfo)fsi).Length,
                    });
                    if (++found >= MaxResults) break;
                    if (clock.ElapsedMilliseconds > 150) { Flush(); clock.Restart(); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // what was found so far still shows
            Flush();
            Dispatcher.BeginInvoke(() =>
            {
                if (v != navVersion) return;
                searching = false;
                UpdateFooter();
                StartSizing(); StartVersions(); StartIcons(); StartThumbs();
            });
        });
    }

    /// Esc in the filter box: out of search results, or just clear the filter.
    public void ClearFilter()
    {
        if (searchQuery != null) Navigate(Dir, record: false);
        else FilterBox.Clear();
    }

    class Scale(double f) : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => (double)v * f;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    public void Refresh()
    {
        if (searchQuery != null) Search(searchQuery);
        else Navigate(Dir, (List.SelectedItem as Entry)?.Name, record: false);
    }

    // Folder sizes: walk each subfolder in the background, fill cells as they finish.
    void StartSizing()
    {
        sizing?.Cancel();
        var cts = sizing = new CancellationTokenSource();
        var todo = items.Where(e => e.IsDir && !e.IsUp && e.Size == -1).ToList();
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
            : searchQuery != null
                ? $"{items.Count} found for “{searchQuery}”{(searching ? "  ·  searching…" : items.Count >= MaxResults ? $"  ·  stopped at {MaxResults}" : "")}  ·  Esc goes back"
            : $"{folders} folders  ·  {files} files  ·  {MainWindow.Fmt(FolderTotal)}{Pending()}";

        string Pending()
        {
            int left = Items.Count(e => e.Size == -1);
            if (left == 0) return "";
            int cached = Items.Count(e => e.IsDir && e.Size >= 0);
            return cached > 0 ? $"  ·  measuring {left} folders ({cached} cached)" : $"  ·  measuring {left} folders";
        }
    }

    void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var parts = new List<(string Name, string Path)>();
        if (!IsServer(Dir))
            for (var d = new DirectoryInfo(Dir); d != null; d = d.Parent)
                parts.Insert(0, (d.Parent != null ? d.Name : d.Name.TrimEnd('\\').Split('\\')[^1], d.FullName)); // "C:", or the share's name
        if (Dir.StartsWith(@"\\")) parts.Insert(0, (ServerOf(Dir), ServerOf(Dir)));
        for (int i = 0; i < parts.Count; i++)
        {
            var path = parts[i].Path;
            var b = new Button { Content = new TextBlock { Text = parts[i].Name }, Style = (Style)FindResource("Ghost") };
            b.Click += (_, _) => { Navigate(path); List.Focus(); };
            Crumbs.Children.Add(b);
            // Explorer's chevron: the folders inside this one, to jump sideways without going up first
            var next = i + 1 < parts.Count ? parts[i + 1].Path : null;
            var chevron = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 8, Foreground = MainWindow.Hex("#80FFFFFF") },
                Style = (Style)FindResource("Chevron"), ToolTip = "Folders in " + parts[i].Name,
            };
            chevron.Click += (_, _) => ShowSubfolders(chevron, path, next);
            Crumbs.Children.Add(chevron);
        }
    }

    async void ShowSubfolders(Button anchor, string folder, string current)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = ShowHidden ? 0 : FileAttributes.Hidden };
        var dirs = await Task.Run(() =>
        {
            if (IsServer(folder)) return NetShares(folder)?.Select(s => $@"{folder}\{s}").ToList() ?? new();
            try { return Directory.EnumerateDirectories(folder, "*", opts).Take(500).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string>(); }
        });
        if (dirs.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 480 };
        foreach (var d in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var path = d;
            var mi = new MenuItem
            {
                Header = new TextBlock { Text = System.IO.Path.GetFileName(d) }, // a string header would eat underscores as access keys
                FontWeight = d.Equals(current, StringComparison.OrdinalIgnoreCase) ? FontWeights.SemiBold : FontWeights.Normal,
            };
            mi.Click += (_, _) => { Navigate(path); List.Focus(); };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // Browser-style tabs: folder icon + name + close (x), active one lifted with an accent underline.
    string tabsShown = "";

    void BuildTabs()
    {
        var signature = string.Join("|", tabs) + "@" + tab + "#" + tabs.Count;
        if (signature == tabsShown) return;
        tabsShown = signature;
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
            TabStrip.Children.Add(b);
        }
        FitTabs();
        Dispatcher.BeginInvoke(UpdateStripShape, DispatcherPriority.Loaded); // after the tabs have their sizes
    }

    // ---- dragging tabs: within a pane to reorder, or across to the other pane ----
    static (PaneView Pane, string Path)? dragTab; // static: the other pane must see what is being dragged
    Point tabDragFrom;
    int tabDragIndex = -1;
    int dropIndex = -1;

    /// Which tab sits under a point in the strip, or -1.
    int TabIndexAt(Point p)
    {
        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            var tabButton = (FrameworkElement)TabStrip.Children[i];
            var left = tabButton.TranslatePoint(new Point(0, 0), TabStrip).X;
            if (p.X >= left && p.X < left + tabButton.ActualWidth) return i;
        }
        return -1;
    }

    /// Where the dragged tab would land, from the pointer's x over the tab strip.
    int InsertIndexAt(DragEventArgs e)
    {
        var x = e.GetPosition(TabStrip).X;
        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            var tabButton = (FrameworkElement)TabStrip.Children[i];
            var left = tabButton.TranslatePoint(new Point(0, 0), TabStrip).X;
            if (x < left + tabButton.ActualWidth / 2) return i;
        }
        return TabStrip.Children.Count;
    }


    void Tabs_DragOver(object s, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = dragTab != null && e.Data.GetDataPresent("FileLabsTab") ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.None) { ShowTabDropLine(-1); return; }
        ShowTabDropLine(InsertIndexAt(e));
    }

    // DragLeave bubbles from every child, so it only counts when the pointer really left the band.
    void Tabs_DragLeave(object s, DragEventArgs e)
    {
        var p = e.GetPosition(TabBand);
        if (p.X < 0 || p.Y < 0 || p.X > TabBand.ActualWidth || p.Y > TabBand.ActualHeight) ShowTabDropLine(-1);
    }

    void ShowTabDropLine(int index)
    {
        if (index == dropIndex) return; // only on change: setting it every move is what flickers
        dropIndex = index;
        if (index < 0) { TabDropLine.Visibility = Visibility.Collapsed; return; }
        double x = TabStrip.Children.Count == 0 ? 0
            : index < TabStrip.Children.Count
                ? ((FrameworkElement)TabStrip.Children[index]).TranslatePoint(new Point(0, 0), TabBand).X
                : ((FrameworkElement)TabStrip.Children[^1]).TranslatePoint(new Point(0, 0), TabBand).X + ((FrameworkElement)TabStrip.Children[^1]).ActualWidth;
        Canvas.SetLeft(TabDropLine, x - 1.5);
        Canvas.SetTop(TabDropLine, 4);
        TabDropLine.Height = Math.Max(0, TabBand.ActualHeight - 4);
        TabDropLine.Visibility = Visibility.Visible;
    }

    void Tabs_Drop(object s, DragEventArgs e)
    {
        int at = dropIndex;
        ShowTabDropLine(-1);
        if (dragTab is not { } drag || at < 0) return;
        e.Handled = true;
        if (drag.Pane == this)
        {
            int from = tabs.IndexOf(drag.Path);
            if (from < 0 || at == from || at == from + 1) return;
            var active = tabs[tab];
            tabs.RemoveAt(from);
            tabs.Insert(at > from ? at - 1 : at, drag.Path);
            tab = tabs.IndexOf(active);
            BuildTabs();
        }
        else
        {
            drag.Pane.TakeTabAway(drag.Path);
            tabs.Insert(Math.Clamp(at, 0, tabs.Count), drag.Path);
            tab = at;
            Navigate(drag.Path, record: false);
            Activated?.Invoke();
        }
        Settings.Save();
    }

    /// Removes a tab that has been dragged to the other pane; a pane always keeps one.
    void TakeTabAway(string path)
    {
        int i = tabs.IndexOf(path);
        if (i < 0) return;
        if (tabs.Count == 1) { tabs[0] = System.IO.Path.GetPathRoot(path) ?? path; Navigate(tabs[0], record: false); return; }
        tabs.RemoveAt(i);
        if (i < tab || tab == tabs.Count) tab--;
        Navigate(Dir, record: false);
    }

    // ---- remembering the open tabs ----
    public IReadOnlyList<string> OpenTabs => tabs;
    public int ActiveTab => tab;

    public void Restore(List<string> paths, int active)
    {
        var usable = paths.Where(Directory.Exists).ToList();
        if (usable.Count == 0) return;
        tabs.Clear();
        tabs.AddRange(usable);
        tab = Math.Clamp(active, 0, tabs.Count - 1);
        Navigate(Dir, record: false);
    }

    // Each tab gets up to 170 px; when they don't all fit, they shrink evenly so the + stays visible.
    const double TabMax = 170;
    void FitTabs()
    {
        double room = TabBand.ActualWidth - 4 /* left edge */ - NewTabBox.ActualWidth - 8 /* keep some strip visible */;
        if (room > 0) TabStrip.Width = Math.Min(tabs.Count * TabMax, room);
        Dispatcher.BeginInvoke(UpdateStripShape, DispatcherPriority.Loaded);
    }
    void TabBand_SizeChanged(object s, SizeChangedEventArgs e) { FitTabs(); UpdateStripShape(); }

    // The strip, minus the active tab. Built by hand rather than stretched, so the corner radii stay
    // round whatever the tab width is.
    const double TabRadius = 8, TabFlare = 8, StripTop = 4;
    void UpdateStripShape()
    {
        double w = TabBand.ActualWidth, h = TabBand.ActualHeight;
        if (w <= 0 || h <= 0) return;
        StripShape.Width = w; StripShape.Height = h;

        var band = new RectangleGeometry(new Rect(0, 0, w, h), 9, 9);
        Geometry shape = band;
        if (tab >= 0 && tab < TabStrip.Children.Count && TabStrip.Children[tab] is FrameworkElement activeTab)
        {
            double x = activeTab.TranslatePoint(new Point(0, 0), TabBand).X, tw = activeTab.ActualWidth;
            var cut = new StreamGeometry();
            using (var g = cut.Open())
            {
                g.BeginFigure(new Point(x - TabFlare, h), true, true);
                g.QuadraticBezierTo(new Point(x, h), new Point(x, h - TabFlare), true, true);           // flare out, bottom left
                g.LineTo(new Point(x, StripTop + TabRadius), true, true);
                g.ArcTo(new Point(x + TabRadius, StripTop), new Size(TabRadius, TabRadius), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(x + tw - TabRadius, StripTop), true, true);
                g.ArcTo(new Point(x + tw, StripTop + TabRadius), new Size(TabRadius, TabRadius), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(x + tw, h - TabFlare), true, true);
                g.QuadraticBezierTo(new Point(x + tw, h), new Point(x + tw + TabFlare, h), true, true); // flare out, bottom right
            }
            cut.Freeze();
            shape = new CombinedGeometry(GeometryCombineMode.Exclude, band, cut);
        }
        shape.Freeze();
        StripShape.Data = shape;
    }

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
    public void Up()
    {
        if (IsServer(Dir)) return;
        var name = System.IO.Path.GetFileName(Dir.TrimEnd('\\'));
        if (Directory.GetParent(Dir) is { } p) Navigate(p.FullName, name);
        else if (Dir.StartsWith(@"\\")) Navigate(ServerOf(Dir), name); // share root → the server's shares
    }

    public void OpenInBackgroundTab(string path) { tabs.Insert(tab + 1, path); BuildTabs(); }

    // ---- selection by pattern (TC's Num+ / Num- / Num*), and "select these" for Compare ----
    bool bulkSelect; // one footer update at the end instead of one per row

    public void SetSelection(Func<Entry, bool> keep)
    {
        bulkSelect = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (Entry e in view) if (!e.IsUp && keep(e)) List.SelectedItems.Add(e);
        }
        finally { bulkSelect = false; }
        UpdateFooter();
        StatsChanged?.Invoke();
    }

    /// `*.jpg;*.png` style patterns; "*.*" means everything, as in Total Commander. Selecting replaces the
    /// selection (the list always has its first row selected, which would otherwise tag along);
    /// deselecting takes the matches out of what is selected.
    public void SelectMatching(string patterns, bool select)
    {
        var list = patterns.Split(';', ',').Select(p => p.Trim()).Where(p => p != "").Select(p => p == "*.*" ? "*" : p).ToArray();
        bool Match(Entry e) => list.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, e.Name));
        var was = List.SelectedItems.Cast<Entry>().ToHashSet();
        SetSelection(e => select ? Match(e) : was.Contains(e) && !Match(e));
    }

    public void InvertSelection()
    {
        var was = List.SelectedItems.Cast<Entry>().ToHashSet();
        SetSelection(e => !was.Contains(e));
    }

    // Rename in place (F2), like Explorer: the row's name turns into an edit box.
    public event Action<Entry, string> RenameCommitted;
    Entry renamingEntry;

    public void BeginRename()
    {
        if (List.SelectedItem is not Entry e || renamingEntry != null) return;
        renamingEntry = e;
        e.Renaming = true;
        foreach (var other in items) other.Dimmed = other != e; // the edited row is the only bright one
    }

    void Rename_Loaded(object s, RoutedEventArgs e)
    {
        var box = (TextBox)s;
        box.Focus();
        var dot = System.IO.Path.GetFileNameWithoutExtension(box.Text).Length;
        box.Select(0, dot > 0 ? dot : box.Text.Length); // keep the extension out of the selection
    }

    void Rename_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Finish((TextBox)s, commit: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { Finish((TextBox)s, commit: false); e.Handled = true; }
    }

    // Clicking away keeps the new name, as Explorer does.
    void Rename_Done(object s, KeyboardFocusChangedEventArgs e) => Finish((TextBox)s, commit: true);

    void Finish(TextBox box, bool commit)
    {
        if (box.Tag is not Entry entry || !entry.Renaming) return;
        entry.Renaming = false;
        renamingEntry = null;
        foreach (var other in items) other.Dimmed = false;
        var name = box.Text.Trim();
        if (commit && name != "" && name != entry.Name) RenameCommitted?.Invoke(entry, name);
        else List.Focus();
    }

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
    void EndEditPath() { Suggest.IsOpen = false; PathBox.Visibility = Visibility.Collapsed; Crumbs.Visibility = Visibility.Visible; }
    void Crumbs_Click(object s, MouseButtonEventArgs e) { if (e.OriginalSource is Border) EditPath(); }
    string TypedPath => Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));

    // Preview, not KeyDown: the text box itself handles Up/Down (caret) before KeyDown would see them.
    void PathBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        bool open = Suggest.IsOpen && SuggestList.Items.Count > 0;
        switch (e.Key)
        {
            case Key.Down when open: SuggestList.SelectedIndex = Math.Min(SuggestList.SelectedIndex + 1, SuggestList.Items.Count - 1); break;
            case Key.Up when open: SuggestList.SelectedIndex = Math.Max(SuggestList.SelectedIndex - 1, -1); break;
            case Key.Tab when open: Complete((string)(SuggestList.SelectedItem ?? SuggestList.Items[0])); break;
            case Key.Enter:
                var target = open && SuggestList.SelectedItem is string pick ? pick : TypedPath;
                EndEditPath(); Navigate(target); List.Focus(); break;
            case Key.Escape when open: Suggest.IsOpen = false; break;
            case Key.Escape: EndEditPath(); List.Focus(); break;
            default: return;
        }
        e.Handled = true;
    }
    void PathBox_Lost(object s, KeyboardFocusChangedEventArgs e) => EndEditPath();

    // Folders that start with what has been typed after the last backslash, as you type.
    int suggestVersion;
    void PathBox_Changed(object s, TextChangedEventArgs e)
    {
        if (PathBox.Visibility != Visibility.Visible) return;
        var text = TypedPath;
        int v = ++suggestVersion;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = ShowHidden ? 0 : FileAttributes.Hidden };
        Task.Run(() =>
        {
            List<string> found = new();
            int cut = text.LastIndexOf('\\');
            if (cut >= 0)
            {
                string parent = text[..(cut + 1)], prefix = text[(cut + 1)..];
                try
                {
                    if (IsServer(parent)) found = NetShares(parent.TrimEnd('\\'))?.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(x => parent + x).ToList() ?? new();
                    else if (Directory.Exists(parent)) found = Directory.EnumerateDirectories(parent, prefix + "*", opts).Take(12).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            found.RemoveAll(f => f.Equals(text.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); // already typed in full
            Dispatcher.BeginInvoke(() =>
            {
                if (v != suggestVersion || PathBox.Visibility != Visibility.Visible) return;
                SuggestList.ItemsSource = found;
                Suggest.IsOpen = found.Count > 0;
            });
        });
    }

    void Complete(string path)
    {
        PathBox.Text = path.TrimEnd('\\') + "\\";
        PathBox.CaretIndex = PathBox.Text.Length;
    }

    // Mouse down, handled: the list must not take focus, or the path box would lose it and close.
    void Suggest_MouseDown(object s, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is ListBoxItem { Content: string path }) { EndEditPath(); Navigate(path); List.Focus(); }
        e.Handled = true;
    }

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
        var q = searchQuery != null ? "" : FilterBox.Text; // search results are already the matches
        bool wild = q.IndexOfAny(new[] { '*', '?' }) >= 0;
        view.Filter = q == "" ? null : o => o is Entry { IsUp: true }
            || (wild ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(q, ((Entry)o).Name) : ((Entry)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase));
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
            FetchOverlays(todo, cts.Token); // after all the icons: badges are per file, so slower
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Sync badges. The shell asks every overlay handler about every file (~4 ms each), so this runs
    // last, on the icon thread, and sets only what changed.
    void FetchOverlays(IEnumerable<Entry> todo, CancellationToken token)
    {
        foreach (var e in todo)
        {
            if (token.IsCancellationRequested) return;
            var badge = Thumbnails.Overlay(e.Path);
            if (badge != e.Overlay) Dispatcher.BeginInvoke(() => e.Overlay = badge);
        }
    }

    // The sync apps tell the shell when a file's status changes (SHChangeNotify); MainWindow passes
    // the path on. A burst during a sync is gathered and re-asked once.
    readonly HashSet<string> overlayQueue = new(StringComparer.OrdinalIgnoreCase);
    DispatcherTimer overlayTimer;

    public void OverlayChanged(string path)
    {
        bool whole = path.Equals(Dir, StringComparison.OrdinalIgnoreCase);
        if (!whole && !items.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        overlayQueue.Add(whole ? "*" : path);
        if (overlayTimer == null)
        {
            overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            overlayTimer.Tick += (_, _) =>
            {
                overlayTimer.Stop();
                var todo = items.Where(e => overlayQueue.Contains("*") || overlayQueue.Contains(e.Path)).ToList();
                overlayQueue.Clear();
                var token = (icons ??= new()).Token;
                var t = new Thread(() => FetchOverlays(todo, token)) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            };
        }
        overlayTimer.Stop();
        overlayTimer.Start();
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
        // a click on text lands on a Run, which isn't a Visual: step up through the logical tree there
        while (d != null && d is not ListViewItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListViewItem)?.DataContext as Entry;
    }

    static bool InTextBox(object source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not TextBox) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d != null;
    }

    void Drag_Down(object s, MouseButtonEventArgs e)
    {
        // A press inside the rename box belongs to the text, not to a file drag.
        if (renamingEntry != null || InTextBox(e.OriginalSource)) { dragArmed = false; keepSelection = null; return; }
        var row = FindRow(e.OriginalSource as DependencyObject);
        dragArmed = row is { IsUp: false };
        dragStart = e.GetPosition(List);
        keepSelection = null;
        // A press on empty space (not a row, scrollbar or column header) may become a selection rectangle.
        // In Details, so may a press on a row outside its Name column, as in Explorer — a full folder
        // has no empty space. Shift keeps its usual range selection.
        bool otherColumn = row != null && Mode == ViewMode.Details && dragStart.X > NameColumn.ActualWidth && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (otherColumn && e.ClickCount == 2) { List.SelectedItem = row; OpenSelected(); e.Handled = true; return; }
        marqueeArmed = (row == null || otherColumn) && !OnChrome(e.OriginalSource as DependencyObject);
        if (marqueeArmed)
        {
            marqueeRow = otherColumn ? row : null;
            marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? List.SelectedItems.Cast<Entry>().ToHashSet() : new();
            dragArmed = false;
            List.Focus();
            e.Handled = true; // selection is ours from here: on release (a click) or while dragging (a rectangle)
            return;
        }
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
        if (marqueeArmed)
        {
            if (Marquee.Visibility != Visibility.Visible) // a click, not a drag
            {
                bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (marqueeRow != null && ctrl) { if (List.SelectedItems.Contains(marqueeRow)) List.SelectedItems.Remove(marqueeRow); else List.SelectedItems.Add(marqueeRow); }
                else if (marqueeRow != null) List.SelectedItem = marqueeRow;
                else if (!ctrl) SetSelection(_ => false); // empty space clears the selection, as in Explorer
            }
            marqueeArmed = false;
            Marquee.Visibility = Visibility.Collapsed;
            List.ReleaseMouseCapture();
            UpdateFooter();
            StatsChanged?.Invoke();
        }
    }

    // ---- drag to select: a rectangle from empty space; Ctrl adds to the selection ----
    bool marqueeArmed;
    Entry marqueeRow; // the row pressed outside its Name column, selected if it turns out to be a click
    HashSet<Entry> marqueeBase = new();

    static bool OnChrome(DependencyObject d)
    {
        while (d != null && d is not ListView)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void MarqueeMove(MouseEventArgs e)
    {
        var p = e.GetPosition(List);
        var d = p - dragStart;
        if (Marquee.Visibility != Visibility.Visible)
        {
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            Marquee.Visibility = Visibility.Visible;
            List.CaptureMouse(); // keep getting moves when the pointer leaves the list
        }
        var rect = new Rect(dragStart, p);
        rect.Intersect(new Rect(0, 0, List.ActualWidth, List.ActualHeight));
        if (rect.IsEmpty) return;
        Canvas.SetLeft(Marquee, rect.X); Canvas.SetTop(Marquee, rect.Y);
        Marquee.Width = rect.Width; Marquee.Height = rect.Height;

        // Only rows on screen have containers (the list is virtualized); off-screen rows keep their state.
        var want = new HashSet<Entry>(marqueeBase);
        foreach (Entry item in view)
        {
            if (item.IsUp || List.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem c || !c.IsVisible) continue;
            var bounds = c.TransformToAncestor(List).TransformBounds(new Rect(c.RenderSize));
            if (bounds.IntersectsWith(rect)) want.Add(item);
        }
        bulkSelect = true;
        try
        {
            foreach (var x in List.SelectedItems.Cast<Entry>().Where(x => !want.Contains(x)).ToList()) List.SelectedItems.Remove(x);
            var have = List.SelectedItems.Cast<Entry>().ToHashSet();
            foreach (var x in want) if (!have.Contains(x)) List.SelectedItems.Add(x);
        }
        finally { bulkSelect = false; }
        UpdateFooter();
    }

    void Drag_Move(object s, MouseEventArgs e)
    {
        if (marqueeArmed && e.LeftButton == MouseButtonState.Pressed) { MarqueeMove(e); return; }
        if (!dragArmed || renamingEntry != null || e.LeftButton != MouseButtonState.Pressed) return;
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
