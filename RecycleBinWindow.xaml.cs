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

    void Load()
    {
        var items = Read();
        List.ItemsSource = items.OrderByDescending(i => i.Deleted).ToList();
        Summary.Text = items.Count == 0 ? "The Recycle Bin is empty" : $"{items.Count} items  ·  {MainWindow.Fmt(items.Sum(i => i.Size))}";
    }

    static List<Item> Read()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var list = new List<Item>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid ?? "");
            if (sid == null || !Directory.Exists(bin)) continue;
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(bin, "$I*").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var info in infos)
            {
                var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                bool isDir = Directory.Exists(data);
                if (!isDir && !File.Exists(data)) continue; // record without its item: nothing to restore
                if (ParseInfo(info) is not { } r) continue;
                list.Add(new Item(Path.GetFileName(r.Path), r.Path, r.Deleted, r.Size, data, info, isDir));
            }
        }
        return list;
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

    void Restore(List<Item> items)
    {
        if (items.Count == 0) return;
        int ok = 0; string error = null;
        foreach (var i in items)
            if (RestoreOne(i) is { } failed) error ??= failed; else ok++;
        Status.Text = error ?? $"Restored {ok} item(s)";
        Load();
        changed();
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

    /// Undo of a delete (Ctrl+Z): puts back what was deleted from these places at or after `since`, the newest of each.
    /// Gone: no longer in the Recycle Bin (emptied, or restored already). Failed: there, but it couldn't be moved back.
    internal static (List<string> Gone, List<string> Failed, string Error) RestoreDeleted(IEnumerable<string> paths, DateTime since)
    {
        var newest = Read().Where(i => i.Deleted >= since)
                           .GroupBy(i => i.OriginalPath, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.MaxBy(i => i.Deleted), StringComparer.OrdinalIgnoreCase);
        List<string> gone = new(), failed = new();
        string error = null;
        foreach (var p in paths)
            if (!newest.TryGetValue(p, out var i)) gone.Add(p);
            else if (RestoreOne(i) is { } why) { failed.Add(p); error ??= why; }
        return (gone, failed, error);
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"Permanently delete {(items.Count == 1 ? $"“{items[0].Name}”" : $"these {items.Count} items")}?", "Recycle Bin",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string error = null;
        foreach (var i in items)
        {
            try
            {
                if (i.IsDir) Directory.Delete(i.DataPath, recursive: true); else File.Delete(i.DataPath);
                File.Delete(i.InfoPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= $"{i.Name}: {ex.Message}"; }
        }
        Status.Text = error ?? $"Deleted {items.Count} item(s)";
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
