using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FileExplorer;

public class Entry : INotifyPropertyChanged
{
    public string Name { get; init; }
    public string Path { get; init; }
    public bool IsDir { get; init; }
    DateTime modified;
    public DateTime Modified
    {
        get => modified;
        set { modified = value; Changed(nameof(Modified)); Changed(nameof(DateText)); Changed(nameof(FullDate)); Changed(nameof(Age)); Changed(nameof(AgeBrush)); Changed(nameof(AgeTextBrush)); Changed(nameof(Tip)); Changed(nameof(DateGroup)); }
    }
    public bool IsUp => Name == "..";

    /// Group by date (Details): Explorer's groups, newest first. ".." heads the list in a group of its own.
    public string DateGroup => IsUp ? "" : DateGroups[DateGroupOf(Modified, DateTime.Now)];
    internal static readonly string[] DateGroups =
        { "Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Last month", "Earlier this year", "A long time ago" };
    internal static int DateGroupOf(DateTime t, DateTime now)
    {
        var day = now.Date;
        if (t >= day) return 0; // a date in the future counts as today
        if (t >= day.AddDays(-1)) return 1;
        int back = ((int)day.DayOfWeek - (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var week = day.AddDays(-back);
        if (t >= week) return 2;
        if (t >= week.AddDays(-7)) return 3;
        var month = new DateTime(day.Year, day.Month, 1);
        if (t >= month) return 4;
        if (t >= month.AddMonths(-1)) return 5;
        return t >= new DateTime(day.Year, 1, 1) ? 6 : 7;
    }
    // A folder, or a shortcut to one: where a middle-click opens and a drop on the row lands. The shortcut is
    // read once, the first time it is asked (a drag asks on every move).
    string shortcutFolder;
    public string FolderTarget => IsDir ? Path : !Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? null
        : (shortcutFolder ??= MainWindow.ShortcutFolder(Path) ?? "") is { Length: > 0 } t ? t : null;
    public string Location { get; init; } = ""; // search results: the folder it was found in, relative to where the search started
    public string Where => Location == "" ? "" : "   in " + Location;

    long size = -1; // -1 = folder size still computing, -2 = not measured (network shares)
    public long Size { get => size; set { size = value; Changed(nameof(Size)); Changed(nameof(SizeText)); Changed(nameof(Tip)); } }
    ImageSource thumb;
    public ImageSource Thumb { get => thumb; set { thumb = value; Changed(nameof(Thumb)); Changed(nameof(NoThumb)); } }
    public bool NoThumb => thumb == null;
    public int ThumbSize; // what it was fetched at, so zooming in knows when a bigger one is needed
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
    bool dropHover;
    public bool DropHover { get => dropHover; set { if (dropHover != value) { dropHover = value; Changed(nameof(DropHover)); } } } // a drag is over it and would land inside
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
    public string KindColor => Kind.color;
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
    public Brush AgeBrush { get { if (NoDate) return Brushes.Transparent; var b = new SolidColorBrush(AgeColor); b.Freeze(); return b; } }
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
                : d.TotalDays < 1 ? $"{(int)d.TotalHours}h {d.Minutes}'"
                : d.TotalDays < 60 ? $"{(int)d.TotalDays}d"
                : d.TotalDays < 730 ? $"{(int)(d.TotalDays / 30)}mo"
                : $"{(int)(d.TotalDays / 365)}y";
        }
    }
    public string DateText => NoDate ? "" : Modified.ToString("yyyy-MM-dd  HH:mm");
    public string FullDate => NoDate ? null : Modified.ToString("dddd, yyyy-MM-dd HH:mm:ss");

    // The hover tooltip, as Explorer's: the whole name (a long one is cut off in the row), then type, size, date.
    public string Tip => IsUp ? null : string.Join("\n", new[]
    {
        Name, Location != "" ? "In: " + Location : null, "Type: " + Thumbnails.TypeName(Name, IsDir),
        size >= 0 ? "Size: " + MainWindow.Fmt(size) : null, NoDate ? null : "Date modified: " + Modified.ToString("yyyy-MM-dd HH:mm:ss"),
    }.Where(line => line != null));
}

// Names in Explorer's order. Sorting by character code put "_drafts" and "~temp" after every letter
// ('_' comes after 'Z'); the shell's own comparison puts them first, and "file2" before "file10".
public sealed class ExplorerOrder : IComparer<string>
{
    public static readonly ExplorerOrder Instance = new();
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string a, string b) => StrCmpLogicalW(a ?? "", b ?? "");
}

// Folders first, ".." always on top, then by the chosen column. With Settings → Files → "Sort shortcuts to folders
// with the folders", a shortcut that opens a folder counts as one (its target is read once per row, then kept).
class EntrySort(string key, bool desc) : IComparer
{
    readonly bool shortcuts = Settings.ShortcutsWithFolders;
    bool Folder(Entry e) => e.IsDir || shortcuts && e.FolderTarget != null;

    public int Compare(object a, object b)
    {
        var x = (Entry)a; var y = (Entry)b;
        if (x.IsUp != y.IsUp) return x.IsUp ? -1 : 1;
        if (Folder(x) != Folder(y)) return Folder(x) ? -1 : 1;
        int r = key switch
        {
            "Size" => x.Size.CompareTo(y.Size),
            "Type" => ExplorerOrder.Instance.Compare(x.Ext, y.Ext) is var t && t != 0 ? t : ExplorerOrder.Instance.Compare(x.Name, y.Name),
            "Modified" => x.Modified.CompareTo(y.Modified),
            "Version" => ExplorerOrder.Instance.Compare(x.Version, y.Version) is var v && v != 0 ? v : ExplorerOrder.Instance.Compare(x.Name, y.Name),
            _ => ExplorerOrder.Instance.Compare(x.Name, y.Name),
        };
        return desc ? -r : r;
    }
}

