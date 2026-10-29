using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileExplorer;

// The Recycle Bin, read straight from disk rather than through the shell namespace: every drive keeps
// X:\$Recycle.Bin\<user SID>\, where each deleted item is a pair — $R… is the item itself (renamed),
// $I… a small record of where it came from, when, and how big it was. Restoring is moving $R back
// and removing its $I, which is exactly what Explorer does.
public partial class RecycleBinWindow : Window
{
    public record Item(string Name, string OriginalPath, DateTime Deleted, long Size, string DataPath, string InfoPath, bool IsDir)
    {
        public string Folder => Path.GetDirectoryName(OriginalPath);
        public string DeletedText => Deleted.ToString("yyyy-MM-dd  HH:mm");
        public string SizeText => MainWindow.Fmt(Size);
        public string Glyph => IsDir ? "\uE8B7" : "\uE7C3";
        public Brush GlyphBrush => MainWindow.Hex(IsDir ? "#60A5FA" : "#9CA3AF");
    }

    readonly Action changed; // panes refresh when something comes back

    public RecycleBinWindow(Window owner, Action changed)
    {
        InitializeComponent();
        Owner = owner;
        this.changed = changed;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Root.Background = MainWindow.Tint();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Delete) Delete_Click(null, null);
        };
        List.PreviewMouseDown += Select_Down;
        PreviewMouseMove += Select_Move;
        PreviewMouseUp += Select_Up;
        List.LostMouseCapture += Select_Lost; // alt-tab in the middle of a rectangle
        LostMouseCapture += Select_Lost;
        Load();
    }

    // ---- drag to select, with either button, from a row or from empty space: every row the rectangle touches, as in
    // Explorer's Recycle Bin; Ctrl adds to the selection. The list's own dragging only moved the selection along.
    Point selectFrom;
    MouseButton? selectButton; // pressed, not yet a rectangle
    bool selecting;
    HashSet<Item> selectBase = new();

    void Select_Down(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right) || OnChrome(e.OriginalSource as DependencyObject)) return;
        selectButton = e.ChangedButton;
        selectFrom = e.GetPosition(List);
        selectBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? List.SelectedItems.Cast<Item>().ToHashSet() : new();
    }

    void Select_Move(object s, MouseEventArgs e)
    {
        if (selectButton is not { } button) return;
        if ((button == MouseButton.Left ? e.LeftButton : e.RightButton) != MouseButtonState.Pressed) { selectButton = null; return; }
        var p = e.GetPosition(List);
        if (!selecting)
        {
            if (Math.Abs(p.X - selectFrom.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - selectFrom.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            selecting = true;
            Marquee.Visibility = Visibility.Visible;
            // the moves are ours from here. A left press: the list already holds the mouse, and keeps it; taken from it,
            // the window got no input at all afterwards. A right press: the list lets go on its first move, so the window holds it.
            if (button == MouseButton.Left) List.CaptureMouse(); else CaptureMouse();
        }
        var rect = new Rect(selectFrom, p);
        rect.Intersect(new Rect(0, 0, List.ActualWidth, List.ActualHeight));
        if (rect.IsEmpty) return;
        var drawn = List.TransformToVisual(MarqueeLayer).TransformBounds(rect);
        Canvas.SetLeft(Marquee, drawn.X); Canvas.SetTop(Marquee, drawn.Y);
        Marquee.Width = drawn.Width; Marquee.Height = drawn.Height;
        // only the rows on screen have containers (the list is virtualized); the others keep their state
        var want = new HashSet<Item>(selectBase);
        foreach (Item item in List.Items)
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem { IsVisible: true } row
                && row.TransformToAncestor(List).TransformBounds(new Rect(row.RenderSize)).IntersectsWith(rect)) want.Add(item);
        foreach (var x in List.SelectedItems.Cast<Item>().Where(x => !want.Contains(x)).ToList()) List.SelectedItems.Remove(x);
        foreach (var x in want) if (!List.SelectedItems.Contains(x)) List.SelectedItems.Add(x);
    }

    void Select_Up(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != selectButton) return;
        selectButton = null;
        if (!selecting) return; // a click: the list handles it as always
        selecting = false;
        Marquee.Visibility = Visibility.Collapsed;
        Mouse.Capture(null);
        e.Handled = true;
    }

    void Select_Lost(object s, MouseEventArgs e)
    {
        if (!selecting) return;
        selecting = false; selectButton = null;
        Marquee.Visibility = Visibility.Collapsed;
    }

    static bool OnChrome(DependencyObject d)
    {
        for (; d != null && d is not ListView; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader) return true;
        return false;
    }

    // Read off the UI thread: a bin with thousands of items on several drives is thousands of small files.
    async void Load()
    {
        Summary.Text = "Reading the Recycle Bin…";
        var items = await Task.Run(() => Read());
        List.ItemsSource = items.OrderByDescending(i => i.Deleted).ToList();
        Summary.Text = items.Count == 0 ? "The Recycle Bin is empty" : $"{items.Count} items  ·  {MainWindow.Fmt(items.Sum(i => i.Size))}";
    }

    static string Sid => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;

    /// Every item in the bins of these drives (roots like G:\), or of every fixed and removable drive.
    static List<Item> Read(IEnumerable<string> roots = null)
    {
        var sid = Sid;
        var list = new List<Item>();
        if (sid == null) return list;
        roots ??= DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.RootDirectory.FullName);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.Combine(root, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin)) continue;
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(bin, "$I*").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var info in infos)
                if (ItemOf(info) is { } item) list.Add(item);
        }
        return list;
    }

    /// The item an $I record describes, while its $R is still there.
    static Item ItemOf(string info)
    {
        var data = Path.Combine(Path.GetDirectoryName(info), "$R" + Path.GetFileName(info)[2..]);
        bool isDir = Directory.Exists(data);
        if (!isDir && !File.Exists(data)) return null; // record without its item: nothing to restore
        return ParseInfo(info) is { } r ? new Item(Path.GetFileName(r.Path), r.Path, r.Deleted, r.Size, data, info, isDir) : null;
    }

    /// The $I record: version (8 bytes), size (8), deletion time as FILETIME (8), then the original path —
    /// fixed 260 UTF-16 chars in version 1 (Vista–8.1), length-prefixed in version 2 (Windows 10+).
    internal static (string Path, DateTime Deleted, long Size)? ParseInfo(string file)
    {
        try
        {
            var b = File.ReadAllBytes(file);
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0), size = BitConverter.ToInt64(b, 8);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            string path = version switch
            {
                1 when b.Length >= 24 + 520 => Encoding.Unicode.GetString(b, 24, 520),
                2 when b.Length >= 28 => Encoding.Unicode.GetString(b, 28, Math.Min(BitConverter.ToInt32(b, 24) * 2, b.Length - 28)),
                _ => null,
            };
            path = path?.Split('\0')[0];
            return string.IsNullOrEmpty(path) ? null : (path, deleted, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    List<Item> Selected => List.SelectedItems.Cast<Item>().ToList();

    void Restore_Click(object s, RoutedEventArgs e) => Restore(Selected);
    void List_DoubleClick(object s, MouseButtonEventArgs e) { if (List.SelectedItem is Item i) Restore(new() { i }); }

    // Restoring and deleting run off the UI thread, the count in the status line as they go.
    async void Restore(List<Item> items)
    {
        if (items.Count == 0) return;
        var (ok, error) = await Task.Run(() => Each(items, "Restoring", RestoreOne));
        Status.Text = error ?? $"Restored {ok} item(s)";
        Load();
        changed();
    }

    /// Runs `one` on each item, saying how far it got ten times a second; how many worked and the first error.
    (int Ok, string Error) Each(List<Item> items, string doing, Func<Item, string> one)
    {
        int ok = 0, done = 0; string error = null;
        long said = 0;
        foreach (var i in items)
        {
            if (one(i) is { } failed) error ??= failed; else ok++;
            done++;
            if (Environment.TickCount64 - said < 100) continue;
            said = Environment.TickCount64;
            int at = done;
            Dispatcher.BeginInvoke(() => Status.Text = $"{doing} {at:N0} of {items.Count:N0}…");
        }
        return (ok, error);
    }

    /// Moves an item back where it was deleted from; null when it worked, else why not.
    /// Something new at the old place wins: the restored item gets "name (2)" beside it.
    static string RestoreOne(Item i)
    {
        try
        {
            var target = MainWindow.Unique(i.OriginalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (i.IsDir) Directory.Move(i.DataPath, target); else File.Move(i.DataPath, target);
            File.Delete(i.InfoPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{i.Name}: {ex.Message}"; }
    }

    /// Right after a delete: which item in the bin each of these paths became, the newest deleted at or after `since`
    /// from that place (path → its $R file). Undo then restores that very item, not whatever was deleted from there later.
    internal static Dictionary<string, string> Find(IEnumerable<string> paths, DateTime since)
    {
        var want = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Read(want.Select(Path.GetPathRoot).Where(r => r != null))
            .Where(i => i.Deleted >= since && want.Contains(i.OriginalPath))
            .GroupBy(i => i.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.MaxBy(i => i.Deleted).DataPath, StringComparer.OrdinalIgnoreCase);
    }

    /// Undo of a delete: puts back the item whose $R file this is. Gone: it isn't in the bin any more (emptied, or
    /// restored already). Otherwise null when it worked, or why not.
    internal static string RestoreItem(string dataPath, out bool gone)
    {
        var info = Path.Combine(Path.GetDirectoryName(dataPath), "$I" + Path.GetFileName(dataPath)[2..]);
        var item = File.Exists(info) ? ItemOf(info) : null;
        gone = item == null;
        return item == null ? null : RestoreOne(item);
    }

    async void Delete_Click(object s, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"Permanently delete {(items.Count == 1 ? $"“{items[0].Name}”" : $"these {items.Count} items")}?", "Recycle Bin",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var (ok, error) = await Task.Run(() => Each(items, "Deleting", i =>
        {
            try
            {
                if (i.IsDir) Directory.Delete(i.DataPath, recursive: true); else File.Delete(i.DataPath);
                File.Delete(i.InfoPath);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{i.Name}: {ex.Message}"; }
        }));
        Status.Text = error ?? $"Deleted {ok} item(s)";
        Load();
    }

    // Emptying goes through the shell: it asks for confirmation itself and updates the desktop icon.
    void Empty_Click(object s, RoutedEventArgs e)
    {
        SHEmptyRecycleBin(new WindowInteropHelper(this).Handle, null, 0);
        Load();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
}
