using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
        Load();
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
        {
            try
            {
                // Something new at the old place wins; the restored item gets "name (2)" beside it.
                var target = MainWindow.Unique(i.OriginalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (i.IsDir) Directory.Move(i.DataPath, target); else File.Move(i.DataPath, target);
                File.Delete(i.InfoPath);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= $"{i.Name}: {ex.Message}"; }
        }
        Status.Text = error ?? $"Restored {ok} item(s)";
        Load();
        changed();
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