// While any UI Automation client runs — a password manager, a screen reader, Logitech Options, ShareX,
// ChatGPT's "work with apps" — WPF re-examines the automation tree after every layout, and each row
// exposed ~10 elements (icon, badge, name, the hidden rename box, the date badge…). Measured on a
// machine with such clients: 370 ms of every folder change went there. A row is now one item,
// named after its file, which is also what a screen reader should announce.
public class FileList : ListView
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ListPeer(this);

    // How far the rows of the inactive pane recede. Per row, not the list's own Opacity: a selected row stays
    // solid (App.xaml), so its name stays white and its fill opaque in the pane that isn't active too.
    public static readonly DependencyProperty RowFadeProperty = DependencyProperty.Register(nameof(RowFade), typeof(double), typeof(FileList), new PropertyMetadata(1.0));
    public double RowFade { get => (double)GetValue(RowFadeProperty); set => SetValue(RowFadeProperty, value); }

    // Esc arrives as the character 27 too, and type-to-find compares names the culture's way, which
    // ignores control characters: every name "started with" it, so Esc jumped to the first row.
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && char.IsControl(e.Text[0])) return;
        base.OnTextInput(e);
    }

    class ListPeer(FileList owner) : System.Windows.Automation.Peers.ListViewAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.ItemAutomationPeer CreateItemAutomationPeer(object item) => new RowPeer(item, this);
    }

    class RowPeer(object item, System.Windows.Automation.Peers.SelectorAutomationPeer list) : System.Windows.Automation.Peers.ListBoxItemAutomationPeer(item, list)
    {
        protected override List<System.Windows.Automation.Peers.AutomationPeer> GetChildrenCore() => null;
        protected override string GetNameCore() => Item is Entry e ? e.Name : base.GetNameCore();
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
    public event Action<Entry> OpenArchived; // Enter on a file inside an archive: MainWindow opens it from a temp copy
    public event Action<List<string>, string, bool> FilesDropped; // sources, destination, move
    public event Action RightDragSelected; // a right-button rectangle was let go: the menu for what it selected
    public static event Action<string> Visited;   // every folder a pane shows, for the history menu
    public static event Action<string> TabClosed; // for Reopen closed tab (Ctrl+Shift+T)

    readonly List<string> tabs = new() { "" };
    readonly List<bool> pins = new() { false }; // beside tabs, index for index: a pinned tab cannot be closed
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
        SetSort(sortKey, sortDesc);
        Loaded += (_, _) => SetView(Settings.Views.TryGetValue(Name, out var v) ? v : ViewMode.Details);        List.GotKeyboardFocus += (_, _) => Activated?.Invoke();
        PreviewMouseDown += (_, _) => Activated?.Invoke(); // anywhere in the pane: tabs, path, list
        renameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        renameTimer.Tick += (_, _) => ClickRename();
        List.PreviewKeyDown += (_, _) => { renameTimer.Stop(); renameCandidate = null; }; // typing in between cancels, as in Explorer
        // FindRow, not "OriginalSource is FrameworkElement": a double-click on the name lands on a Run.
        // In the rename box a double-click selects a word, it doesn't open the file.
        List.MouseDoubleClick += (_, e) => { if (!InTextBox(e.OriginalSource) && FindRow(e.OriginalSource as DependencyObject) != null) OpenSelected(); };
        List.SelectionChanged += (_, _) => { if (bulkSelect) return; UpdateFooter(); StatsChanged?.Invoke(); };
        // Ctrl+wheel steps through the view modes, smallest to largest, the way Explorer resizes icons.
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true; // otherwise the list scrolls as well
            Zoom(e.Delta > 0 ? 1 : -1);
        };
        // Settings → Smooth scrolling: the wheel glides as far as it would have jumped. Details and the icon views;
        // List scrolls sideways and keeps the plain wheel.
        List.PreviewMouseWheel += (_, e) =>
        {
            if (!Settings.SmoothScroll || e.Handled || Keyboard.Modifiers != ModifierKeys.None || FindScroller(List) is not { ScrollableHeight: > 0 } sv) return;
            e.Handled = true;
            int lines = SystemParameters.WheelScrollLines; // -1: a screen a notch
            Glide(sv, -e.Delta / 120.0 * (lines > 0 ? lines * (Mode == ViewMode.Details ? 16 : 32) : sv.ViewportHeight));
        };
        // During a rectangle the row with the keyboard scrolls away and is recycled; WPF then asked to bring it back into
        // view and the list jumped back to where the drag began, over and over (trace: offset 281 → 69), so the count went
        // 50 → 100 → 50 (Rico, 1.0.61). Nothing is brought into view while a rectangle is drawn.
        // (Caught under the list's ScrollViewer, which acts on it first: ArmMarquee hooks its content presenter.)
        // A row's tooltip only over the row's columns: the empty strip past them in Details is the folder's (Rico, 1.0.61)
        List.AddHandler(ToolTipOpeningEvent, new ToolTipEventHandler((_, e) => { if (FindRow(e.OriginalSource as DependencyObject) != null && RowAt(e.OriginalSource as DependencyObject) == null) e.Handled = true; }));
        // During a rectangle the list holds the mouse, so the wheel lands on the list itself and never reaches its scroller.
        List.MouseWheel += (_, e) =>
        {
            if (e.Handled || !Marqueeing || FindScroller(List) is not { } sv) return;
            int lines = SystemParameters.WheelScrollLines;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 120.0 * (lines > 0 ? lines * 16 : sv.ViewportHeight));
            e.Handled = true;
        };
        // Middle click, as in a browser: a folder (or a shortcut to one) opens in a tab behind this one, a tab closes, and
        // on the list's empty space (Settings: Middle-click autoscroll) moving the mouse scrolls the list.
        List.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            var at = e.OriginalSource as DependencyObject;
            // Empty space as a left press sees it: in Details a row's other columns and the strip past them count too.
            // FindRow alone took any press on a folder's row for the folder, so autoscroll there opened it (Rico, 1.0.53).
            var row = Settings.Autoscroll ? RowAt(at) : FindRow(at);
            if (row != null && Settings.Autoscroll && Mode == ViewMode.Details && e.GetPosition(List).X + ScrollX > NameColumn.ActualWidth) row = null;
            if (row?.FolderTarget is { } folder) OpenInBackgroundTab(folder);
            else if (!Settings.Autoscroll || row != null || OnChrome(at) || !StartAutoscroll(e.GetPosition(List))) return;
            e.Handled = true;
        };
        // on the pane, so it comes before the list's own handling of the press (selecting, a drag, a rectangle)
        PreviewMouseDown += (_, e) => { if (autoscrolling) { StopAutoscroll(); swallowUp = e.ChangedButton; e.Handled = true; } };
        PreviewMouseUp += (_, e) =>
        {
            if (swallowUp == e.ChangedButton) { swallowUp = null; e.Handled = true; } // the click that ended it does nothing else
            else if (autoscrolling && autoscrollHeld && e.ChangedButton == MouseButton.Middle)
            {
                autoscrollHeld = false;
                if (autoscrollMoved) StopAutoscroll(); // held and moved, as in a browser: letting go ends it
                e.Handled = true;
            }
        };
        List.PreviewKeyDown += (_, e) => { if (autoscrolling) { StopAutoscroll(); if (e.Key == Key.Escape) e.Handled = true; } };
        LostMouseCapture += (_, _) => StopAutoscroll();
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
        List.PreviewMouseRightButtonDown += RightDrag_Down;
        List.PreviewMouseRightButtonUp += RightDrag_Up;
        List.DragOver += Drop_Over;
        List.Drop += Drop_Drop;
        List.DragLeave += (_, e) =>
        {
            // DragLeave bubbles up from every row, and rows scrolling away under a resting pointer raise it too: acting
            // on those turned the lit folder off and stopped the edge scroll until the next DragOver, a flicker and a
            // stutter all the way down (1.0.50, seen with a real drag). Only leaving the list itself ends them.
            var at = e.GetPosition(List);
            if (at.X >= 0 && at.Y >= 0 && at.X < List.ActualWidth && at.Y < List.ActualHeight) return;
            ShowDropRow(null);
            ScrollAtEdge(null);
            WheelWhileDragging(null);
        };
        // Not a row, so DropTarget gives the folder shown: the strip always means "here".
        FolderStrip.DragOver += Drop_Over;
        FolderStrip.Drop += (s, e) => { FolderStrip.Background = Brushes.Transparent; Drop_Drop(s, e); };
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
            var data = new DataObject("FileLabsTab", "");
            data.SetData("FileLabsFolder", dragTab.Value.Path); // dropped on Quick access, the tab's folder is pinned there
            try { DragDrop.DoDragDrop(TabStrip, data, DragDropEffects.Move | DragDropEffects.Link); }
            finally { dragTab = null; }
        };
        TabStrip.PreviewMouseLeftButtonUp += (_, _) => tabDragIndex = -1;

        // Name fills whatever the other columns leave (shrinks with the pane, absorbs width when you resize
        // another column) until you drag Name's own edge: from then on its width is yours, remembered, and a
        // list wider than the pane scrolls sideways. Double-clicking Name's edge hands it back to the pane.
        List.SizeChanged += (_, _) => { if (Mode == ViewMode.Details) FitName(); };
        var widthProp = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var c in ((GridView)List.View).Columns)
        {
            // widths survive restarts (saved with the other settings on exit)
            if (Settings.ColumnWidths.TryGetValue(colKey[c], out var w)) c.Width = w;
            if (c == NameColumn)
                widthProp.AddValueChanged(c, (_, _) => { if (double.IsNaN(c.Width)) { Settings.ColumnWidths.Remove("Name"); FitName(); } });
            else
                widthProp.AddValueChanged(c, (_, _) => { Settings.ColumnWidths[colKey[c]] = c.Width; if (!ResizingColumn) FitName(); });
        }
        // The line where Name ends follows its width, the sideways scroll and the list's height.
        ((System.ComponentModel.INotifyPropertyChanged)NameColumn).PropertyChanged += (_, e) => { if (e.PropertyName == "ActualWidth") PlaceNameEdge(); };
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            PlaceNameEdge();
            if (Marqueeing) MarqueeMove(Mouse.GetPosition(List)); // a wheel or the edge moved the rows under a rectangle
        }));
        MarqueeLayer.SizeChanged += (_, _) => PlaceNameEdge();
        // While a column's edge is dragged Name stays put and gives the room back on release. Shrunk on
        // every step, it held the dragged edge still under the pointer, and the header (which measures each
        // step from where the drag began) added the whole distance again each time: 40 px of mouse made a
        // column 420 px wider. Name's own edge used to end here too, and FitName took the new width back.
        List.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) =>
            {
                if (Mode != ViewMode.Details) return;
                if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb { TemplatedParent: GridViewColumnHeader { Column: var col } } && col == NameColumn)
                {   // a double-click on the edge also ends a "drag" of 0 px; that one gives Name back to the pane
                    if (e.HorizontalChange != 0) Settings.ColumnWidths["Name"] = NameColumn.Width;
                }
                else FitName();
            }), handledEventsToo: true); // the header marks it handled
    }

    public IEnumerable<Entry> Items => items.Where(e => !e.IsUp);
    // This PC's rows are drive roots: nothing that copies, moves, deletes, renames or drags them may get them, so in
    // This PC the selection is empty to every command (the menu there is Windows' own, MainWindow.ShowMenu).
    public List<Entry> Selected => Dir == ThisPC || ArchiveRoot != null ? new() : List.SelectedItems.Cast<Entry>().Where(e => !e.IsUp).ToList();
    public long FolderTotal => Items.Sum(e => Math.Max(0, e.Size));
    public bool SizesPending => Items.Any(e => e.Size == -1);

    // Active pane: lighter surface and accent edge. Inactive: darker and slightly faded, so it recedes.
    // The three changes are animated together, or switching panes reads as a flash.
    readonly SolidColorBrush cardFill = new(), cardEdge = new();
    readonly SolidColorBrush glowColor = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();

    public void SetActive(bool on)
    {
        if (on && !isActivePane) paneActivatedAt = Environment.TickCount64;
        isActivePane = on;
        if (Card.Background != cardFill) { Card.Background = cardFill; Card.BorderBrush = cardEdge; Glow.BorderBrush = glowColor; }
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentEdge"]).Color;
        Animate(cardFill, ((SolidColorBrush)MainWindow.Hex(on ? "#12FFFFFF" : "#0A000000")).Color);
        Animate(cardEdge, on ? accent : Color.FromArgb(0x0F, 255, 255, 255));
        glowColor.Color = Color.FromRgb(accent.R, accent.G, accent.B); // a discreet glow around the active pane only
        var glowTo = on ? 0.7 : 0;
        if (Motion.On) Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glowTo, Quick) { EasingFunction = Ease });
        else { Glow.BeginAnimation(OpacityProperty, null); Glow.Opacity = glowTo; }
        var fade = on ? 1 : 0.88;
        if (Motion.On) List.BeginAnimation(FileList.RowFadeProperty, new DoubleAnimation(fade, Quick) { EasingFunction = Ease });
        else { List.BeginAnimation(FileList.RowFadeProperty, null); List.RowFade = fade; }
    }

    static void Animate(SolidColorBrush brush, Color to)
    {
        if (Motion.On) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Quick) { EasingFunction = Ease });
        else { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = to; }
    }

    // Set at the main window's first frame. Before it, a finished listing waits at Background priority, so the window
    // appears first and the rows after: first frame 5.2 → 3.3 s on a throttled CPU (1.0.44).
    public static bool WindowShown;
    public bool Listed; // its first folder is on screen (the startup clock waits for it)
    int navVersion; // a newer Navigate wins; a slow share must not overwrite the folder you moved on to

    public void Navigate(string dir, string select = null, bool record = true)
    {
        if (dir.Equals(ThisPC, StringComparison.OrdinalIgnoreCase)) { NavigateThisPC(select ?? Dir, record); return; }
        if (ArchiveOf(dir) is { } inArchive) { NavigateArchive(dir.TrimEnd('\\'), inArchive, select ?? ChildToward(dir, Dir), record); return; }
        select ??= ChildToward(dir, Dir);
        if (IsServer(dir)) { NavigateServer(dir.TrimEnd('\\'), select, record); return; }
        DirectoryInfo di;
        try
        {
            di = new DirectoryInfo(dir);
            // A network folder is checked by the read below instead: a server that is off answers only
            // after the SMB timeout, 21 s (measured), and this is the UI thread.
            if (!IsRemote(di.FullName) && !di.Exists) throw new DirectoryNotFoundException($"{dir} does not exist");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            Error?.Invoke(ex.Message);
            return;
        }

        int v = GoTo(di.FullName, record);

        // Show the folder as we last saw it, immediately; the read below replaces it a moment later.
        // Without this, every tab switch waits on the disk — measured at 1.2 s for a cold folder.
        var cached = Cached(di.FullName);
        // A refresh of the folder on screen: its rows are the cached ones, and showing them again would
        // rebuild the list, drop the selection and jump the scroll — every 400 ms in a busy folder.
        bool onScreen = cached != null && cached.Items == items;
        if (cached != null && !onScreen) { ShowItems(cached.Items, select, animate: false); StartPasses(); }

        // Reading the folder and asking the drive for free space both block — on a network share for
        // the best part of a second — so they happen off the UI thread and the list is handed over
        // when it's ready. The window never freezes on a folder change.
        Task.Run(() =>
        {
            var (used, driveText) = DriveUsage(di.FullName);

            // Always read: a folder's timestamp moves when a child is added, removed or renamed, but not
            // when a file inside is rewritten — a growing download or log kept its old size forever.
            long stamp = Stamp(di.FullName);
            var list = ReadFolder(di, ShowHidden, out var error);

            Dispatcher.BeginInvoke(() =>
            {
                if (v != navVersion) return; // the user has already moved on
                if (error != null)
                {
                    Error?.Invoke(error);
                    if (cached == null) ShowItems(new List<Entry>(), null); // not the last folder's files under this one's name
                    return;
                }
                // Same files as the rows on screen: only sizes and dates are brought up to date, so the
                // scroll position, the selection and the icons stay. Otherwise the list is replaced.
                bool shown = cached != null && cached.Items == items;
                if (shown && Patch(list))
                {
                    Remember(di.FullName, cached.Items, stamp);
                    if (select != null && items.FirstOrDefault(e => e.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) is { } row)
                    {
                        List.SelectedItem = row;
                        List.ScrollIntoView(row);
                        // a file shown from outside, in the folder already on screen: the keyboard goes to it too
                        if (List.IsKeyboardFocusWithin && renamingEntry == null) Dispatcher.BeginInvoke(FocusSelectedRow, DispatcherPriority.Loaded);
                    }
                }
                else
                {
                    Remember(di.FullName, list, stamp);
                    ShowItems(list, select, animate: cached == null, keep: shown);
                    StartPasses();
                }
                if (pendingSelect != null) ApplyPendingSelect();
                if (watcher?.Path != di.FullName) Watch();
                if (driveText != null)
                {
                    UsedBar.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = UsedBar.Parent, Converter = new Scale(used) });
                    DriveInfoText.Text = driveText;
                }
                Listed = true;
                UpdateFooter();
                StatsChanged?.Invoke();
                StartPrefetch();
                // at startup: after the window's first frame
            }, WindowShown ? DispatcherPriority.Normal : DispatcherPriority.Background);
        });
    }

    static (double Used, string Text) DriveUsage(string path)
    {
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(path));
            if (drive.IsReady)
                return (1 - (double)drive.AvailableFreeSpace / drive.TotalSize,
                        $"{MainWindow.Fmt(drive.AvailableFreeSpace)} free of {MainWindow.Fmt(drive.TotalSize)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return (-1, null);
    }

    void StartPasses()
    {
        StartSizing();
        StartVersions();
        StartThumbs();
        StartIcons();
    }

    // ---- the folder cache: what makes tab switching and re-opening a folder instant ----
    sealed class Listing { public List<Entry> Items; public long Stamp; public long UsedAt; }

    // Per pane, so a rename in one pane can't dim rows in the other. 200 folders is a few MB.
    readonly Dictionary<string, Listing> listCache = new(StringComparer.OrdinalIgnoreCase);
    const int MaxFolders = 200, MaxEntries = 60_000;

    static long Stamp(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    Listing Cached(string path)
    {
        lock (listCache)
        {
            if (!listCache.TryGetValue(path, out var hit)) return null;
            hit.UsedAt = DateTime.UtcNow.Ticks;
            return hit;
        }
    }

    void Remember(string path, List<Entry> list, long stamp)
    {
        lock (listCache)
        {
            listCache[path] = new Listing { Items = list, Stamp = stamp, UsedAt = DateTime.UtcNow.Ticks };
            while (listCache.Count > MaxFolders || listCache.Values.Sum(l => l.Items.Count) > MaxEntries)
            {
                var oldest = listCache.OrderBy(kv => kv.Value.UsedAt).First().Key;
                if (oldest.Equals(path, StringComparison.OrdinalIgnoreCase)) break; // never drop what we just read
                listCache.Remove(oldest);
            }
        }
    }

    static List<Entry> ReadFolder(DirectoryInfo di, bool hidden, out string error)
    {
        var list = new List<Entry>();
        error = null;
        try
        {
            foreach (var fsi in di.EnumerateFileSystemInfos())
            {
                if (!hidden && fsi.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                bool isDir = fsi is DirectoryInfo;
                var e = new Entry { Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, Modified = fsi.LastWriteTime, IsCut = CutPaths.Contains(fsi.FullName) };
                e.Size = isDir ? SizeCache.Peek(fsi.FullName, fsi.LastWriteTimeUtc) : ((FileInfo)fsi).Length; // never waits for the cache
                list.Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            error = ex.Message;
        }
        return list;
    }

    /// Reads folders into the cache in the background, so opening one of them costs nothing: the other
    /// tabs first (those are one click away), then the subfolders of the one being shown.
    CancellationTokenSource prefetching;

    void StartPrefetch(IEnumerable<string> extra = null)
    {
        prefetching?.Cancel();
        var cts = prefetching = new CancellationTokenSource();
        var todo = (extra ?? Enumerable.Empty<string>())
            .Concat(tabs.Where(t => t != "" && t != ThisPC && !t.Equals(Dir, StringComparison.OrdinalIgnoreCase)))
            .Concat(items.Where(e => e.IsDir && !e.IsUp).Select(e => e.Path).Take(40))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool hidden = ShowHidden;
        Task.Run(() =>
        {
            foreach (var path in todo)
            {
                if (cts.IsCancellationRequested) return;
                long stamp = Stamp(path);
                if (stamp == 0 || Cached(path) is { } hit && hit.Stamp == stamp) continue; // already fresh
                var list = ReadFolder(new DirectoryInfo(path), hidden, out var error);
                if (error == null && !cts.IsCancellationRequested) Dispatcher.BeginInvoke(() => Remember(path, list, stamp));
            }
        }, cts.Token);
    }

    /// The sidebar's folders are one click away too; MainWindow hands them over once at startup.
    public void PrefetchPaths(IEnumerable<string> paths) => StartPrefetch(paths);

    /// Makes `path` the current tab's folder (history, crumbs, tabs) and leaves any search.
    /// Returns the navigation number that later results must still match.
    int GoTo(string path, bool record)
    {
        if (record && tabs[tab] != "" && !tabs[tab].Equals(path, StringComparison.OrdinalIgnoreCase)) { back.Push(tabs[tab]); fwd.Clear(); }
        tabs[tab] = path;
        ArchiveRoot = null; // NavigateArchive sets it again right after
        Visited?.Invoke(path);
        // each folder keeps the order it was last sorted in; the rest are by name. Set before its rows are shown.
        var (key, desc) = Settings.FolderSorts.TryGetValue(path, out var sort) ? sort : ("Name", false);
        if (key != sortKey || desc != sortDesc) ShowSort(key, desc);
        if (searchQuery != null) { searchQuery = null; searchMore = null; FilterBox.Text = ""; } // the query would hide most of the new folder
        BuildCrumbs();
        BuildTabs();
        return ++navVersion;
    }

    /// `keep`: the same folder, read again — the selection (by name) and the scroll position survive,
    /// unless `select` names a row to show instead (a rename, a new folder).
    void ShowItems(IList<Entry> list, string select, bool animate = true, bool keep = false)
    {
        bool focused = List.IsKeyboardFocusWithin;
        var scroller = keep ? FindScroller(List) : null;
        double offset = scroller?.VerticalOffset ?? 0, across = scroller?.HorizontalOffset ?? 0;
        var wasSelected = keep ? List.SelectedItems.Cast<Entry>().Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        // where the first selected row sat: if the selection is gone (deleted), the row that moved up into its place
        int firstSelected = keep && view != null && List.SelectedItems.Count > 0 ? List.SelectedItems.Cast<object>().Min(view.IndexOf) : -1;
        var left = items;
        items = list;
        view = new ListCollectionView((IList)items) { CustomSort = new EntrySort(sortKey, sortDesc) };
        ApplyGrouping();
        ApplyFilter();
        List.ItemsSource = view;
        // The rows just left stay in the folder cache; their thumbnails don't. Kept there, every photo
        // folder ever opened stayed in memory; coming back takes them from the thumbnail cache instead.
        if (left != list) foreach (var e in left) if (e.Thumb != null) { e.Thumb = null; e.ThumbSize = 0; }
        if (keep && select == null)
        {
            SetSelection(e => wasSelected.Contains(e.Name));
            // Deleted (Delete, Shift+Delete → Enter, or from outside): the keyboard carries on from the next row, as in
            // Explorer, not from the first row of the folder
            if (List.SelectedItems.Count == 0 && firstSelected >= 0 && view.Count > 0)
                List.SelectedItem = view.GetItemAt(Math.Min(firstSelected, view.Count - 1));
            // no scroller in a pane never shown (the right one, with one pane): a file changing in its folder crashed the app
            scroller?.ScrollToVerticalOffset(offset);
            scroller?.ScrollToHorizontalOffset(across);
        }
        else
        {
            List.SelectedItem = items.FirstOrDefault(e => e.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? view.Cast<Entry>().FirstOrDefault();
            if (select != null && List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        }
        // the rows the keyboard was on are gone; without this the next arrow key started over at the first row
        if (focused) Dispatcher.BeginInvoke(FocusSelectedRow, DispatcherPriority.Loaded);
        if (animate) FadeInList();
        UpdateFooter();
        StatsChanged?.Invoke();
    }

    /// Dragging out of an archive (the user, 1.0.61: three files from a ZIP to the other pane did nothing): what is
    /// selected is extracted to a temp folder first and those copies are dragged, to the other pane or any app, as Explorer
    /// does with a ZIP. ponytail: extracted before the drag starts, so a big selection waits a moment with the wait cursor;
    /// delayed rendering (the data written only on drop) would remove that wait.
    // A drag out of an archive needs the files before it can start, so that one waits (with the wait cursor): Windows
    // asks for the dragged paths as the drag begins. ponytail: delayed rendering (virtual files) would lift that, if big
    // archives make it matter. Ctrl+C has no such hurry and takes them out off the UI thread.
    internal string[] ExtractForDrag()
    {
        if (ArchiveRoot is not var (archive, inner) || SelectedNames() is not { Count: > 0 } names) return Array.Empty<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try { return TakeOut(archive, inner, names); }
        finally { Mouse.OverrideCursor = null; }
    }

    internal async Task<string[]> ExtractForClipboard()
    {
        if (ArchiveRoot is not var (archive, inner) || SelectedNames() is not { Count: > 0 } names) return Array.Empty<string>();
        return await Task.Run(() => TakeOut(archive, inner, names));
    }

    List<string> SelectedNames() => List.SelectedItems.Cast<Entry>().Where(e => !e.IsUp).Select(e => e.Name).ToList();

    /// Extracts these entries to a new folder under %TEMP%\FileLabs\drag; the paths that came out. Any thread.
    string[] TakeOut(string archive, string inner, List<string> names)
    {
        var drags = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileLabs", "drag");
        var temp = System.IO.Path.Combine(drags, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // the copies of earlier drags, once they're an hour old (a drop that is still copying from one is long done)
            if (Directory.Exists(drags))
                foreach (var old in new DirectoryInfo(drags).EnumerateDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddHours(-1)))
                    try { old.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Directory.CreateDirectory(temp);
            Archive.Extract(archive, names, inner, temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Dispatcher.BeginInvoke(() => Error?.Invoke($"Couldn't take them out of {System.IO.Path.GetFileName(archive)}: {ex.Message}"));
            return Array.Empty<string>();
        }
        return names.Select(n => System.IO.Path.Combine(temp, n)).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
    }

    // ---- after a paste or drop: the new arrivals selected once the folder has been read again ----
    // (also a tab coming back: its selection, the row the keyboard was on, and how far it was scrolled)
    HashSet<string> pendingSelect;
    string pendingCurrent;
    double? pendingOffset;
    bool pendingFocus;
    public void SelectAfterRefresh(IEnumerable<string> names, bool focus)
    {
        pendingSelect = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        pendingCurrent = null; pendingOffset = null;
        pendingFocus = focus;
    }

    void ApplyPendingSelect()
    {
        var (names, current, offset) = (pendingSelect, pendingCurrent, pendingOffset);
        pendingSelect = null; pendingCurrent = null; pendingOffset = null;
        if (names.Count > 0 && !items.Any(e => names.Contains(e.Name))) return; // not here (yet): the selection stays as it was
        if (names.Count > 0 || offset != null) SetSelection(e => names.Contains(e.Name)); // a tab left with nothing selected comes back so
        var first = view.Cast<Entry>().FirstOrDefault(e => current != null ? e.Name.Equals(current, StringComparison.OrdinalIgnoreCase) : names.Contains(e.Name))
                    ?? view.Cast<Entry>().FirstOrDefault(e => names.Contains(e.Name));
        if (first == null && offset == null) return;
        if (offset == null) List.ScrollIntoView(first);
        bool focus = pendingFocus;
        Dispatcher.BeginInvoke(() =>
        {
            if (offset is { } y) FindScroller(List)?.ScrollToVerticalOffset(y);
            if (focus && first != null && List.ItemContainerGenerator.ContainerFromItem(first) is UIElement row) row.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ---- each tab keeps its selection and scroll while another is shown (Rico, 1.0.61); kept for the session ----
    readonly Dictionary<string, (HashSet<string> Selected, string Current, double Offset)> tabMemory = new(StringComparer.OrdinalIgnoreCase);

    void RememberTab()
    {
        if (Dir == "" || view == null || searchQuery != null) return;
        tabMemory[Dir] = (List.SelectedItems.Cast<Entry>().Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                          (Keyboard.FocusedElement as ListViewItem)?.DataContext is Entry { } f ? f.Name : (List.SelectedItem as Entry)?.Name,
                          FindScroller(List)?.VerticalOffset ?? 0);
    }

    void RecallTab(bool focus)
    {
        if (!tabMemory.TryGetValue(Dir, out var m) || m.Selected.Count == 0 && m.Offset == 0) return;
        (pendingSelect, pendingCurrent, pendingOffset, pendingFocus) = (m.Selected, m.Current, m.Offset, focus);
        if (Cached(Dir)?.Items == items) ApplyPendingSelect(); // the cached rows are on screen already; otherwise when the read lands
    }

    void FocusSelectedRow() { if (List.SelectedItem is { } sel && List.ItemContainerGenerator.ContainerFromItem(sel) is UIElement row) row.Focus(); }

    /// Brings the rows on screen up to date with a fresh read of the same folder, in place. False when
    /// files were added, removed or renamed — then the list has to be replaced.
    bool Patch(List<Entry> fresh)
    {
        if (fresh.Count != items.Count) return false;
        var rows = new Dictionary<string, Entry>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in items) rows[e.Name] = e;
        foreach (var f in fresh)
            if (!rows.TryGetValue(f.Name, out var e) || e.IsDir != f.IsDir) return false;
        bool resize = false, rethumb = false, reversion = false;
        var reicon = new List<Entry>();
        foreach (var f in fresh)
        {
            var e = rows[f.Name];
            if (e.Modified != f.Modified)
            {
                e.Modified = f.Modified;
                if (e.IsDir) { e.Size = f.Size; resize |= f.Size == -1; } // -1: its contents changed, measure again
                else if (e.Thumb != null) { e.Thumb = null; e.ThumbSize = 0; rethumb = true; } // an edited photo
                // an installer still being written when it was first listed had no version or icon yet
                if (!e.IsDir && Versioned.Contains(System.IO.Path.GetExtension(e.Name))) reversion = true;
                if (!e.IsDir && OwnIcon.Contains(System.IO.Path.GetExtension(e.Name))) reicon.Add(e);
            }
            if (!e.IsDir && e.Size != f.Size) e.Size = f.Size;
        }
        if (resize) StartSizing();
        if (rethumb) StartThumbs();
        if (reversion) StartVersions();
        if (reicon.Count > 0) StartIcons(reicon);
        return true;
    }

    static ScrollViewer FindScroller(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    // ---- motion: short, and only where something actually changed ----
    static readonly Duration Quick = TimeSpan.FromMilliseconds(140);
    static readonly IEasingFunction Ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    /// A folder that was read from disk fades in. A folder served from the cache does not — it is
    /// already on screen and fading it would turn "instant" into "blinking".
    void FadeInList() => Motion.Enter(List, from: 0.45, ms: 140); // begun after the frame that builds the rows, which can be long

    // ---- \\server: its shares, as folders (DirectoryInfo can't open a bare server name) ----
    static bool IsServer(string p) => p.StartsWith(@"\\") && p.Trim('\\').Length > 0 && !p.Trim('\\').Contains('\\');

    /// A share or a mapped network drive: anything asked of it may wait for the network. Telling which
    /// it is doesn't (the drive type comes from the mapping, not from the server).
    public static bool IsRemote(string p)
    {
        if (p.StartsWith(@"\\")) return true;
        try { return new DriveInfo(System.IO.Path.GetPathRoot(p)).DriveType == DriveType.Network; }
        catch (ArgumentException) { return false; }
    }
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

    /// This PC (Favorites, and Up from a drive): the drives, as rows. Its path is the shell's parsing name, which every
    /// file call refuses, so nothing can be pasted, dropped or created "in" it.
    public const string ThisPC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public static string NameOf(string path) => path.Equals(ThisPC, StringComparison.OrdinalIgnoreCase) ? "This PC" : System.IO.Path.GetFileName(path.TrimEnd('\\'));

    // ---- inside a ZIP or TAR (1.0.60): its folders as rows; the path goes on past the archive, "C:\a\b.zip\docs" ----
    /// The archive this pane shows, and the folder inside it ('/'-separated, "" = its top); null on a real folder.
    public (string Archive, string Inner)? ArchiveRoot { get; private set; }

    /// The archive a path runs through, if any. Only paths that name one are looked at on disk.
    public static (string Archive, string Inner)? ArchiveOf(string path)
    {
        if (string.IsNullOrEmpty(path) || !(path.Contains(".zip", StringComparison.OrdinalIgnoreCase) || path.Contains(".tar", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".tgz", StringComparison.OrdinalIgnoreCase))) return null;
        path = path.TrimEnd('\\');
        for (var at = path; at != null && at.Length > 3; at = System.IO.Path.GetDirectoryName(at))
            if (Archive.IsArchiveName(at) && File.Exists(at))
                return (at, at.Length == path.Length ? "" : path[(at.Length + 1)..].Replace('\\', '/'));
        return null;
    }

    /// This PC and the inside of an archive: shown like folders, but nothing can be copied, moved or created "in" them.
    public static bool IsVirtual(string dir) => dir == ThisPC || ArchiveOf(dir) != null;

    void NavigateArchive(string dir, (string Archive, string Inner) at, string select, bool record)
    {
        int v = GoTo(dir, record);
        ArchiveRoot = at;
        watcher?.Dispose(); watcher = null;
        sizing?.Cancel();
        Task.Run(() =>
        {
            try { return (Archive.List(at.Archive, at.Inner), (string)null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { return (null, ex.Message); }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (v != navVersion) return;
            var (items, error) = t.Result;
            if (error != null) { Error?.Invoke($"Can't read {System.IO.Path.GetFileName(at.Archive)}: {error}"); return; }
            ShowItems(items.Select(i => new Entry { Name = i.Name, Path = dir + "\\" + i.Name, IsDir = i.IsDir, Size = i.Size, Modified = i.Modified }).ToList(), select);
            DriveInfoText.Text = $"Inside {System.IO.Path.GetFileName(at.Archive)}";
            UsedBar.Width = 0;
            StartIcons();
        }));
    }

    void NavigateThisPC(string from, bool record)
    {
        int v = GoTo(ThisPC, record);
        watcher?.Dispose(); watcher = null;
        sizing?.Cancel();
        Task.Run(() => DriveInfo.GetDrives().Select(d =>
        {
            // a drive that isn't ready (an empty card reader) is listed without asking it more
            string label = "";
            try { if (d.IsReady) label = d.VolumeLabel; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (label == "") label = d.DriveType switch { DriveType.Network => "Network drive", DriveType.Removable => "Removable disk", DriveType.CDRom => "DVD drive", _ => "Local Disk" };
            return new Entry { Name = $"{d.Name.TrimEnd('\\')}  {label}", Path = d.Name, IsDir = true, Size = -2 }; // as in Drives: by letter
        }).ToList()).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (v != navVersion) return;
            var drives = t.Result;
            ShowItems(drives, drives.FirstOrDefault(e => e.Path.Equals(from, StringComparison.OrdinalIgnoreCase))?.Name);
            DriveInfoText.Text = "";
            UsedBar.Width = 0;
            StartIcons();
        }));
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
    Action searchMore; // a search that paused at Settings.SearchLimit: carries on from where it stopped

    /// What the filter box asks for: words of the name (with * and ?), and terms for the rest —
    /// size:>10MB · size:<1KB · size:>=10MB · size:<=1GB · size:10MB-1GB (both ends included) · within:7d (d, w, m, y, or today) · after:2026-10-01 · before:2026-01-01 ·
    /// re:^IMG_\d+ (a regular expression on the name) · content:"some text" (inside files, on Enter only).
    /// Quotes keep spaces in a term. Sizes count in 1000s, like every size in File Labs.
    internal sealed class SearchQuery
    {
        public string Name = "";
        public System.Text.RegularExpressions.Regex Pattern;
        public string Content;
        public long MinSize = -1, MaxSize = long.MaxValue;
        public DateTime After = DateTime.MinValue, Before = DateTime.MaxValue;
        public string Error; // a term that couldn't be read; the search doesn't run
        public bool FilesOnly => Content != null || MinSize >= 0 || MaxSize != long.MaxValue;

        public static SearchQuery Parse(string text, DateTime now)
        {
            var q = new SearchQuery();
            var words = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(\w+:)?(""[^""]*""|\S+)"))
            {
                var key = m.Groups[1].Value.ToLowerInvariant().TrimEnd(':');
                var val = m.Groups[2].Value.Trim('"');
                switch (key)
                {
                    case "size" when ReadSizes(val, q): break;
                    case "within" when Ago(val, now) is { } since: q.After = since; break;
                    case "after" when DateTime.TryParse(val, out var a): q.After = a; break;
                    case "before" when DateTime.TryParse(val, out var b): q.Before = b; break;
                    case "content" when val != "": q.Content = val; break;
                    case "re":
                        try { q.Pattern = new(val, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                        catch (ArgumentException) { q.Error = $"\"{val}\" isn't a valid regular expression"; }
                        break;
                    case "size" or "within" or "after" or "before" or "content": q.Error = $"Can't read \"{m.Value}\""; break;
                    default: words.Add(m.Value.Trim('"')); break;
                }
            }
            q.Name = string.Join(" ", words);
            return q;
        }

        static bool ReadSizes(string v, SearchQuery q)
        {
            if (v.Split('-') is [var lo, var hi] && Bytes(lo) is { } l && Bytes(hi) is { } h) { q.MinSize = l; q.MaxSize = h; return true; }
            if (v.StartsWith(">=") && Bytes(v[2..]) is { } least) { q.MinSize = least; return true; }
            if (v.StartsWith("<=") && Bytes(v[2..]) is { } most) { q.MaxSize = most; return true; }
            if (v.StartsWith('>') && Bytes(v[1..]) is { } min) { q.MinSize = min + 1; return true; }
            if (v.StartsWith('<') && Bytes(v[1..]) is { } max) { q.MaxSize = max - 1; return true; }
            if (Bytes(v) is { } at) { q.MinSize = at; return true; } // size:1GB = at least 1 GB
            return false;
        }

        internal static long? Bytes(string v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^(\d+(?:[.,]\d+)?)\s*(b|kb|k|mb|m|gb|g|tb|t)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            double unit = m.Groups[2].Value.ToLowerInvariant() switch { "kb" or "k" => 1e3, "mb" or "m" => 1e6, "gb" or "g" => 1e9, "tb" or "t" => 1e12, _ => 1 };
            return (long)(n * unit);
        }

        static DateTime? Ago(string v, DateTime now)
        {
            if (v.Equals("today", StringComparison.OrdinalIgnoreCase)) return now.Date;
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^(\d+)([dwmy])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            int n = int.Parse(m.Groups[1].Value);
            return m.Groups[2].Value.ToLowerInvariant() switch { "d" => now.AddDays(-n), "w" => now.AddDays(-7 * n), "m" => now.AddMonths(-n), _ => now.AddYears(-n) };
        }

        /// Everything but the contents: what the live filter can check without reading a file.
        public bool MatchesMeta(string name, bool isDir, long size, DateTime modified)
        {
            if (Name != "" && !(Name.IndexOfAny(new[] { '*', '?' }) >= 0 ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(Name, name)
                                                                          : name.Contains(Name, StringComparison.OrdinalIgnoreCase))) return false;
            if (Pattern != null)
                try { if (!Pattern.IsMatch(name)) return false; }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
            if (isDir && FilesOnly) return false; // a folder's size isn't known while searching, and it has no text
            if (!isDir && (size < MinSize || size > MaxSize)) return false;
            return modified >= After && modified < Before;
        }

        /// Files up to 64 MB, read as text (UTF-8, or the encoding their BOM names). Files kept only in the cloud are
        /// skipped rather than downloaded. ponytail: whole-file read; a streaming search would lift the 64 MB ceiling.
        public bool MatchesContent(FileInfo f)
        {
            if (Content == null) return true;
            const FileAttributes InCloud = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
            if (f.Length > 64_000_000 || (f.Attributes & InCloud) != 0) return false;
            try { return File.ReadAllText(f.FullName).Contains(Content, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// Enter in the filter box. The same query again, on a search that paused at the limit, carries on instead.
    public void Search(string query)
    {
        if (query == searchQuery && searchMore != null) { var more = searchMore; searchMore = null; searching = true; UpdateFooter(); more(); return; }
        if (IsServer(Dir) || Dir == ThisPC || ArchiveRoot != null) { if (ArchiveRoot != null) Error?.Invoke("Search works in folders, not inside an archive; the filter still narrows this one"); return; }
        var find = SearchQuery.Parse(query, DateTime.Now);
        if (find.Error != null) { Error?.Invoke(find.Error); return; }
        searchQuery = query;
        searching = true;
        searchMore = null;
        int v = ++navVersion;
        sizing?.Cancel();
        watcher?.Dispose(); watcher = null; // results span the whole subtree; a refresh runs the search again
        var results = new System.Collections.ObjectModel.ObservableCollection<Entry>();
        ShowItems(results, null);
        string root = Dir;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (ShowHidden ? 0 : FileAttributes.Hidden),
        };
        IEnumerator<FileSystemInfo> walk = null;
        void Run() => Task.Run(() =>
        {
            var batch = new List<Entry>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int found = 0, limit = Settings.SearchLimit;
            bool paused = false;
            // Results go to the list in batches of at most 500, and the next batch waits until the list has taken the last:
            // a fast walk (no limit, a big drive) can't pile up more work on the UI than it gets through. Only the size of
            // each delivery is capped, never how many results the search finds.
            System.Windows.Threading.DispatcherOperation shown = null;
            void Flush()
            {
                shown?.Wait();
                var b = batch; batch = new();
                shown = Dispatcher.BeginInvoke(() => { if (v != navVersion) return; foreach (var e in b) results.Add(e); UpdateFooter(); });
            }
            try
            {
                walk ??= new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts).GetEnumerator();
                while (walk.MoveNext())
                {
                    if (v != navVersion) { walk.Dispose(); return; }
                    var fsi = walk.Current;
                    bool isDir = fsi is DirectoryInfo;
                    if (!find.MatchesMeta(fsi.Name, isDir, isDir ? 0 : ((FileInfo)fsi).Length, fsi.LastWriteTime)
                        || !isDir && !find.MatchesContent((FileInfo)fsi)) continue;
                    var where = System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(fsi.FullName));
                    batch.Add(new Entry
                    {
                        Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, Modified = fsi.LastWriteTime, Location = where == "." ? "" : where,
                        IsCut = CutPaths.Contains(fsi.FullName),
                        Size = isDir ? SizeCache.Get(fsi.FullName, fsi.LastWriteTimeUtc) : ((FileInfo)fsi).Length,
                    });
                    if (++found == limit) { paused = true; break; }
                    if (batch.Count >= 500 || clock.ElapsedMilliseconds > 150) { Flush(); clock.Restart(); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // what was found so far still shows
            if (!paused) walk?.Dispose(); // null when the folder itself couldn't be read
            Flush();
            Dispatcher.BeginInvoke(() =>
            {
                if (v != navVersion) { if (paused) walk.Dispose(); return; }
                searching = false;
                if (paused) searchMore = Run;
                UpdateFooter();
                StartSizing(); StartVersions(); StartIcons(); StartThumbs();
            });
        });
        Run();
    }

    // ---- the search options button: each choice writes its term into the filter box, replacing one of the same kind ----
    void SearchOptions_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SearchOptions, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Choice(string header, string kind, string term)
        {
            var mi = new MenuItem { Header = header, IsChecked = term != null && FilterBox.Text.Contains(term, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (_, _) => SetTerm(kind, term);
            return mi;
        }
        var size = new MenuItem { Header = "Size" };
        foreach (var (h, t) in new[] { ("Any size", null), ("Under 1 MB", "size:<1MB"), ("1 MB to 100 MB", "size:1MB-100MB"), ("Over 100 MB", "size:>100MB"), ("Over 1 GB", "size:>1GB") })
            size.Items.Add(Choice(h, "size", t));
        size.Items.Add(new Separator());
        var customSize = new MenuItem { Header = "Custom size…" };
        customSize.Click += (_, _) => { if (AskSize(FilterBox.Text) is var t && t != "") SetTerm("size", t); };
        size.Items.Add(customSize);
        var date = new MenuItem { Header = "Modified" };
        foreach (var (h, t) in new[] { ("Any time", null), ("Today", "within:today"), ("In the last 7 days", "within:7d"), ("In the last 30 days", "within:30d"), ("In the last year", "within:1y") })
        {
            var mi = Choice(h, "within", t);
            mi.Click += (_, _) => { SetTerm("after", null); SetTerm("before", null); }; // a preset replaces custom dates
            date.Items.Add(mi);
        }
        date.Items.Add(new Separator());
        var customDate = new MenuItem { Header = "Custom dates…", IsChecked = FilterBox.Text.Contains("after:", StringComparison.OrdinalIgnoreCase) || FilterBox.Text.Contains("before:", StringComparison.OrdinalIgnoreCase) };
        customDate.Click += (_, _) =>
        {
            if (AskDates(FilterBox.Text) is not var (after, before)) return;
            SetTerm("within", null); SetTerm("after", after); SetTerm("before", before);
        };
        date.Items.Add(customDate);
        menu.Items.Add(size);
        menu.Items.Add(date);
        menu.Items.Add(new Separator());
        var re = new MenuItem { Header = "Name matches a regular expression…" };
        re.Click += (_, _) => { if (Ask("Regular expression", "The name matches (case doesn't matter)", "re:") is { } v) SetTerm("re", $"re:\"{v}\""); };
        var content = new MenuItem { Header = "Contains text…", ToolTip = "Looks inside files up to 64 MB when you press Enter (files only in the cloud are skipped)" };
        content.Click += (_, _) => { if (Ask("Contains text", "Text inside the file", "content:") is { } v) SetTerm("content", $"content:\"{v}\""); };
        menu.Items.Add(re);
        menu.Items.Add(content);
        menu.Items.Add(new Separator());
        var go = new MenuItem { Header = "Search every subfolder", InputGestureText = "Enter", IsEnabled = FilterBox.Text.Trim() != "" };
        go.Click += (_, _) => { Search(FilterBox.Text.Trim()); FocusList(); };
        menu.Items.Add(go);
        menu.IsOpen = true;

        string Ask(string title, string label, string kind)
        {
            var current = System.Text.RegularExpressions.Regex.Match(FilterBox.Text, @"(?<!\S)" + kind + @"(""[^""]*""|\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.Trim('"');
            return PromptDialog.Ask(Window.GetWindow(this), title, label, current, "Use") is { Length: > 0 } v ? v.Replace("\"", "") : null;
        }
    }

    static readonly string[] SizeUnits = { "B", "KB", "MB", "GB" };

    /// The Custom size dialog: at least and at most, either or both, each in its own unit. Returns the size: term,
    /// null when both are left empty (any size), or "" for Cancel.
    string AskSize(string text)
    {
        var now = SearchQuery.Parse(text, DateTime.Now);
        var (minText, minUnit) = now.MinSize > 0 ? Shown(now.MinSize) : ("", 2);
        var (maxText, maxUnit) = now.MaxSize != long.MaxValue ? Shown(now.MaxSize) : ("", 2);
        var got = PromptDialog.Fields(Window.GetWindow(this), "Custom size",
                                      "Files of this size, both ends included. Leave a box empty for no limit that way. 1 KB = 1,000 bytes, as everywhere in File Labs.",
                                      new (string, string, string[], int)[] { ("At least", minText, SizeUnits, minUnit), ("At most", maxText, SizeUnits, maxUnit) },
                                      e => SizeTerm(e[0], e[1]).Error);
        return got == null ? "" : SizeTerm(got[0], got[1]).Term;
    }

    /// The term for an at-least and an at-most size (text and unit index), or what's wrong with them.
    internal static (string Term, string Error) SizeTerm((string Text, int Unit) least, (string Text, int Unit) most)
    {
        string lo = least.Text == "" ? null : least.Text.Replace(',', '.') + SizeUnits[least.Unit];
        string hi = most.Text == "" ? null : most.Text.Replace(',', '.') + SizeUnits[most.Unit];
        if (lo != null && SearchQuery.Bytes(lo) == null) return (null, $"“{least.Text}” isn't a size");
        if (hi != null && SearchQuery.Bytes(hi) == null) return (null, $"“{most.Text}” isn't a size");
        if (lo != null && hi != null && SearchQuery.Bytes(lo) > SearchQuery.Bytes(hi)) return (null, "At least is more than at most");
        return (lo, hi) switch
        {
            (null, null) => (null, null),
            (_, null) => ($"size:>={lo}", null),
            (null, _) => ($"size:<={hi}", null),
            _ => ($"size:{lo}-{hi}", null),
        };
    }

    static (string Text, int Unit) Shown(long bytes)
    {
        int unit = bytes >= 1e9 ? 3 : bytes >= 1e6 ? 2 : bytes >= 1e3 ? 1 : 0;
        return ((bytes / Math.Pow(1000, unit)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), unit);
    }

    /// The Custom dates dialog: on or after, before, or both for a range. Returns the after: and before: terms
    /// (null for an empty box), or null for Cancel.
    (string After, string Before)? AskDates(string text)
    {
        string Current(string kind) => System.Text.RegularExpressions.Regex.Match(text, @"(?<!\S)" + kind + @":(""[^""]*""|\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.Trim('"');
        var got = PromptDialog.Fields(Window.GetWindow(this), "Custom dates",
                                      "Modified on or after the first date, and before the second (that day not included). Fill one or both, as 2026-10-01.",
                                      new (string, string, string[], int)[] { ("On or after", Current("after"), null, 0), ("Before", Current("before"), null, 0) },
                                      e => DateTerms(e[0].Text, e[1].Text).Error);
        if (got == null) return null;
        var (after, before, _) = DateTerms(got[0].Text, got[1].Text);
        return (after, before);
    }

    /// after: and before: terms for two dates as typed (empty = no limit that way), or what's wrong with them.
    internal static (string After, string Before, string Error) DateTerms(string after, string before)
    {
        DateTime? Read(string v) => v == "" ? null
            : DateTime.TryParseExact(v, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) || DateTime.TryParse(v, out d) ? d.Date : DateTime.MinValue;
        var a = Read(after);
        var b = Read(before);
        if (a == DateTime.MinValue) return (null, null, $"“{after}” isn't a date");
        if (b == DateTime.MinValue) return (null, null, $"“{before}” isn't a date");
        if (a == null && b == null) return (null, null, "Fill at least one date");
        if (a >= b) return (null, null, "The first date has to come before the second");
        return (a is { } x ? $"after:{x:yyyy-MM-dd}" : null, b is { } y ? $"before:{y:yyyy-MM-dd}" : null, null);
    }

    /// Puts `term` in the filter box in place of any term of the same kind (null just removes it).
    void SetTerm(string kind, string term)
    {
        var rest = System.Text.RegularExpressions.Regex.Replace(FilterBox.Text, @"(?<!\S)" + kind + @":(""[^""]*""|\S+)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        FilterBox.Text = System.Text.RegularExpressions.Regex.Replace((rest + " " + term).Trim(), @"\s{2,}", " ");
        FilterBox.Focus();
        FilterBox.CaretIndex = FilterBox.Text.Length;
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

    /// `force` re-reads even if the folder's timestamp says nothing changed (Ctrl+R).
    public void Refresh(bool force = false)
    {
        if (force) lock (listCache) listCache.Remove(Dir); // rebuilt from scratch: folder sizes are measured again
        if (searchQuery != null) { searchMore = null; Search(searchQuery); }
        // Without force the rows on screen keep their own selection and scroll; rebuilt, the list is told
        // which row to put the cursor back on.
        else Navigate(Dir, force ? (List.SelectedItem as Entry)?.Name : null, record: false);
    }

    // Folder sizes: walk each subfolder in the background, fill cells as they finish.
    void StartSizing()
    {
        sizing?.Cancel();
        var cts = sizing = new CancellationTokenSource();
        var todo = items.Where(e => e.IsDir && !e.IsUp && e.Size == -1).ToList();
        if (todo.Count == 0) return;
        Task.Run(() =>
        {
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token }, e =>
            {
                long total;
                try { total = Measure(new DirectoryInfo(e.Path), cts.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // gone or unreadable: leave it at "…"
                Dispatcher.BeginInvoke(() => { e.Size = total; UpdateFooter(); StatsChanged?.Invoke(); });
            });
        }, cts.Token).ContinueWith(_ => { }); // swallow cancellation
    }

    static readonly EnumerationOptions OneLevel = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// A folder's size, walked one level at a time so that every folder on the way is remembered too:
    /// measuring D:\ used to cache D:\ alone, and opening any folder under it walked that whole subtree
    /// again. A remembered subfolder is not walked, so a scan that was cut short (you moved on) picks
    /// up from everything it had finished.
    internal static long Measure(DirectoryInfo dir, CancellationToken token)
    {
        long bytes = 0;
        foreach (var fsi in dir.EnumerateFileSystemInfos("*", OneLevel))
        {
            token.ThrowIfCancellationRequested();
            if (fsi is FileInfo f) { bytes += f.Length; continue; }
            var sub = (DirectoryInfo)fsi;
            long known = SizeCache.Get(sub.FullName, sub.LastWriteTimeUtc);
            bytes += known >= 0 ? known : Measure(sub, token);
        }
        // The timestamp as it is now, after the walk: NTFS keeps a copy of it in the parent's index
        // (what a listing reads), and opening the folder to walk it is what brings that copy up to
        // date. Stored from before the walk, it looked changed at the next visit and was walked again.
        dir.Refresh();
        SizeCache.Set(dir.FullName, dir.LastWriteTimeUtc, bytes);
        return bytes;
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

    int changeQueued;

    void Watch()
    {
        watcher?.Dispose();
        try
        {
            watcher = new FileSystemWatcher(Dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            // 10,000 files arriving are 10,000 events: one callback is queued to the UI until it has run, not one per event
            void Changed()
            {
                if (Interlocked.Exchange(ref changeQueued, 1) == 1) return;
                Dispatcher.BeginInvoke(() => { Volatile.Write(ref changeQueued, 0); debounce.Stop(); debounce.Start(); });
            }
            watcher.Created += (_, _) => Changed(); watcher.Deleted += (_, _) => Changed(); watcher.Changed += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
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
                ? $"{items.Count:N0} found for “{searchQuery}”{(searching ? "  ·  searching…" : searchMore != null ? "  ·  paused, Enter carries on" : "")}  ·  Esc goes back"
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
        if (Dir == ThisPC) parts.Add(("This PC", ThisPC));
        else if (!IsServer(Dir))
            for (var d = new DirectoryInfo(Dir); d != null; d = d.Parent)
                parts.Insert(0, (d.Parent != null ? d.Name : d.Name.TrimEnd('\\').Split('\\')[^1], d.FullName)); // "C:", or the share's name
        if (Dir.StartsWith(@"\\")) parts.Insert(0, (ServerOf(Dir), ServerOf(Dir)));
        for (int i = 0; i < parts.Count; i++)
        {
            var path = parts[i].Path;
            var b = new Button { Content = new TextBlock { Text = parts[i].Name }, Style = (Style)FindResource("Ghost") };
            b.Click += (_, _) => { Navigate(path); List.Focus(); };
            b.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { OpenInBackgroundTab(path); e.Handled = true; } };
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
            if (folder == ThisPC) return DriveInfo.GetDrives().Select(d => d.Name).ToList();
            if (IsServer(folder)) return NetShares(folder)?.Select(s => $@"{folder}\{s}").ToList() ?? new();
            try { return Directory.EnumerateDirectories(folder, "*", opts).Take(500).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<string>(); }
        });
        if (dirs.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 480 };
        foreach (var d in dirs.OrderBy(d => d, ExplorerOrder.Instance))
        {
            var path = d;
            var mi = new MenuItem
            {
                Header = new TextBlock { Text = System.IO.Path.GetFileName(d) is { Length: > 0 } n ? n : d }, // a drive (This PC's) by its root // a string header would eat underscores as access keys
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
        var signature = string.Join("|", tabs) + "@" + tab + "#" + tabs.Count + "^" + string.Join("", pins.Select(p => p ? 1 : 0));
        if (signature == tabsShown) return;
        tabsShown = signature;
        TabStrip.Children.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            int idx = i;
            var name = NameOf(tabs[i]);
            var close = new Button { Style = (Style)FindResource("TabClose"), ToolTip = "Close tab (Ctrl+W)" };
            if (tabs.Count == 1 && closeLast == null || pins[i]) close.Visibility = Visibility.Collapsed; // the last tab can't be closed (unless it closes the pane), nor a pinned one
            close.Click += (_, e) => { CloseTab(idx); e.Handled = true; };
            var row = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
            if (pins[i])
            {
                var pin = new TextBlock { Text = "\uE718", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 2, 0) };
                DockPanel.SetDock(pin, Dock.Right);
                row.Children.Add(pin);
            }
            row.Children.Add(MainWindow.ShellIcon(tabs[i], "\uE8B7", 16, new(0, 0, 7, 0)));
            row.Children.Add(new TextBlock { Text = name == "" ? tabs[i] : name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, Style = (Style)FindResource("BrowserTab"), Tag = i == tab ? "on" : null, Uid = i == tab - 1 ? "beforeActive" : "", ToolTip = tabs[i] };
            b.Click += (_, _) => SwitchTab(idx);
            b.ContextMenu = TabMenu(idx);
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
            var at = tabButton.TranslatePoint(new Point(0, 0), TabStrip);
            if (p.X >= at.X && p.X < at.X + tabButton.ActualWidth && p.Y >= at.Y && p.Y < at.Y + tabButton.ActualHeight) return i;
        }
        return -1;
    }

    /// Where the dragged tab would land: the row under the pointer, then the place in that row.
    int InsertIndexAt(DragEventArgs e)
    {
        var p = e.GetPosition(TabStrip);
        int lastInRow = -1;
        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            var tabButton = (FrameworkElement)TabStrip.Children[i];
            var at = tabButton.TranslatePoint(new Point(0, 0), TabStrip);
            if (TabStrip.Rows > 1 && (p.Y < at.Y || p.Y >= at.Y + tabButton.ActualHeight)) continue; // another row
            if (p.X < at.X + tabButton.ActualWidth / 2) return i;
            lastInRow = i;
        }
        return lastInRow >= 0 ? lastInRow + 1 : TabStrip.Children.Count;
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
        // beside the tab it lands before, or after the last tab of the row it was dropped in
        var before = index < TabStrip.Children.Count ? (FrameworkElement)TabStrip.Children[index] : null;
        var after = index > 0 ? (FrameworkElement)TabStrip.Children[index - 1] : null;
        var row = after != null && (before == null || before.TranslatePoint(new Point(0, 0), TabBand).Y > after.TranslatePoint(new Point(0, 0), TabBand).Y + 1)
            ? after : before;
        if (row == null) return;
        var at = row.TranslatePoint(new Point(0, 0), TabBand);
        double x = row == before ? at.X : at.X + row.ActualWidth;
        Canvas.SetLeft(TabDropLine, x - 1.5);
        Canvas.SetTop(TabDropLine, at.Y + 4);
        TabDropLine.Height = Math.Max(0, row.ActualHeight - 4);
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
            var pinned = pins[from];
            tabs.RemoveAt(from); pins.RemoveAt(from);
            tabs.Insert(at > from ? at - 1 : at, drag.Path); pins.Insert(at > from ? at - 1 : at, pinned);
            tab = tabs.IndexOf(active);
            BuildTabs();
        }
        else
        {
            if (drag.Pane.IsPinned(drag.Path)) return; // a pinned tab stays where it was pinned
            drag.Pane.TakeTabAway(drag.Path);
            pins.Insert(Math.Clamp(at, 0, tabs.Count), false);
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
        tabs.RemoveAt(i); pins.RemoveAt(i);
        if (i < tab || tab == tabs.Count) tab--;
        Navigate(Dir, record: false);
    }

    bool IsPinned(string path) => tabs.IndexOf(path) is >= 0 and var i && pins[i];

    // ---- remembering the open tabs ----
    public IReadOnlyList<string> OpenTabs => tabs;
    public int ActiveTab => tab;
    public List<int> PinnedTabs => Enumerable.Range(0, tabs.Count).Where(i => pins[i]).ToList();

    /// False when none of the saved folders exists any more.
    public bool Restore(List<string> paths, int active, List<int> pinned = null)
    {
        var usable = paths.Select((p, i) => (p, pin: pinned?.Contains(i) == true))
                          .Where(x => x.p == ThisPC || IsRemote(x.p) || Directory.Exists(x.p) || ArchiveOf(x.p) != null).ToList(); // a NAS that is off: 21 s per tab
        if (usable.Count == 0) return false;
        tabs.Clear(); pins.Clear();
        tabs.AddRange(usable.Select(x => x.p)); pins.AddRange(usable.Select(x => x.pin));
        tab = Math.Clamp(active, 0, tabs.Count - 1);
        Navigate(Dir, record: false);
        return true;
    }

    // Each tab gets up to 170 px; when they don't all fit they shrink evenly, and below 120 px they
    // wrap into more rows instead, which stay put: switching tabs never reorders the rows.
    const double TabMax = 170, TabMin = 120;
    void FitTabs()
    {
        double room = TabBand.ActualWidth - 4 /* left edge */ - NewTabBox.ActualWidth - CaptionSpace.Width - 8 /* keep some strip visible */;
        if (room <= 0) return;
        int perRow = Math.Max(1, (int)(room / TabMin));
        if (tabs.Count <= perRow) { TabStrip.Rows = 1; TabStrip.Columns = 0; TabStrip.Width = Math.Min(tabs.Count * TabMax, room); }
        else { TabStrip.Columns = perRow; TabStrip.Rows = (tabs.Count + perRow - 1) / perRow; TabStrip.Width = room; }
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
            var at = activeTab.TranslatePoint(new Point(0, 0), TabBand);
            double x = at.X, tw = activeTab.ActualWidth;
            var cut = new StreamGeometry();
            using (var g = cut.Open())
            if (TabStrip.Rows > 1)
            {
                // several rows: the active tab may sit above another row, so it is cut out on its own
                // instead of flaring into the panel below
                double top = at.Y + StripTop, bottom = at.Y + activeTab.ActualHeight;
                g.BeginFigure(new Point(x, bottom), true, true);
                g.LineTo(new Point(x, top + TabRadius), true, true);
                g.ArcTo(new Point(x + TabRadius, top), new Size(TabRadius, TabRadius), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(x + tw - TabRadius, top), true, true);
                g.ArcTo(new Point(x + tw, top + TabRadius), new Size(TabRadius, TabRadius), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(x + tw, bottom), true, true);
            }
            else
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

    public void NewTab() { tabs.Insert(tab + 1, Dir); pins.Insert(tab + 1, false); tab++; Navigate(Dir, record: false); }

    /// What closing the last tab does instead of nothing: the second pane hides itself (Settings → "Close the second pane
    /// with its last tab"). The tab stays, so the pane comes back as it was. Null: the last tab can't be closed.
    Action closeLast;
    public Action CloseLastTab
    {
        get => closeLast;
        set { closeLast = value; tabsShown = ""; if (Dir != "") BuildTabs(); } // the last tab's close button comes or goes
    }

    public void CloseTab(int i = -1)
    {
        int idx = i < 0 ? tab : i;
        if (pins[idx]) return;
        if (tabs.Count == 1) { closeLast?.Invoke(); return; }
        TabClosed?.Invoke(tabs[idx]);
        tabs.RemoveAt(idx); pins.RemoveAt(idx);
        if (idx < tab || tab == tabs.Count) tab--;
        Navigate(Dir, record: false);
        List.Focus();
        RecallTab(focus: true);
    }
    public void SwitchTab(int i) { RememberTab(); tab = (i + tabs.Count) % tabs.Count; back.Clear(); fwd.Clear(); Navigate(Dir, record: false); List.Focus(); RecallTab(focus: true); }
    public void NextTab(int step) => SwitchTab(tab + step);

    /// <summary>Going up to a folder that holds the one on screen, by any road (Up, Back, a breadcrumb, a typed
    /// path): the name of the folder just left, so it is selected and a long listing does not start over
    /// from the top. Null when <paramref name="to"/> is not above <paramref name="from"/>.</summary>
    static string ChildToward(string to, string from)
    {
        var above = to.TrimEnd('\\') + "\\";
        if (from == null || !from.StartsWith(above, StringComparison.OrdinalIgnoreCase)) return null;
        var next = from.Substring(above.Length).Split('\\')[0];
        return next.Length > 0 ? next : null;
    }

    public void Back() { if (back.Count > 0) { fwd.Push(Dir); Navigate(back.Pop(), record: false); } }
    public void Forward() { if (fwd.Count > 0) { back.Push(Dir); Navigate(fwd.Pop(), record: false); } }
    public void Up()
    {
        if (IsServer(Dir) || Dir == ThisPC) return;
        var name = System.IO.Path.GetFileName(Dir.TrimEnd('\\'));
        if (Directory.GetParent(Dir) is { } p) Navigate(p.FullName, name);
        else if (Dir.StartsWith(@"\\")) Navigate(ServerOf(Dir), name); // share root → the server's shares
        else Navigate(ThisPC, Dir); // a drive → This PC, with the drive selected
    }

    /// The tab showing `path`, or -1. Opening a folder from outside switches to it instead of adding a duplicate.
    public int FindTab(string path) => tabs.FindIndex(t => t.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    public void ShowTab(int i, string select = null) { RememberTab(); tab = i; back.Clear(); fwd.Clear(); Navigate(Dir, select, record: false); if (select == null) RecallTab(focus: List.IsKeyboardFocusWithin); }

    /// Room kept free at the right of the tab row for the window's own buttons, which sit over it.
    /// Part of the tab row, so it still moves the window like the rest of the row's empty space.
    public void ReserveCaption(double width) { CaptionSpace.Width = width; FitTabs(); }

    // The empty part of the tab row moves the window, as a title bar would: the window has no other.
    void TabBand_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == TabBand || e.OriginalSource is Border { Child: null }) (Window.GetWindow(this) as MainWindow)?.CaptionDown(e);
    }

    public void OpenInBackgroundTab(string path) { tabs.Insert(tab + 1, path); pins.Insert(tab + 1, false); BuildTabs(); }

    /// Pinned tabs sit together at the left and have no close button; Ctrl+W and middle-click pass them by.
    public void TogglePin(int i)
    {
        var path = tabs[i]; var active = tabs[tab];
        bool pin = !pins[i];
        tabs.RemoveAt(i); pins.RemoveAt(i);
        int at = pins.Count(p => p); // first place after the pinned group
        tabs.Insert(at, path); pins.Insert(at, pin);
        tab = i == tab ? at : tabs.IndexOf(active);
        BuildTabs();
        Settings.Save();
    }

    ContextMenu TabMenu(int i)
    {
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = pins[i] ? "Unpin tab" : "Pin tab" };
        pin.Click += (_, _) => TogglePin(i);
        var close = new MenuItem { Header = "Close tab", InputGestureText = "Ctrl+W", IsEnabled = !pins[i] && (tabs.Count > 1 || closeLast != null) };
        close.Click += (_, _) => CloseTab(i);
        var others = new MenuItem { Header = "Close other tabs", IsEnabled = pins.Where((p, j) => j != i && !p).Any() };
        others.Click += (_, _) => CloseOtherTabs(i);
        menu.Items.Add(pin); menu.Items.Add(close); menu.Items.Add(others);
        return menu;
    }

    /// Every tab but this one and the pinned ones; this one becomes the active tab.
    void CloseOtherTabs(int keep)
    {
        for (int j = tabs.Count - 1; j >= 0; j--)
            if (j != keep && !pins[j]) { TabClosed?.Invoke(tabs[j]); tabs.RemoveAt(j); pins.RemoveAt(j); if (j < keep) keep--; }
        tab = keep;
        Navigate(Dir, record: false);
        List.Focus();
    }

    /// Back, Forward and Up open where they would go in a new tab on a middle click, as in a browser.
    void Nav_MiddleClick(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var target = (string)((FrameworkElement)s).Tag switch
        {
            "back" => back.Count > 0 ? back.Peek() : null,
            "fwd" => fwd.Count > 0 ? fwd.Peek() : null,
            _ => UpTarget(),
        };
        if (target != null) OpenInBackgroundTab(target);
        e.Handled = true;
    }

    string UpTarget() =>
        IsServer(Dir) || Dir == ThisPC ? null : Directory.GetParent(Dir)?.FullName ?? (Dir.StartsWith(@"\\") ? ServerOf(Dir) : ThisPC);

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
        if (List.SelectedItem is not Entry e || renamingEntry != null || Dir == ThisPC) return;
        renamingEntry = e;
        List.ScrollIntoView(e); // its row has to exist for the box to appear
        e.Renaming = true;
        foreach (var other in items) other.Dimmed = other != e; // the edited row is the only bright one
    }

    TextBox renameBox;

    // When the row's box appears, not when it is loaded: the box is built with the row, hidden, long before
    // F2, so focusing it on Loaded did nothing and the keys kept going to the list.
    void Rename_Shown(object s, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)s;
        if (!box.IsVisible || box.Tag is not Entry { Renaming: true }) return;
        renameBox = box;
        box.Text = ((Entry)box.Tag).Name; // not what was typed last time: an edit cancelled with Esc stays in the box
        box.Focus();
        var dot = System.IO.Path.GetFileNameWithoutExtension(box.Text).Length;
        box.Select(0, dot > 0 ? dot : box.Text.Length); // keep the extension out of the selection
    }

    void Rename_KeyDown(object s, KeyEventArgs e)
    {
        var box = (TextBox)s;
        if (e.Key == Key.Enter) Finish(box, commit: true);
        else if (e.Key == Key.Escape) Finish(box, commit: false);
        // one line: Up and Down go to its start and end, as in Explorer (PreviewKeyDown: the box itself
        // would take them and do nothing), and never on to the list
        else if (e.Key is Key.Up or Key.PageUp) box.CaretIndex = 0;
        else if (e.Key is Key.Down or Key.PageDown) box.CaretIndex = box.Text.Length;
        else return;
        e.Handled = true;
    }

    /// A click anywhere outside the box ends the rename with the new name, as in Explorer.
    void CommitRename() { if (renamingEntry != null && renameBox != null) Finish(renameBox, commit: true); }

    // Clicking away keeps the new name, as Explorer does. The keyboard stays where it went (the other pane,
    // the sidebar): it is not pulled back to the list.
    void Rename_Done(object s, KeyboardFocusChangedEventArgs e) => Finish((TextBox)s, commit: true, refocus: false);

    void Finish(TextBox box, bool commit, bool refocus = true)
    {
        if (box.Tag is not Entry entry || !entry.Renaming) return;
        entry.Renaming = false;
        renamingEntry = null; renameBox = null;
        foreach (var other in items) other.Dimmed = false;
        // back to the row, not to the list as a whole: from there the next arrow key started over at the top
        if (refocus && (List.ItemContainerGenerator.ContainerFromItem(entry) as UIElement)?.Focus() != true) List.Focus();
        var name = box.Text.Trim();
        if (commit && name != "" && name != entry.Name) RenameCommitted?.Invoke(entry, name);
    }

    public async void OpenSelected()
    {
        if (List.SelectedItem is not Entry e) return;
        // a Stash or Backup Labs backup opens in its app when that is installed (1.0.40), otherwise as before;
        // read off the UI thread, the file may be on a share that is asleep
        var backup = !e.IsUp && !e.IsDir && Backups.Named(e.Path) ? await Task.Run(() => MainWindow.BackupStart(e.Path)) : null;
        if (e.IsUp) Up();
        else if (ArchiveRoot != null) { if (e.IsDir) Navigate(e.Path); else OpenArchived?.Invoke(e); }
        else if (!e.IsDir && Archive.IsArchiveName(e.Path)) Navigate(e.Path); // a ZIP or TAR opens like a folder
        else if (e.FolderTarget is { } folder) Navigate(folder); // a shortcut to a folder opens here, as in Explorer
        // a script is run, like Explorer does; editing it is F4 / Edit in the menu
        else if (backup == null && Settings.InternalEditor && EditorWindow.IsText(e.Path) && !MainWindow.Runnable.Contains(Path.GetExtension(e.Path))) EditorWindow.Open(e.Path);
        else
        {
            // A shortcut whose target is gone gets Windows' own answer, as from Explorer: it looks for the target, then
            // says the shortcut no longer works and asks whether to delete it. Without its dialogs Windows gave up
            // silently and the list only said "cancelled" (1.0.39 report); what happens to the shortcut is the user's call.
            bool link = e.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            // started in its own folder, as from Explorer: scripts rely on relative paths
            try
            {
                System.Diagnostics.Process.Start(backup ?? new System.Diagnostics.ProcessStartInfo(e.Path)
                {
                    UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(e.Path),
                    ErrorDialog = link, ErrorDialogParentHandle = link ? new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle : IntPtr.Zero,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // ERROR_CANCELLED: answered in Windows' dialog
            catch (System.ComponentModel.Win32Exception ex) { Error?.Invoke(ex.Message); } // e.g. no app associated
        }
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
    // middle-click on the empty part of the path: the folder above, in a new tab (a segment opens itself)
    void Crumbs_MiddleClick(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not Border || UpTarget() is not { } up) return;
        OpenInBackgroundTab(up);
        e.Handled = true;
    }

    /// Settings → Text size per area: the tabs, the path bar and the list, each on top of the window's own size.
    public void ApplyScales()
    {
        static Transform Scale(double s) => s == 1 ? null : new ScaleTransform(s, s);
        TabsArea.LayoutTransform = Scale(Settings.TabsScale);
        PathBar.LayoutTransform = Scale(Settings.PathScale);
        List.LayoutTransform = Scale(Settings.ListScale);
        PlaceNameEdge();
    }
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
    // On the selected row once it is drawn, not on the list: a file handed in from outside (Open path in Everything,
    // Open-Shell) was selected while the keyboard stayed on the list, so the arrows started elsewhere (Rico, 1.0.53).
    // A folder read from the cache is shown before this runs, and a fresh read that changes nothing only patches it.
    public void FocusList()
    {
        List.Focus();
        Dispatcher.BeginInvoke(() => { if (Keyboard.FocusedElement == List) FocusSelectedRow(); }, DispatcherPriority.Loaded);
    }
    void Filter_Changed(object s, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }
    void ApplyFilter()
    {
        if (view == null) return;
        var q = searchQuery != null ? "" : FilterBox.Text; // search results are already the matches
        // the same words and terms as the search, but contents only on Enter: the filter runs at every keystroke
        var find = SearchQuery.Parse(q, DateTime.Now);
        view.Filter = q.Trim() == "" || find.Error != null ? null : o => o is Entry { IsUp: true }
            || o is Entry e && find.MatchesMeta(e.Name, e.IsDir, e.Size, e.Modified);
    }

    void Header_Click(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col } || !colKey.TryGetValue(col, out var key)) return;
        SetSort(key, key == sortKey ? !sortDesc : key != "Name"); // size/date: biggest/newest first
    }

    public static readonly string[] SortKeys = { "Name", "Size", "Type", "Modified", "Version" };
    public string SortKey => sortKey;
    public bool SortDescending => sortDesc;

    /// The user's choice: applied, and remembered for this folder (Name ascending is the default, kept as nothing).
    public void SetSort(string key, bool desc)
    {
        if (Dir != "")
        {
            if (key == "Name" && !desc) Settings.FolderSorts.Remove(Dir);
            else Settings.FolderSorts[Dir] = (key, desc);
        }
        ShowSort(key, desc);
        if (view != null) view.CustomSort = new EntrySort(key, desc);
        // the selected file stays in sight and keeps the keyboard, wherever the new order put it
        if (List.SelectedItem is { } sel)
            Dispatcher.BeginInvoke(() =>
            {
                List.ScrollIntoView(sel);
                if (List.IsKeyboardFocusWithin) FocusSelectedRow();
            }, DispatcherPriority.Loaded);
    }

    void ShowSort(string key, bool desc)
    {
        sortKey = key; sortDesc = desc;
        ShowSortArrow();
        SortIcon.Text = desc ? "" : ""; // down / up arrow
        SortName.Text = key;
    }

    // The column headers are only there in Details, so every mode gets this menu.
    void SortButton_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var item in SortMenuItems()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    /// The sort choices, shared by the Sort button and the right-click menu.
    public IEnumerable<object> SortMenuItems()
    {
        foreach (var key in SortKeys)
        {
            var k = key;
            var mi = new MenuItem { Header = key, IsChecked = key == sortKey, FontWeight = key == sortKey ? FontWeights.SemiBold : FontWeights.Normal };
            mi.Click += (_, _) => SetSort(k, k == sortKey ? sortDesc : k != "Name"); // size/date: biggest/newest first
            yield return mi;
        }
        yield return new Separator();
        var reverse = new MenuItem
        {
            Header = sortDesc ? "Ascending" : "Descending",
            Icon = new TextBlock { Text = sortDesc ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 },
        };
        reverse.Click += (_, _) => SetSort(sortKey, !sortDesc);
        yield return reverse;
        yield return new Separator();
        var group = new MenuItem { Header = "Group by date", IsChecked = Settings.DateGroupFolders.Contains(Dir), ToolTip = "Today, Yesterday, Last week… in Details, for this folder" };
        group.Click += (_, _) => ToggleDateGroups();
        yield return group;
    }

    // ---- Group by date: per folder, like its sort; shown in Details (in the icon views it waits for Details) ----
    public void ToggleDateGroups()
    {
        if (Dir == "" || Dir == ThisPC) return;
        if (!Settings.DateGroupFolders.Remove(Dir)) Settings.DateGroupFolders.Add(Dir);
        if (Mode != ViewMode.Details && Settings.DateGroupFolders.Contains(Dir)) SetView(ViewMode.Details); // groups show only there
        else ApplyGrouping();
        if (List.SelectedItem is { } sel) Dispatcher.BeginInvoke(() => List.ScrollIntoView(sel), DispatcherPriority.Loaded);
    }

    // the view hands the comparer its groups, not their names (a cast to string took 1.0.57's first build down)
    static readonly IComparer DateGroupOrder = Comparer<object>.Create((a, b) => GroupRank(a).CompareTo(GroupRank(b)));
    static int GroupRank(object g) => Array.IndexOf(Entry.DateGroups, (g as CollectionViewGroup)?.Name ?? g); // "" (..) is -1: first

    void ApplyGrouping()
    {
        if (view == null) return;
        bool on = Mode == ViewMode.Details && Settings.DateGroupFolders.Contains(Dir) && searchQuery == null;
        if (on == view.GroupDescriptions.Count > 0) return;
        if (on)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Entry.DateGroup)) { CustomSort = DateGroupOrder });
            view.IsLiveGrouping = true; // a file written now moves to Today
            view.LiveGroupingProperties.Add(nameof(Entry.DateGroup));
        }
        else { view.GroupDescriptions.Clear(); view.IsLiveGrouping = false; view.LiveGroupingProperties.Clear(); }
    }

    static bool ResizingColumn => Mouse.Captured is System.Windows.Controls.Primitives.Thumb { TemplatedParent: GridViewColumnHeader };

    void FitName()
    {
        // gutter: a strip of empty space kept right of the last column, to click on (see RowAt)
        if (Settings.ColumnWidths.ContainsKey("Name")) return; // dragged by the user: theirs, not the pane's
        const double scrollbar = 14, padding = 14, gutter = 110;
        var others = ((GridView)List.View).Columns.Where(c => c != NameColumn).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        NameColumn.Width = Math.Max(120, List.ActualWidth - others - scrollbar - padding - gutter);
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

    /// In Details, a thin line down the list where the Name column ends: right of it, a press on any row starts a
    /// selection rectangle instead of picking up the file (Drag_Down), and the line shows where that begins.
    void PlaceNameEdge()
    {
        bool show = Mode == ViewMode.Details;
        NameEdge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        // the same boundary Drag_Down tests, mapped through the list's text-size scale like the marquee
        Canvas.SetLeft(NameEdge, List.TransformToVisual(MarqueeLayer).Transform(new Point(NameColumn.ActualWidth - ScrollX, 0)).X);
        NameEdge.Height = MarqueeLayer.ActualHeight;
    }

    public void SetView(ViewMode mode)
    {
        Mode = mode;
        PlaceNameEdge();
        var m = Modes.First(x => x.mode == mode);
        ViewIcon.Text = m.glyph; ViewName.Text = m.name;
        Settings.Views[Name] = mode;
        var sel = List.SelectedItem;
        // Template, item style and panel are three separate changes, and the list would rebuild its
        // containers after each one. Detached from its items it rebuilds once, when they come back.
        var source = List.ItemsSource;
        List.ItemsSource = null;
        ApplyGrouping(); // Details only: the icon views' own panel can't hold groups
        if (mode == ViewMode.Details)
        {
            List.View = details;
            List.ClearValue(ItemsControl.ItemTemplateProperty);
            List.ClearValue(ItemsControl.ItemsPanelProperty);
            List.ClearValue(ItemsControl.ItemContainerStyleProperty);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto); // columns wider than the pane scroll sideways
            Dispatcher.BeginInvoke(HideScrollCorner, DispatcherPriority.Loaded); // once the Details template is in
        }
        else
        {
            List.View = null;
            bool list = mode == ViewMode.List;
            List.Tag = (double)m.size;
            List.ItemTemplate = (DataTemplate)FindResource(list ? "ListCell" : "TileCell");
            // No Width on the tiles: the panel arranges each at TileWidth, which less TileItem's 2 px margin is the
            // size + 28 they had, and the size can then change while zooming without the list being rebuilt.
            List.ItemContainerStyle = (Style)FindResource(list ? "ListViewItem.Plain" : "TileItem");
            if (!list) iconSize = iconTarget = m.size;
            // List flows top→bottom then sideways; icons wrap left→right then down. The panel only
            // builds the tiles on screen — a WrapPanel built all 5 000 of them and froze the window.
            var panel = new FrameworkElementFactory(typeof(TileVirtualizer));
            panel.SetValue(TileVirtualizer.OrientationProperty, list ? Orientation.Vertical : Orientation.Horizontal);
            panel.SetValue(TileVirtualizer.TileWidthProperty, list ? 246.0 : m.size + 32.0);
            panel.SetValue(TileVirtualizer.TileHeightProperty, list ? 24.0 : m.size + 62.0);
            List.ItemsPanel = new ItemsPanelTemplate(panel);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, list ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(List, list ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            StartThumbs();
        }
        if (mode == ViewMode.Details) ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        List.ItemsSource = source;
        List.SelectedItem = sel;
        if (sel != null) List.ScrollIntoView(sel);
        FadeInList(); // the rows come back in one go; the fade covers the reflow
    }

    // Windows' Details template draws two white 1 px lines in the square where both scroll bars meet: a bright
    // corner mark on a dark list, now that wide columns scroll sideways.
    void HideScrollCorner()
    {
        static void Walk(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is System.Windows.Shapes.Rectangle { Fill: SolidColorBrush { Color: var col } } r && col == Colors.White) r.Fill = Brushes.Transparent;
                else if (c is not ScrollContentPresenter) Walk(c); // not into the rows
            }
        }
        if (FindScroller(List) is { } sv) Walk(sv);
    }

    // Details ← List ← M ← L ← XL: the same five modes as Ctrl+Shift+1…5, from smallest to largest.
    static readonly ViewMode[] ZoomOrder = { ViewMode.Details, ViewMode.List, ViewMode.IconsM, ViewMode.IconsL, ViewMode.IconsXL };

    // 1.0.53: in the icon views Ctrl+wheel resizes the icons in steps of 16 px, from 48 to 256, gliding to each new
    // size at the screen's rate with the tiles resized in place, so every step shows. Below the smallest icons it
    // steps on to List and Details; those changes rebuild the list, so they come one per notch, 160 ms apart,
    // instead of a fast spin adding up and jumping from one end to the other.
    const double IconMin = 48, IconMax = 256, IconStep = 16;
    double iconSize = 64, iconTarget = 64;
    int zoomSteps;
    DispatcherTimer zoomTimer;
    bool zoomGliding;

    public void Zoom(int step)
    {
        if (Mode is ViewMode.IconsM or ViewMode.IconsL or ViewMode.IconsXL && zoomSteps == 0
            && (step > 0 || iconTarget > IconMin))
        {
            iconTarget = Math.Clamp(iconTarget + step * IconStep, IconMin, IconMax);
            if (!zoomGliding) { zoomGliding = true; CompositionTarget.Rendering += Glide; }
            return;
        }
        zoomSteps += step;
        zoomTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        if (zoomTimer.Tag == null)
        {
            zoomTimer.Tag = "hooked";
            zoomTimer.Tick += (_, _) =>
            {
                int one = Math.Sign(zoomSteps);
                zoomSteps -= one;
                int next = Math.Clamp(Array.IndexOf(ZoomOrder, Mode) + one, 0, ZoomOrder.Length - 1);
                if (ZoomOrder[next] != Mode) SetView(ZoomOrder[next]);
                else zoomSteps = 0;
                // once in the icons, the rest of the spin resizes them
                if (zoomSteps != 0 && Mode is ViewMode.IconsM) { int rest = zoomSteps; zoomSteps = 0; Zoom(rest); }
                if (zoomSteps == 0) zoomTimer.Stop();
            };
        }
        if (!zoomTimer.IsEnabled) zoomTimer.Start();
    }

    void Glide(object s, EventArgs e)
    {
        double diff = iconTarget - iconSize;
        iconSize = Math.Abs(diff) < 0.5 ? iconTarget : iconSize + diff * 0.3;
        ApplyIconSize();
        if (iconSize != iconTarget) return;
        CompositionTarget.Rendering -= Glide;
        zoomGliding = false;
        // the nearest of the three icon views, for the View button, the menus and the next start
        var mode = iconSize < 88 ? ViewMode.IconsM : iconSize < 152 ? ViewMode.IconsL : ViewMode.IconsXL;
        if (mode != Mode)
        {
            Mode = mode;
            var m = Modes.First(x => x.mode == mode);
            ViewIcon.Text = m.glyph; ViewName.Text = m.name;
            Settings.Views[Name] = mode;
        }
        StartThumbs(); // a bigger size may want the larger thumbnails; ones already big enough are kept
    }

    void ApplyIconSize()
    {
        List.Tag = iconSize;
        if (FindPanel(List) is { } panel)
        {
            panel.TileWidth = iconSize + 32;
            panel.TileHeight = iconSize + 62;
        }
        static TileVirtualizer FindPanel(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if ((c as TileVirtualizer ?? FindPanel(c)) is { } p) return p;
            }
            return null;
        }
    }

    /// The five view modes, largest first, for the Windows menu's View submenu.
    public List<(string Text, bool On, Action Run)> ViewChoices() => Modes.Select(m => (m.name, m.mode == Mode, (Action)(() => SetView(m.mode)))).ToList();

    void ViewButton_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var item in ViewMenuItems()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    /// The five view modes, largest first, shared by the View button and the right-click menu's View submenu.
    public IEnumerable<MenuItem> ViewMenuItems()
    {
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
            yield return mi;
        }
    }

    public string ViewModeName => Modes.First(x => x.mode == Mode).name;

    // Real Windows icons for Details/List rows (instead of outline glyphs). Plain files share one icon
    // per extension, so those are looked up once; exe/lnk/ico and folders carry their own.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> IconByExt = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".appref-ms" };
    CancellationTokenSource icons;
    /// stale: rows whose file was rewritten, asked again although they have an icon
    void StartIcons(ICollection<Entry> stale = null)
    {
        icons?.Cancel();
        var cts = icons = new CancellationTokenSource();
        var todo = view?.Cast<Entry>().Where(e => !e.IsUp).Take(5000).ToList() ?? new();
        var t = new Thread(() =>
        {
            foreach (var e in todo)
            {
                if (cts.IsCancellationRequested) return;
                if (e.SmallIcon != null && stale?.Contains(e) != true) continue; // kept from the last time this folder was shown
                var ext = System.IO.Path.GetExtension(e.Name);
                bool shared = !e.IsDir && !OwnIcon.Contains(ext);
                // a file's own icon is cached by its timestamp, so a rewritten one isn't served the old answer
                ImageSource img = shared && IconByExt.TryGetValue(ext, out var cached) ? cached : Thumbnails.Get(e.Path, 32, iconOnly: true, stamp: shared ? 0 : e.Modified.Ticks);
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
                foreach (var e in todo) Thumbnails.ForgetOverlay(e.Path); // the point of the notice is that it changed
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
        // Two sizes only, not one per mode: zooming between L and XL then costs no new thumbnails, and
        // an image already fetched at a larger size is kept instead of being asked for again.
        int size = iconSize <= 96 ? 128 : 256;
        var todo = view?.Cast<Entry>().Where(e => !e.IsUp).Take(3000).ToList() ?? new();
        var t = new Thread(() =>
        {
            foreach (var e in todo)
            {
                if (cts.IsCancellationRequested) return;
                if (e.Thumb != null && e.ThumbSize >= size) continue; // the one we have is big enough
                var img = Thumbnails.Get(e.Path, size, stamp: e.Modified.Ticks);
                if (img != null) Dispatcher.BeginInvoke(() => { e.ThumbSize = size; e.Thumb = img; });
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Arrow on the sorted column: ↑ ascending, ↓ descending.
    readonly Dictionary<GridViewColumn, string> colKey = new();

    void ShowSortArrow()
    {
        if (details is not GridView grid) return; // the other views have no headers to mark
        foreach (var c in grid.Columns)
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

    // ---- Drag & drop: between panes, and to/from Explorer (CF_HDROP). Explorer's rules: Ctrl copies, Shift moves, else same drive moves. ----
    Point dragStart;
    bool dragArmed;
    Entry keepSelection; // click on an already multi-selected row: keep the selection so it can be dragged

    public static Entry FindRow(DependencyObject d)
    {
        // a click on text lands on a Run, which isn't a Visual: step up through the logical tree there
        while (d != null && d is not ListViewItem) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListViewItem)?.DataContext as Entry;
    }

    /// The row under a click, or null for empty space. In Details that includes the strip right of the last
    /// column, as in Explorer: a full folder still has somewhere to click (and right-click) for the folder
    /// itself, so a paste or a new folder lands there and not in a subfolder that happened to be selected.
    public Entry RowAt(DependencyObject source)
    {
        var row = FindRow(source);
        return row != null && Mode == ViewMode.Details && Mouse.GetPosition(List).X + ScrollX > ((GridView)List.View).Columns.Sum(c => c.ActualWidth) + 8 ? null : row;
    }

    /// How far the Details columns are scrolled sideways: pointer positions are in the list's own frame.
    double ScrollX => FindScroller(List)?.HorizontalOffset ?? 0;

    static bool InTextBox(object source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not TextBox) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d != null;
    }

    void Drag_Down(object s, MouseButtonEventArgs e)
    {
        // A press inside the rename box belongs to the text, not to a file drag; anywhere else it ends the rename.
        renameTimer.Stop(); renameCandidate = null; // a second press means a double-click (open) or something else
        if (InTextBox(e.OriginalSource)) { dragArmed = false; keepSelection = null; return; }
        CommitRename();
        var row = RowAt(e.OriginalSource as DependencyObject);
        dragArmed = row is { IsUp: false };
        dragStart = e.GetPosition(List);
        keepSelection = null; deselectOnUp = null;
        // A press on empty space (not a row, scrollbar or column header) may become a selection rectangle.
        // In Details, so may a press on a row outside its Name column, as in Explorer — a full folder
        // has no empty space. Shift keeps its usual range selection.
        bool otherColumn = row != null && Mode == ViewMode.Details && dragStart.X + ScrollX > NameColumn.ActualWidth && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (otherColumn && e.ClickCount == 2) { List.SelectedItem = row; OpenSelected(); e.Handled = true; return; }
        marqueeArmed = (row == null || otherColumn) && !OnChrome(e.OriginalSource as DependencyObject);
        if (marqueeArmed)
        {
            marqueeRow = otherColumn ? row : null;
            ArmMarquee();
            dragArmed = false;
            FocusKeepingRow();
            e.Handled = true; // selection is ours from here: on release (a click) or while dragging (a rectangle)
            return;
        }
        // A slow second click on the name of the one selected file renames it, as in Explorer: armed here,
        // started after the double-click time on release (a double-click opens instead). Not the click that
        // brought the window or this pane forward, and not a click that selected the row.
        if (row is { IsUp: false } && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Count == 1
            && List.SelectedItem == row && isActivePane && Environment.TickCount64 - Math.Max(WindowActivatedAt, paneActivatedAt) > 500)
            renameCandidate = row;
        if (row != null && e.ClickCount == 1 && List.SelectedItems.Count > 1 && List.SelectedItems.Contains(row) && Keyboard.Modifiers == ModifierKeys.None)
        {
            keepSelection = row;
            e.Handled = true;
            List.Focus();
        }
        // Ctrl+press is ours, not the row's. The list dropped a selected row from the selection on the press, so a
        // Ctrl+drag (copy) left without it, or with nothing at all; Explorer drops it on release, if it wasn't a
        // drag. And after the row had handled a Ctrl+press itself, Windows refused the drag that followed ("a drag
        // operation is already in progress"), which crashed 1.0.36.
        if (row is { IsUp: false } && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (List.SelectedItems.Contains(row)) deselectOnUp = row;
            else List.SelectedItems.Add(row);
            e.Handled = true;
            if ((List.ItemContainerGenerator.ContainerFromItem(row) as UIElement)?.Focus() != true) List.Focus();
        }
    }

    Entry deselectOnUp;
    Entry renameCandidate;
    public static long WindowActivatedAt; // MainWindow sets it: the click that activates the window doesn't rename
    bool isActivePane; long paneActivatedAt;
    DispatcherTimer renameTimer;

    void ClickRename()
    {
        renameTimer.Stop();
        var row = renameCandidate;
        renameCandidate = null;
        // still the one selected row, still focused (the window wasn't left, no menu opened), not dragged
        if (row != null && renamingEntry == null && List.SelectedItems.Count == 1 && List.SelectedItem == row && List.IsKeyboardFocusWithin)
            BeginRename();
    }

    void Drag_Up(object s, MouseButtonEventArgs e)
    {
        if (keepSelection != null) { List.SelectedItem = keepSelection; keepSelection = null; } // it was a plain click after all
        if (deselectOnUp != null) { List.SelectedItems.Remove(deselectOnUp); deselectOnUp = null; }
        if (renameCandidate != null) renameTimer.Start();
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
            EdgeScroll(false);
            List.ReleaseMouseCapture();
            UpdateFooter();
            StatsChanged?.Invoke();
        }
    }

    // ---- drag to select: a rectangle from empty space; Ctrl adds to the selection ----
    bool marqueeArmed;
    Entry marqueeRow; // the row pressed outside its Name column, selected if it turns out to be a click
    HashSet<Entry> marqueeBase = new();
    // The rectangle lives in the content, not on the screen: its start is where the press was before any scrolling,
    // and every row seen under it keeps its place there. Before 1.0.54 it was redrawn from the screen alone, so a wheel
    // or a scroll during the drag dropped the rows scrolled away from the selection (Rico, 1.0.53).
    Point marqueeFrom;
    Dictionary<Entry, Rect> marqueeSeen = new();

    Vector ScrollOffset => FindScroller(List) is { } sv ? new Vector(sv.HorizontalOffset, sv.VerticalOffset) : default;

    void ArmMarquee()
    {
        marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? List.SelectedItems.Cast<Entry>().ToHashSet() : new();
        marqueeFrom = dragStart + ScrollOffset;
        marqueeSeen.Clear();
        if (FindScroller(List) is { } sv && sv.Template.FindName("PART_ScrollContentPresenter", sv) is UIElement rows && hookedRows.Add(rows))
            rows.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => { if (Marqueeing) e.Handled = true; }));
    }
    readonly HashSet<UIElement> hookedRows = new(); // each view mode's scroller, hooked once

    bool Marqueeing => Marquee.Visibility == Visibility.Visible
        && (marqueeArmed && Mouse.LeftButton == MouseButtonState.Pressed || rightArmed && Mouse.RightButton == MouseButtonState.Pressed);

    static bool OnChrome(DependencyObject d)
    {
        while (d != null && d is not ListView)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void MarqueeMove(MouseEventArgs e) => MarqueeMove(e.GetPosition(List));

    void MarqueeMove(Point p)
    {
        var offset = ScrollOffset;
        var d = p + offset - marqueeFrom;
        if (Marquee.Visibility != Visibility.Visible)
        {
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            Marquee.Visibility = Visibility.Visible;
            List.CaptureMouse(); // keep getting moves when the pointer leaves the list
        }
        MarqueeEdge(p);
        var area = new Rect(marqueeFrom, p + offset); // in the content
        var rect = new Rect(area.TopLeft - offset, area.Size); // on screen
        // drawn within the rows: not over the column headers above them or the scrollbar below (Rico, 1.0.61)
        rect.Intersect(FindScroller(List)?.Template.FindName("PART_ScrollContentPresenter", FindScroller(List)) is FrameworkElement rows
            ? rows.TransformToAncestor(List).TransformBounds(new Rect(rows.RenderSize)) : new Rect(0, 0, List.ActualWidth, List.ActualHeight));
        if (rect.IsEmpty) return;
        // Drawn in the overlay's coordinates, not the list's: the list has its own size (Settings → Text
        // size → File list), and at 125% the rectangle was drawn at 80% of where the pointer was.
        var drawn = List.TransformToVisual(MarqueeLayer).TransformBounds(rect);
        Canvas.SetLeft(Marquee, drawn.X); Canvas.SetTop(Marquee, drawn.Y);
        Marquee.Width = drawn.Width; Marquee.Height = drawn.Height;

        // Only rows on screen have containers (the list is virtualized): each one seen is remembered where it sits in the
        // content, so it stays in (or out) once scrolled away. Rows never on screen keep their state.
        foreach (Entry item in view)
        {
            if (item.IsUp || List.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem c || !c.IsVisible) continue;
            var bounds = c.TransformToAncestor(List).TransformBounds(new Rect(c.RenderSize));
            bounds.Offset(offset);
            marqueeSeen[item] = bounds;
        }
        var want = new HashSet<Entry>(marqueeBase);
        foreach (var (item, bounds) in marqueeSeen) if (bounds.IntersectsWith(area)) want.Add(item);
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

    // ---- right-button rectangle: selects, and letting go opens the menu for the selection, as in many file
    // managers. A right-click that doesn't move is left alone: the row selects itself and the menu opens as always.
    bool rightArmed;

    void RightDrag_Down(object s, MouseButtonEventArgs e)
    {
        rightArmed = !marqueeArmed && !InTextBox(e.OriginalSource) && !OnChrome(e.OriginalSource as DependencyObject);
        if (!rightArmed) return;
        dragStart = e.GetPosition(List);
        ArmMarquee();
    }

    void RightDrag_Up(object s, MouseButtonEventArgs e)
    {
        if (!rightArmed) return;
        rightArmed = false;
        if (Marquee.Visibility != Visibility.Visible) return; // a plain right-click
        Marquee.Visibility = Visibility.Collapsed;
        EdgeScroll(false);
        List.ReleaseMouseCapture();
        UpdateFooter();
        StatsChanged?.Invoke();
        // the usual menu would be for the spot under the pointer (empty space = the folder, clearing the selection)
        e.Handled = true;
        RightDragSelected?.Invoke();
    }

    void Drag_Move(object s, MouseEventArgs e)
    {
        // Handled: the list holds the mouse for the rectangle, and ListBox's own drag-select would otherwise run as well,
        // scrolling and selecting a range from its anchor when the pointer is outside it, so the count jumped back and
        // forth (50 → 100 → 50) and the rows flickered (Rico, 1.0.61)
        if (rightArmed && e.RightButton == MouseButtonState.Pressed) { MarqueeMove(e); e.Handled = Marquee.Visibility == Visibility.Visible; return; }
        if (marqueeArmed && e.LeftButton == MouseButtonState.Pressed) { MarqueeMove(e); e.Handled = Marquee.Visibility == Visibility.Visible; return; }
        if (!dragArmed || renamingEntry != null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(List) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragArmed = false; keepSelection = null; deselectOnUp = null; renameCandidate = null;
        var paths = ArchiveRoot != null ? ExtractForDrag() : Selected.Select(x => x.Path).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        // Windows can refuse to start a drag ("a drag operation is already in progress"): 1.0.36 died of it
        // once in testing. Then there is simply no drag, as if the mouse hadn't moved far enough.
        // out of an archive it is a copy, whatever the drive: the card says Copied, and the ZIP keeps its files
        try { DragDrop.DoDragDrop(List, data, ArchiveRoot != null ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move); }
        catch (System.Runtime.InteropServices.COMException) { }
        finally { WheelWhileDragging(null); ScrollAtEdge(null); } // however the drag ended
    }

    // Drop on a folder row, or on a shortcut to a folder → into that folder; anywhere else → the folder this pane shows.
    string DropTarget(DragEventArgs e) => FindRow(e.OriginalSource as DependencyObject)?.FolderTarget ?? Dir;

    void Drop_Over(object s, DragEventArgs e)
    {
        // DragOver only: Drop_Drop runs this too, after it has ended both
        if (s == List && e.RoutedEvent == DragOverEvent) { WheelWhileDragging(this); ScrollAtEdge(e); }
        // onto a drive's row it goes into the drive; onto This PC itself, nowhere
        e.Effects = DropTarget(e) is var into && into != ThisPC ? ArchiveOf(into) is { } a ? (Archive.CanChange(a.Archive) && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None)
                    : DropEffects(e, into) : DragDropEffects.None;
        // the folder it would go into lights up, in a colour apart from the selection's
        var row = FindRow(e.OriginalSource as DependencyObject);
        ShowDropRow(e.Effects != DragDropEffects.None && row?.FolderTarget != null ? row : null);
        e.Handled = true;
    }

    // What a drag carries, read once per drag: DragOver comes many times a second, and with 20,000 search results each
    // one copied the whole list out of the drag again and walked it. WPF keeps one data object from DragEnter to Drop.
    static (IDataObject Data, string[] Files, HashSet<string> Paths, HashSet<string> Parents) dragged;

    static (string[] Files, HashSet<string> Paths, HashSet<string> Parents)? Dragged(IDataObject data)
    {
        if (!ReferenceEquals(dragged.Data, data))
            dragged = data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
                ? (data, files, files.ToHashSet(StringComparer.OrdinalIgnoreCase),
                   files.Select(f => System.IO.Path.GetDirectoryName(f)).Where(d => d != null).ToHashSet(StringComparer.OrdinalIgnoreCase))
                : (data, null, null, null);
        return dragged.Files == null ? null : (dragged.Files, dragged.Paths, dragged.Parents);
    }

    /// What a drop of this drag into `target` would do.
    static DragDropEffects DropEffects(DragEventArgs e, string target)
    {
        var effects = DragDropEffects.None;
        // As in Explorer: Ctrl copies, Shift moves, and without either a drop on the same drive moves and
        // on another drive copies — only as far as the source allows (some offer Copy alone).
        if (Dragged(e.Data) is { } drag)
        {
            var (files, paths, parents) = drag;
            bool sameDrive = string.Equals(System.IO.Path.GetPathRoot(files[0]), System.IO.Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            var want = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy
                : e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) || sameDrive ? DragDropEffects.Move
                : DragDropEffects.Copy;
            effects = e.AllowedEffects.HasFlag(want) ? want : e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move);
            // no dropping a folder onto itself, nor into the folder it's already in — except Ctrl+drop (a copy),
            // which duplicates there as in Explorer ("name - Copy.ext")
            bool duplicate = effects == DragDropEffects.Copy && e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
            if (paths.Contains(target) || !duplicate && parents.Contains(target))
                effects = DragDropEffects.None;
        }
        return effects;
    }

    Entry dropRow;
    void ShowDropRow(Entry row)
    {
        if (dropRow == row) return;
        if (dropRow != null) dropRow.DropHover = false;
        dropRow = row;
        if (row != null) row.DropHover = true;
    }

    void Drop_Drop(object s, DragEventArgs e)
    {
        WheelWhileDragging(null);
        ScrollAtEdge(null);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        Drop_Over(s, e);
        ShowDropRow(null);
        if (e.Effects == DragDropEffects.None) return;
        FilesDropped?.Invoke(files.ToList(), DropTarget(e), e.Effects == DragDropEffects.Move);
        // Report Copy back to the source even for a move: Ferry does the move itself, and a source
        // like Explorer that sees Move might delete the originals while Ferry is still copying.
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    // ---- the wheel while a drag is over the list: Windows' drag loop takes the mouse and swallows the wheel (3 notches
    // scrolled 0 rows in 1.0.41), files from Explorer as much as our own. A low-level mouse hook, set only while a drag
    // is over a list and removed when it leaves, drops or ends, hands each notch over the list to its scroller, the way
    // a wheel scrolls it normally (rows in Details, columns in List, tiles in Icons). Wheel turns elsewhere pass by.
    static IntPtr wheelHook;
    static PaneView wheelPane;
    static readonly LowLevelMouseProc WheelProc = OnWheel; // kept alive for as long as Windows may call it

    static void WheelWhileDragging(PaneView pane)
    {
        wheelPane = pane;
        if (pane != null && wheelHook == IntPtr.Zero) wheelHook = SetWindowsHookEx(WH_MOUSE_LL, WheelProc, GetModuleHandle(null), 0);
        else if (pane == null && wheelHook != IntPtr.Zero) { UnhookWindowsHookEx(wheelHook); wheelHook = IntPtr.Zero; }
    }

    static IntPtr OnWheel(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == WM_MOUSEWHEEL && wheelPane is { } pane && FindScroller(pane.List) is { } scroller)
        {
            var hook = System.Runtime.InteropServices.Marshal.PtrToStructure<MouseHookData>(data);
            var at = pane.List.PointFromScreen(new Point(hook.X, hook.Y));
            if (at.X >= 0 && at.Y >= 0 && at.X < pane.List.ActualWidth && at.Y < pane.List.ActualHeight)
            {
                scroller.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, (short)(hook.MouseData >> 16)) { RoutedEvent = MouseWheelEvent });
                return 1; // scrolled here, not also by whatever window has the focus
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    // ---- a drag held near the top or bottom of the list scrolls it, faster the closer to the edge, as in Explorer.
    // Windows' drag loop still renders frames, so a step a frame keeps it going while the pointer rests. It moves by the
    // time since the last frame: a 16 ms timer ticked every 16 or 31 ms and scrolled in uneven jerks (35 steps a second). ----
    double edgeSpeed; // pixels a second, negative = up
    bool edging;
    TimeSpan edgeFrame;
    // Windows sends DragOver when the pointer or a key changes, not while the pointer rests, so as the rows scroll
    // under a resting pointer the folder lit as the drop target is picked again each frame from where the pointer
    // was; it used to stay on the row under the pointer when the scrolling began and travel away with it (1.0.48)
    DragEventArgs edgeDrag;
    Point edgeAt;

    void ScrollAtEdge(DragEventArgs e) // null: the drag left the list, dropped or ended
    {
        edgeSpeed = 0;
        edgeDrag = e;
        if (e != null) edgeAt = e.GetPosition(List);
        // measured on the rows' area: the column header above it and the scrollbar below count as the edge too
        if (e != null && FindScroller(List) is { } sv && sv.Template.FindName("PART_ScrollContentPresenter", sv) is FrameworkElement rows)
        {
            double y = e.GetPosition(rows).Y, h = rows.ActualHeight, zone = Math.Min(48, h / 4);
            if (y < zone) edgeSpeed = -1250 * Math.Min(1, (zone - y) / zone);
            else if (y > h - zone) edgeSpeed = 1250 * Math.Min(1, (y - h + zone) / zone);
        }
        EdgeScroll(edgeSpeed != 0);
    }

    // Past the top or bottom of the rows during a rectangle, the list scrolls toward the pointer, faster the further out.
    // ponytail: up and down only; List view (which scrolls sideways) doesn't scroll at its sides.
    void MarqueeEdge(Point p)
    {
        edgeSpeed = 0;
        edgeDrag = null;
        if (FindScroller(List) is { } sv && sv.Template.FindName("PART_ScrollContentPresenter", sv) is FrameworkElement rows)
        {
            double y = List.TranslatePoint(p, rows).Y, h = rows.ActualHeight;
            if (y < 0) edgeSpeed = -Math.Min(2500, 300 + 12 * -y);
            else if (y > h) edgeSpeed = Math.Min(2500, 300 + 12 * (y - h));
        }
        EdgeScroll(edgeSpeed != 0);
    }

    void EdgeScroll(bool on)
    {
        if (on == edging) return;
        edging = on;
        if (on) { edgeFrame = TimeSpan.Zero; CompositionTarget.Rendering += EdgeStep; }
        else CompositionTarget.Rendering -= EdgeStep;
    }

    void EdgeStep(object s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = edgeFrame == TimeSpan.Zero ? 1 / 60.0 : (now - edgeFrame).TotalSeconds;
        if (dt <= 0) return; // raised twice for one frame
        edgeFrame = now;
        if (FindScroller(List) is not { } sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + edgeSpeed * Math.Min(dt, 0.1));
        if (edgeDrag == null) return;
        List.UpdateLayout(); // the rows where this frame draws them (the layout pass this frame would run anyway)
        var row = FindRow(List.InputHitTest(edgeAt) as DependencyObject);
        ShowDropRow(row?.FolderTarget is { } into && DropEffects(edgeDrag, into) != DragDropEffects.None ? row : null);
    }

    // ---- smooth wheel (Settings): each notch moves a target, and the list eases toward it frame by frame ----
    ScrollViewer gliding;
    double glideTo, glideAt; // where the wheel wants the list; where the last frame put it
    TimeSpan glideFrame;

    void Glide(ScrollViewer sv, double by)
    {
        // notches during a glide add to its target, so a fast spin goes further instead of starting over
        if (gliding != sv)
        {
            if (gliding == null) CompositionTarget.Rendering += GlideStep;
            gliding = sv; glideTo = glideAt = sv.VerticalOffset; glideFrame = TimeSpan.Zero;
        }
        glideTo = Math.Clamp(glideTo + by, 0, sv.ScrollableHeight);
    }

    void GlideStep(object s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = glideFrame == TimeSpan.Zero ? 1 / 60.0 : (now - glideFrame).TotalSeconds;
        glideFrame = now;
        var sv = gliding;
        // anything else that scrolled it meanwhile (scrollbar, keys, a new folder) wins; so does a target reached
        if (Math.Abs(sv.VerticalOffset - glideAt) > 1 || Math.Abs(glideTo - sv.VerticalOffset) < 0.5)
        {
            CompositionTarget.Rendering -= GlideStep;
            gliding = null;
            return;
        }
        // about 85% of the way in 0.1 s, whatever the screen's refresh rate
        double left = glideTo - sv.VerticalOffset, next = glideTo - left * Math.Exp(-dt / 0.05);
        if (Math.Abs(glideTo - next) < 0.5) next = glideTo;
        sv.ScrollToVerticalOffset(next);
        glideAt = next;
    }

    // ---- middle-click autoscroll (Settings): the list scrolls toward the pointer, faster the further it is from where the
    // click was, every frame, until a click or a key. The pointer is read from Windows each frame: with no button held,
    // the window gets no moves once the pointer is outside it. ----
    bool autoscrolling, autoscrollHeld, autoscrollMoved;
    MouseButton? swallowUp;
    Point autoscrollFrom;
    TimeSpan autoscrollFrame;
    ScrollViewer autoscroller;
    bool autoscrollUpDown, autoscrollSides;
    Window autoscrollWindow;
    const double AutoscrollDeadZone = 12;

    /// False when there is nowhere to scroll.
    bool StartAutoscroll(Point at)
    {
        if (FindScroller(List) is not { } sv) return false;
        autoscrollUpDown = sv.ScrollableHeight > 0;
        autoscrollSides = Settings.AutoscrollSideways && sv.ScrollableWidth > 0;
        if (!autoscrollUpDown && !autoscrollSides) return false;
        autoscroller = sv;
        autoscrolling = autoscrollHeld = true;
        autoscrollMoved = false;
        autoscrollFrom = at;
        autoscrollFrame = TimeSpan.Zero;
        AutoscrollUpDown.Visibility = autoscrollUpDown ? Visibility.Visible : Visibility.Collapsed;
        AutoscrollSides.Visibility = autoscrollSides ? Visibility.Visible : Visibility.Collapsed;
        var mark = List.TransformToVisual(MarqueeLayer).Transform(at);
        Canvas.SetLeft(AutoscrollMark, mark.X - AutoscrollMark.Width / 2);
        Canvas.SetTop(AutoscrollMark, mark.Y - AutoscrollMark.Height / 2);
        AutoscrollMark.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = AutoscrollCursor(0, 0);
        CaptureMouse(); // every click comes to the pane, so one anywhere in the window ends it
        autoscrollWindow = Window.GetWindow(this);
        if (autoscrollWindow != null) autoscrollWindow.Deactivated += AutoscrollDeactivated;
        CompositionTarget.Rendering += AutoscrollStep;
        return true;
    }

    void AutoscrollDeactivated(object s, EventArgs e) => StopAutoscroll();

    void StopAutoscroll()
    {
        if (!autoscrolling) return;
        autoscrolling = autoscrollHeld = false;
        CompositionTarget.Rendering -= AutoscrollStep;
        if (autoscrollWindow != null) autoscrollWindow.Deactivated -= AutoscrollDeactivated;
        autoscrollWindow = null;
        AutoscrollMark.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture(); // raises LostMouseCapture, which finds it stopped
    }

    /// Pixels a second for a pointer this far from where the click was: nothing inside the dead zone, then gently
    /// faster, as in a browser (100 px away: about 1,100 px a second).
    internal static double AutoscrollSpeed(double distance)
    {
        double a = Math.Abs(distance) - AutoscrollDeadZone;
        return a <= 0 ? 0 : Math.Sign(distance) * Math.Min(8000, a * 8 + a * a * 0.05);
    }

    void AutoscrollStep(object s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = autoscrollFrame == TimeSpan.Zero ? 1 / 60.0 : (now - autoscrollFrame).TotalSeconds;
        if (dt <= 0) return; // raised twice for one frame
        autoscrollFrame = now;
        if (!List.IsVisible || PresentationSource.FromVisual(List) == null) { StopAutoscroll(); return; }
        GetCursorPos(out var cursor);
        var d = List.PointFromScreen(new Point(cursor.X, cursor.Y)) - autoscrollFrom;
        double dx = autoscrollSides ? d.X : 0, dy = autoscrollUpDown ? d.Y : 0;
        if (Math.Abs(dx) > AutoscrollDeadZone || Math.Abs(dy) > AutoscrollDeadZone) autoscrollMoved = true;
        if (AutoscrollCursor(AutoscrollSpeed(dx), AutoscrollSpeed(dy)) is var c && c != Mouse.OverrideCursor) Mouse.OverrideCursor = c; // on change: each set asks the whole window for its cursor again
        dt = Math.Min(dt, 0.1); // a long frame (a folder loading) is not a jump
        var sv = autoscroller;
        if (dy != 0) sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + AutoscrollSpeed(dy) * dt, 0, sv.ScrollableHeight));
        if (dx != 0) sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + AutoscrollSpeed(dx) * dt, 0, sv.ScrollableWidth));
    }

    /// Windows' own autoscroll pointers: which way it is going, or the ways it can go while it rests.
    Cursor AutoscrollCursor(double vx, double vy) => (Math.Sign(vx), Math.Sign(vy)) switch
    {
        (0, 0) => autoscrollUpDown && autoscrollSides ? Cursors.ScrollAll : autoscrollUpDown ? Cursors.ScrollNS : Cursors.ScrollWE,
        (0, < 0) => Cursors.ScrollN, (0, > 0) => Cursors.ScrollS, (< 0, 0) => Cursors.ScrollW, (> 0, 0) => Cursors.ScrollE,
        (< 0, < 0) => Cursors.ScrollNW, (> 0, < 0) => Cursors.ScrollNE, (< 0, > 0) => Cursors.ScrollSW, _ => Cursors.ScrollSE,
    };

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);

    const int WH_MOUSE_LL = 14;
    static readonly IntPtr WM_MOUSEWHEEL = 0x020A;
    delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    void FolderStrip_Click(object s, MouseButtonEventArgs e) { FocusKeepingRow(); SetSelection(_ => false); }

    /// A click on empty space clears the selection, not the place: the keyboard stays on the row it was on (now
    /// unselected, as in Explorer), so Up and Down carry on from there. On the list itself, Down left the list.
    void FocusKeepingRow()
    {
        if (Keyboard.FocusedElement is ListViewItem row && ItemsControl.ItemsControlFromItemContainer(row) == List) return;
        if (List.SelectedItem is { } sel && List.ItemContainerGenerator.ContainerFromItem(sel) is UIElement selRow && selRow.Focus()) return;
        List.Focus();
    }
    void FolderStrip_DragEnter(object s, DragEventArgs e) =>
        FolderStrip.Background = (Brush)FindResource("AccentSoft");
    void FolderStrip_DragLeave(object s, DragEventArgs e) => FolderStrip.Background = Brushes.Transparent;

    void NewTab_Click(object s, RoutedEventArgs e) => NewTab();
    // The press gave the button the keyboard, and the list only puts it back on a row when it had it (ShowItems):
    // after a click on Back the arrow keys moved between the buttons instead of the files (1.0.47 report).
    // Handed to the list first, the folder that opens puts it on its selected row, as Alt+Left does.
    void Back_Click(object s, RoutedEventArgs e) { FocusKeepingRow(); Back(); }
    void Fwd_Click(object s, RoutedEventArgs e) { FocusKeepingRow(); Forward(); }
    void Up_Click(object s, RoutedEventArgs e) { FocusKeepingRow(); Up(); }
}
