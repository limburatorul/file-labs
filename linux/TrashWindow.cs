using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FileExplorer;

/// The Trash, as the Recycle Bin window on Windows: what is in it (the home Trash and each drive's), where it came from,
/// when it was deleted; Restore puts the selected back, Delete permanently and Empty Trash ask first.
sealed class TrashWindow : Window
{
    sealed record Item(string InTrash, string From, DateTime Deleted, bool IsDir, long Size)
    {
        public string Name => Path.GetFileName(From ?? InTrash);
        public string Where => Path.GetDirectoryName(From ?? "") ?? "";
        public string Glyph => IsDir ? FileExplorer.Glyph.Folder : FileExplorer.Glyph.Document;
        public string When => Deleted.ToString("yyyy-MM-dd HH:mm");
        public string SizeText => IsDir ? "" : Fmt.Size(Size);
    }

    readonly ListBox list = new() { Classes = { "files" }, SelectionMode = SelectionMode.Multiple };
    readonly TextBlock summary = new() { Foreground = Fmt.Brush("#8A97AA"), VerticalAlignment = VerticalAlignment.Center };

    public TrashWindow()
    {
        Title = "Trash — File Labs";
        Width = 900; Height = 560; MinWidth = 500; MinHeight = 300;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button B(string glyph, string text, Action run) { var b = new Button { Classes = { "job" }, Content = $"{text}" }; b.Click += (_, _) => run(); return b; }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { B(FileExplorer.Glyph.Restore, "Restore", Restore), B(FileExplorer.Glyph.Delete, "Delete permanently", Purge), B(FileExplorer.Glyph.EmptyBin, "Empty Trash", Empty) } };
        var top = new DockPanel { Margin = new(0, 0, 0, 10), Children = { buttons, summary } };
        DockPanel.SetDock(buttons, Dock.Right);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Item>((it, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = it.Glyph, Classes = { "icon" }, FontSize = 15, Foreground = Fmt.Brush(it.IsDir ? "#60A5FA" : "#9CA3AF"), Width = 26, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = it.Name, Width = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = it.Where, Width = 300, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = it.SizeText, Width = 90, Opacity = 0.7, TextAlignment = TextAlignment.Right, Padding = new(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = it.When, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center },
            },
        }, supportsRecycling: true);
        Content = new DockPanel { Margin = new(16), Children = { top, list } };
        DockPanel.SetDock(top, Dock.Top);
        Load();
    }

    void Load()
    {
        var items = new List<Item>();
        foreach (var trash in Trash.AllTrashes())
        {
            var files = Path.Combine(trash, "files");
            if (!Directory.Exists(files)) continue;
            foreach (var fsi in new DirectoryInfo(files).EnumerateFileSystemInfos())
            {
                var info = Path.Combine(trash, "info", fsi.Name + ".trashinfo");
                items.Add(new Item(fsi.FullName, Trash.OriginalPath(info), File.Exists(info) ? Trash.Deleted(info) : fsi.LastWriteTime, fsi is DirectoryInfo, fsi is FileInfo f ? f.Length : 0));
            }
        }
        items.Sort((a, b) => b.Deleted.CompareTo(a.Deleted));
        list.ItemsSource = items;
        summary.Text = items.Count == 0 ? "The Trash is empty" : $"{items.Count:N0} item(s) · {Fmt.Size(items.Sum(i => i.Size))}";
    }

    List<Item> Selected => list.SelectedItems?.Cast<Item>().ToList() ?? new();

    void Restore()
    {
        var errors = new List<string>();
        foreach (var i in Selected)
            try { Trash.Restore(i.InTrash); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{i.Name}: {ex.Message}"); }
        Load();
        if (errors.Count > 0) summary.Text = errors[0];
    }

    async void Purge()
    {
        var sel = Selected;
        if (sel.Count == 0 || !await Dialogs.Confirm(this, "Delete permanently", $"Delete {(sel.Count == 1 ? $"“{sel[0].Name}”" : $"these {sel.Count} items")} for good? This can't be undone.")) return;
        foreach (var i in sel) try { Trash.Purge(i.InTrash); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Load();
    }

    async void Empty()
    {
        var all = (list.ItemsSource as List<Item>) ?? new();
        if (all.Count == 0 || !await Dialogs.Confirm(this, "Empty Trash", $"Delete all {all.Count:N0} items in the Trash for good? This can't be undone.", "Empty Trash")) return;
        await Task.Run(() => { foreach (var i in all) try { Trash.Purge(i.InTrash); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } });
        Load();
    }
}
