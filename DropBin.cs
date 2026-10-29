using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FileExplorer;

// The Drop bin (1.0.43): files and folders gathered from anywhere while browsing — dragged onto the bin, or
// Ctrl+B on a selection — then copied or moved together into the active pane's folder. Paths only: nothing
// moves until Copy or Move is pressed, and the bin lasts for the session.
// 1.0.64: built for big selections. A HashSet answers "already in?", and the list is virtualized: only the rows
// in sight get controls (and an icon), so 20,000 paths cost 20,000 strings, not 20,000 rows of buttons.
public partial class MainWindow
{
    readonly List<string> bin = new();
    readonly HashSet<string> inBin = new(StringComparer.OrdinalIgnoreCase);

    public sealed record BinItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
        public string RemoveName => "Take " + Name + " out of the bin";
    }

    void AddToBin(IEnumerable<string> paths)
    {
        int before = bin.Count;
        foreach (var p in paths)
            if (inBin.Add(p)) bin.Add(p);
        if (!DropBin.IsVisible) { DropBin.Visibility = Visibility.Visible; Motion.Enter(DropBin, rise: 8); }
        ShowBin();
        Status.Text = bin.Count == before ? "Already in the Drop bin" : $"{bin.Count - before:N0} added to the Drop bin";
    }

    void ShowBin()
    {
        BinCount.Text = bin.Count > 0 ? bin.Count.ToString("N0") : "";
        BinActions.Visibility = bin.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BinList.ItemsSource = bin.Select(p => new BinItem(p)).ToList();
        ShowBinTarget();
    }

    // A row's icon, when the row is made (it comes into sight): the shell's icon for that file or folder.
    void BinIcon_Loaded(object s, RoutedEventArgs e)
    {
        if (s is Border { DataContext: BinItem item, Child: null } b)
            b.Child = ShellIcon(item.Path, Directory.Exists(item.Path) ? "" : "", 16, new(0));
    }

    void BinRemove_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is not BinItem item) return;
        inBin.Remove(item.Path);
        bin.Remove(item.Path);
        ShowBin();
    }

    /// The summary names the folder the buttons would send everything to: the active pane's, which changes as you browse.
    void ShowBinTarget()
    {
        if (bin.Count == 0) { BinSummary.Text = "Drag files here from any folder, or press Ctrl+B on a selection. Then copy or move them all at once."; return; }
        int folders = bin.Select(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var into = active?.Dir is { Length: > 0 } d ? (Path.GetFileName(d.TrimEnd('\\')) is { Length: > 0 } n ? n : d) : "?";
        BinSummary.Text = $"{bin.Count:N0} item{(bin.Count == 1 ? "" : "s")} from {folders:N0} folder{(folders == 1 ? "" : "s")}  ·  into {into}";
    }

    // Copy, never Move, as the drop's effect: the bin only keeps the paths, and a source told "moved" (Explorer)
    // could delete what it sent.
    void Bin_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Bin_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) AddToBin(files);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void BinButton_Click(object s, RoutedEventArgs e)
    {
        DropBin.Visibility = DropBin.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        if (DropBin.Visibility == Visibility.Visible) Motion.Enter(DropBin, rise: 8);
        ShowBin();
    }

    void BinClear_Click(object s, RoutedEventArgs e) { bin.Clear(); inBin.Clear(); ShowBin(); }
    void BinCopy_Click(object s, RoutedEventArgs e) => SendBin(move: false);
    void BinMove_Click(object s, RoutedEventArgs e) => SendBin(move: true);

    /// Everything still on disk goes to the active pane's folder as one transfer; the bin empties.
    void SendBin(bool move)
    {
        var there = bin.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        int gone = bin.Count - there.Count;
        if (there.Count > 0) Submit(there, active.Dir, move, ask: false); // the bin says how many from how many folders, and where
        bin.Clear(); inBin.Clear();
        ShowBin();
        DropBin.Visibility = Visibility.Collapsed;
        if (gone > 0) Status.Text = $"{gone} item{(gone == 1 ? " was" : "s were")} no longer there and stayed out";
    }
}
