using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace FileExplorer;

/// F4: a text file in a plain editor: Ctrl+S saves (through a temporary file, so a failed save never leaves half a file).
sealed class EditorWindow : Window
{
    readonly string path;
    readonly TextBox box = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontSize = 13, BorderThickness = new(0), Background = Brushes.Transparent,
                                   FontFamily = new FontFamily("JetBrains Mono, Fira Code, Cascadia Mono, DejaVu Sans Mono, Noto Sans Mono, monospace") };
    readonly TextBlock status = new() { Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, Margin = new(12, 4) };
    string saved;

    public EditorWindow(string path)
    {
        this.path = path;
        Title = $"{Path.GetFileName(path)} — File Labs";
        Width = 960; Height = 680;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6");
        try
        {
            if (new FileInfo(path).Length > 20 << 20) { saved = ""; box.Text = "This file is over 20 MB: too big for the editor."; box.IsReadOnly = true; }
            else box.Text = saved = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { box.Text = ex.Message; box.IsReadOnly = true; saved = box.Text; }
        box.TextChanged += (_, _) => status.Text = box.Text != saved ? "Not saved · Ctrl+S" : "";
        var root = new DockPanel { Children = { status, box } };
        DockPanel.SetDock(status, Dock.Bottom);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Save(); e.Handled = true; } };
        Closing += async (_, e) =>
        {
            if (box.IsReadOnly || box.Text == saved) return;
            e.Cancel = true;
            if (await Dialogs.Confirm(this, "Not saved", $"Save the changes to {Path.GetFileName(path)}?", "Save", "Don't save")) Save();
            saved = box.Text;
            Close();
        };
        Opened += (_, _) => box.Focus();
    }

    void Save()
    {
        try
        {
            var tmp = path + ".filelabs-tmp";
            File.WriteAllText(tmp, box.Text, new UTF8Encoding(false));
            try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(path)); } catch (IOException) { }
            File.Move(tmp, path, overwrite: true);
            saved = box.Text;
            status.Text = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
    }
}

