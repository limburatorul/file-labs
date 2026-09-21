using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FileExplorer;

// Rename many at once (F2 with several selected), Total Commander's multi-rename in small:
// a name pattern with [N] name / [E] extension / [C] counter, then find → replace on the result.
public partial class MultiRenameDialog : Window
{
    readonly List<Entry> items;
    List<(string Path, string NewName)> plan = new();

    MultiRenameDialog(Window owner, List<Entry> items)
    {
        InitializeComponent();
        Owner = owner;
        this.items = items;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Root.Background = MainWindow.Tint();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter && OkButton.IsEnabled) DialogResult = true;
        };
        Update();
    }

    /// The renames to do (only the names that change), or null if cancelled.
    public static List<(string Path, string NewName)> Ask(Window owner, List<Entry> items)
    {
        var d = new MultiRenameDialog(owner, items);
        return d.ShowDialog() == true ? d.plan : null;
    }

    record Row(string Old, string New, Brush Brush);

    void Changed(object s, System.Windows.Controls.TextChangedEventArgs e) { if (IsLoaded) Update(); }

    void Update()
    {
        int.TryParse(StartBox.Text, out int counter);
        int digits = (counter + items.Count - 1).ToString().Length; // 1..120 → 001..120, so names sort right
        var rows = new List<Row>();
        plan = new();
        string problem = null;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ours = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in items)
        {
            var n = e.IsDir ? e.Name : Path.GetFileNameWithoutExtension(e.Name);
            var x = e.IsDir ? "" : Path.GetExtension(e.Name).TrimStart('.');
            var c = counter++.ToString().PadLeft(digits, '0');
            string Fill(string pattern) => pattern.Replace("[N]", n, StringComparison.OrdinalIgnoreCase)
                                                  .Replace("[E]", x, StringComparison.OrdinalIgnoreCase)
                                                  .Replace("[C]", c, StringComparison.OrdinalIgnoreCase);
            var ext = Fill(ExtBox.Text).Trim();
            var name = Fill(NameBox.Text) + (ext == "" ? "" : "." + ext);
            if (FindBox.Text != "") name = name.Replace(FindBox.Text, ReplaceBox.Text, StringComparison.OrdinalIgnoreCase);
            name = name.Trim();

            string bad = name == "" ? "a name would be empty"
                : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? $"“{name}” has characters Windows doesn't allow"
                : !taken.Add(Path.Combine(Path.GetDirectoryName(e.Path), name)) ? $"two files would be named “{name}”"
                : ExistsElsewhere(Path.Combine(Path.GetDirectoryName(e.Path), name), ours) ? $"“{name}” already exists"
                : null;
            problem ??= bad;
            rows.Add(new Row(e.Name, name, MainWindow.Hex(bad != null ? "#F87171" : name == e.Name ? "#8A97AA" : "#E8EEF6")));
            if (name != e.Name) plan.Add((e.Path, name));
        }
        Preview.ItemsSource = rows;
        Problem.Text = problem ?? (plan.Count == 0 ? "Nothing changes yet" : $"{plan.Count} of {items.Count} will be renamed");
        Problem.Foreground = MainWindow.Hex(problem != null ? "#F87171" : "#8A97AA");
        OkButton.IsEnabled = problem == null && plan.Count > 0;
    }

    // Taken by a file that isn't part of this rename (those move out of the way first).
    static bool ExistsElsewhere(string path, HashSet<string> ours) =>
        !ours.Contains(path) && (File.Exists(path) || Directory.Exists(path));

    void Ok_Click(object s, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object s, RoutedEventArgs e) => Close();
}