/// Find duplicates: files of the same size, then the same content (xxHash3), grouped; "Select extra copies" keeps the
/// oldest in each group, and the selected go to the Trash.
sealed class DuplicatesWindow : Window
{
    readonly ListBox list = new() { Classes = { "files" }, SelectionMode = SelectionMode.Multiple };
    readonly TextBlock summary = new() { Foreground = Fmt.Brush("#8A97AA"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly CancellationTokenSource cts = new();
    sealed record Copy(string Path, long Size, DateTime Modified, int Group, bool First)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Header => First ? $"{Fmt.Size(Size)} each" : "";
    }

    public DuplicatesWindow(string root)
    {
        Title = $"Duplicates in {Path.GetFileName(root)} — File Labs";
        Width = 900; Height = 600;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var extra = new Button { Content = "Select extra copies", Classes = { "job" } };
        extra.Click += (_, _) => { list.SelectedItems?.Clear(); foreach (var c in Items.GroupBy(c => c.Group).SelectMany(g => g.OrderBy(c => c.Modified).Skip(1))) list.SelectedItems?.Add(c); };
        var delete = new Button { Content = "Move to Trash", Classes = { "job", "on" } };
        delete.Click += async (_, _) =>
        {
            var sel = list.SelectedItems?.Cast<Copy>().ToList() ?? new();
            if (sel.Count == 0 || !await Dialogs.Confirm(this, "Move to Trash", $"Move {sel.Count:N0} copies to the Trash?", "Move to Trash")) return;
            foreach (var c in sel) try { Trash.Put(c.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            list.ItemsSource = Items.Except(sel).ToList();
            summary.Text = $"{sel.Count:N0} moved to the Trash";
        };
        var top = new DockPanel { Margin = new(0, 0, 0, 10), Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Children = { extra, delete } }, summary } };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Copy>((c, _) => new DockPanel
        {
            Margin = new(0, c.First ? 8 : 0, 0, 0),
            Children =
            {
                new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = $"{c.Modified:yyyy-MM-dd HH:mm}", Opacity = 0.6, Margin = new(12, 0, 0, 0) },
                new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = c.Header, Foreground = Fmt.Brush("#FFD23F"), Margin = new(12, 0, 0, 0) },
                new TextBlock { Text = c.Path, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        }, supportsRecycling: true);
        Content = new DockPanel { Margin = new(16), Children = { top, list } };
        DockPanel.SetDock(top, Dock.Top);
        Closed += (_, _) => cts.Cancel();
        Scan(root);
    }

    List<Copy> Items => (list.ItemsSource as List<Copy>) ?? new();

    async void Scan(string root)
    {
        summary.Text = "Looking…";
        var token = cts.Token;
        var result = await Task.Run(() =>
        {
            var bySize = new Dictionary<long, List<FileInfo>>();
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (token.IsCancellationRequested) return null;
                if (f.Length == 0 || f.LinkTarget != null) continue;
                (bySize.TryGetValue(f.Length, out var l) ? l : bySize[f.Length] = new()).Add(f);
            }
            var copies = new List<Copy>(); int group = 0;
            foreach (var (size, files) in bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key))
            {
                foreach (var same in files.GroupBy(f => Hash(f.FullName)).Where(g => g.Key != 0 && g.Count() > 1))
                {
                    if (token.IsCancellationRequested) return null;
                    bool first = true;
                    foreach (var f in same.OrderBy(f => f.LastWriteTime)) { copies.Add(new Copy(f.FullName, size, f.LastWriteTime, group, first)); first = false; }
                    group++;
                }
            }
            return copies;
        });
        if (result == null) return;
        list.ItemsSource = result;
        long wasted = result.GroupBy(c => c.Group).Sum(g => g.First().Size * (g.Count() - 1));
        summary.Text = result.Count == 0 ? "No duplicates here" : $"{result.GroupBy(c => c.Group).Count():N0} groups · {Fmt.Size(wasted)} in extra copies";
    }

    static ulong Hash(string path)
    {
        try
        {
            var h = new System.IO.Hashing.XxHash3();
            using var s = File.OpenRead(path);
            var buf = new byte[1 << 20]; int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) h.Append(buf.AsSpan(0, n));
            return h.GetCurrentHashAsUInt64();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// Rename all: find and replace, or a pattern with a counter ("Holiday {n}"), previewed before anything moves.
sealed class MultiRenameWindow : Window
{
    readonly List<Entry> files;
    readonly PaneView pane;
    readonly TextBox find = new() { Watermark = "Find" }, replace = new() { Watermark = "Replace with" }, pattern = new() { Watermark = "Or a new name: Photo {n}" };
    readonly ListBox preview = new() { Classes = { "files" } };

    public MultiRenameWindow(PaneView pane, List<Entry> files)
    {
        this.pane = pane; this.files = files;
        Title = $"Rename {files.Count} items — File Labs";
        Width = 760; Height = 560;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var go = new Button { Content = "Rename", Classes = { "job", "on" } };
        go.Click += (_, _) => Apply();
        var fields = new StackPanel { Spacing = 8, Margin = new(0, 0, 0, 10), Children = { find, replace, pattern, new TextBlock { Text = "{n} counts from 1, {n:00} pads it; the extension stays.", Foreground = Fmt.Brush("#8A97AA") } } };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0), Children = { go } };
        Content = new DockPanel { Margin = new(16), Children = { fields, bottom, preview } };
        DockPanel.SetDock(fields, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        foreach (var t in new[] { find, replace, pattern }) t.TextChanged += (_, _) => Update();
        Update();
    }

    List<(Entry From, string To)> Plan()
    {
        var plan = new List<(Entry, string)>();
        int n = 1;
        foreach (var e in files)
        {
            string name = e.Name;
            var ext = e.IsDir ? "" : Path.GetExtension(name);
            if (!string.IsNullOrEmpty(pattern.Text))
            {
                var p = pattern.Text;
                var m = System.Text.RegularExpressions.Regex.Match(p, @"\{n(?::(0+))?\}");
                name = m.Success ? p.Replace(m.Value, n.ToString(new string('0', Math.Max(1, m.Groups[1].Value.Length)))) + ext : p + (n > 1 ? $" ({n})" : "") + ext;
                n++;
            }
            else if (!string.IsNullOrEmpty(find.Text)) name = name.Replace(find.Text, replace.Text ?? "");
            plan.Add((e, name));
        }
        return plan;
    }

    void Update() => preview.ItemsSource = Plan().Select(p => $"{p.From.Name}   →   {p.To}").ToList();

    void Apply()
    {
        var plan = Plan().Where(p => p.To != p.From.Name && p.To.Trim() != "" && !p.To.Contains('/')).ToList();
        var names = plan.Select(p => p.To).ToList();
        if (names.Distinct().Count() != names.Count) { Title = "Two items would get the same name"; return; }
        // through temporary names first, so new names may reuse old ones ("2" → "1" while "1" → "2")
        var parked = new List<(string Tmp, string Final, Entry From)>();
        try
        {
            foreach (var (e, to) in plan)
            {
                var tmp = Path.Combine(pane.Dir, $".filelabs-{Guid.NewGuid():N}");
                if (e.IsDir && !e.IsLink) Directory.Move(e.Path, tmp); else File.Move(e.Path, tmp);
                parked.Add((tmp, Path.Combine(Path.GetDirectoryName(e.Path)!, to), e));
            }
            foreach (var (tmp, final, _) in parked)
            {
                if (File.Exists(final) || Directory.Exists(final)) throw new IOException($"{Path.GetFileName(final)} is already there");
                if (Directory.Exists(tmp)) Directory.Move(tmp, final); else File.Move(tmp, final);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (tmp, _, e) in parked.Where(p => File.Exists(p.Tmp) || Directory.Exists(p.Tmp)))
                try { if (Directory.Exists(tmp)) Directory.Move(tmp, e.Path); else File.Move(tmp, e.Path); } catch (IOException) { }
            Title = ex.Message;
            return;
        }
        pane.Refresh(keepSelection: false);
        Close();
    }
}
