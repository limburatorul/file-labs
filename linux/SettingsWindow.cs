using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FileExplorer;

/// Settings, grouped as on Windows: Appearance, Files, Transfers, Linux (the default file manager, the terminal), About.
sealed class SettingsWindow : Window
{
    readonly StackPanel page = new() { Spacing = 14 };
    readonly StackPanel nav = new() { Spacing = 2, Margin = new(10, 14, 10, 10) };
    Button activeNav;

    public SettingsWindow()
    {
        Title = "Settings — File Labs";
        Width = 820; Height = 600; MinWidth = 640; MinHeight = 420;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var (name, build) in new (string, Action)[] { ("Appearance", Appearance), ("Files", Files), ("Transfers", Transfers), ("Linux", Linux), ("About", About) })
        {
            var b = new Button { Classes = { "ghost", "side" }, Content = name, Padding = new(12, 7), FontSize = 13 };
            b.Click += (_, _) => Show(b, build);
            nav.Children.Add(b);
        }
        var side = new Border { Width = 190, Background = Fmt.Brush("#40000000"), Child = nav };
        var content = new ScrollViewer { Content = new Border { Padding = new(26, 20), Child = page } };
        var root = new DockPanel { Children = { side, content } };
        DockPanel.SetDock(side, Dock.Left);
        Content = root;
        Show((Button)nav.Children[0], Appearance);
        Closed += (_, _) => { Settings.Save(); MainWindow.Instance?.ApplyAccent(); };
    }

    void Show(Button b, Action build)
    {
        if (activeNav != null) activeNav.Background = Brushes.Transparent;
        activeNav = b; b.Background = Fmt.Brush("#1AFFFFFF");
        page.Children.Clear();
        build();
    }

    void Head(string text) => page.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new(0, 0, 0, 4) });
    void Note(string text) => page.Children.Add(new TextBlock { Text = text, Foreground = Fmt.Brush("#8A97AA"), FontSize = 12, TextWrapping = TextWrapping.Wrap });

    void Toggle(string text, string detail, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        t.IsCheckedChanged += (_, _) => { set(t.IsChecked == true); Settings.Save(); };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = text } } };
        if (detail != null) label.Children.Add(new TextBlock { Text = detail, Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel { Children = { t, label } };
        DockPanel.SetDock(t, Dock.Right);
        page.Children.Add(Card(row));
    }

    void Slider(string text, double min, double max, double value, Action<double> set)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, _) => { set(s.Value); MainWindow.Instance?.ApplyAccent(); };
        var row = new DockPanel { Children = { s, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        DockPanel.SetDock(s, Dock.Right);
        page.Children.Add(Card(row));
    }

    static Border Card(Control c) => new() { Background = Fmt.Brush("#0DFFFFFF"), BorderBrush = Fmt.Brush("#14FFFFFF"), BorderThickness = new(1), CornerRadius = new(8), Padding = new(14, 10), Child = c };

    void Appearance()
    {
        Head("Appearance");
        var swatches = new WrapPanel();
        foreach (var hex in new[] { "#4C8DFF", "#FF4FD8", "#A78BFA", "#34D399", "#FBBF24", "#FB923C", "#F87171", "#38BDF8", "#E8EEF6" })
        {
            var h = hex;
            var b = new Button { Width = 34, Height = 34, Margin = new(0, 0, 8, 8), CornerRadius = new(17), Background = Fmt.Brush(h), BorderThickness = new(Settings.Accent.Equals(h, StringComparison.OrdinalIgnoreCase) ? 3 : 0), BorderBrush = Brushes.White };
            b.Click += (_, _) => { Settings.Accent = h; Settings.Save(); MainWindow.Instance?.ApplyAccent(); Show(activeNav, Appearance); };
            swatches.Children.Add(b);
        }
        page.Children.Add(Card(new StackPanel { Children = { new TextBlock { Text = "Accent", Margin = new(0, 0, 0, 8) }, swatches } }));
        Slider("Accent strength", 0.3, 2.5, Settings.AccentStrength, v => Settings.AccentStrength = v);
        Slider("Row height", 0, 8, Settings.RowPad, v => Settings.RowPad = Math.Round(v));
        Toggle("Cards under the panes", "Drive, folder and selection", Settings.Stats > 0, v => Settings.Stats = v ? 2 : 0);
        Toggle("Age badge in the Modified column", "The coloured badge before the date; off, the colour stays on the row's stripe", Settings.AgeBadges, v => { Settings.AgeBadges = v; MainWindow.Instance?.ApplyOptions(); });
        var parts = new WrapPanel();
        foreach (var (key, title) in MainWindow.SidebarParts)
        {
            var k = key;
            var box = new CheckBox { Content = title, IsChecked = !Settings.SidebarHidden.Contains(k), Margin = new(0, 0, 14, 4), MinWidth = 120 };
            box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) Settings.SidebarHidden.Remove(k); else Settings.SidebarHidden.Add(k); Settings.Save(); MainWindow.Instance?.ApplyOptions(); };
            parts.Children.Add(box);
        }
        page.Children.Add(Card(new StackPanel { Children = { new TextBlock { Text = "In the sidebar", Margin = new(0, 0, 0, 6) }, parts,
            new TextBlock { Text = "Untick a whole section, or single places in Favorites, to leave them out.", Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, Margin = new(0, 4, 0, 0) } } }));
        var pick = new Button { Content = Settings.BackgroundImage == "" ? "Choose a picture…" : Path.GetFileName(Settings.BackgroundImage), Classes = { "job" } };
        pick.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Background picture", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) { Settings.BackgroundImage = p; Settings.Save(); MainWindow.Instance?.ApplyBackground(); Show(activeNav, Appearance); }
        };
        var none = new Button { Content = "None", Classes = { "job" } };
        none.Click += (_, _) => { Settings.BackgroundImage = ""; Settings.Save(); MainWindow.Instance?.ApplyBackground(); Show(activeNav, Appearance); };
        page.Children.Add(Card(new DockPanel { Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Children = { pick, none } }, new TextBlock { Text = "Background picture", VerticalAlignment = VerticalAlignment.Center } } }));
        Slider("Picture: how much shows", 5, 100, Settings.ImageOpacity, v => { Settings.ImageOpacity = v; MainWindow.Instance?.ApplyBackground(); });
        Slider("Picture: darker … lighter", -100, 100, Settings.ImageBrightness, v => { Settings.ImageBrightness = v; MainWindow.Instance?.ApplyBackground(); });
    }

    void Files()
    {
        Head("Files");
        Toggle("Show hidden files", "Names starting with a dot (Ctrl+H)", Settings.ShowHidden, v => Settings.ShowHidden = v);
        Toggle("Tooltips on files", "Hovering a file shows its full name, type, size and date", Settings.ItemTips, v => Settings.ItemTips = v);
        Toggle("Ask before deleting for good", "Shift+Delete asks first", Settings.DeleteSound, v => Settings.DeleteSound = v);
        var limit = new NumericUpDown { Value = Settings.SearchLimit, Minimum = 0, Maximum = 1_000_000, Increment = 1000, Width = 160, FormatString = "0" };
        limit.ValueChanged += (_, _) => { Settings.SearchLimit = (int)(limit.Value ?? 0); Settings.Save(); };
        page.Children.Add(Card(new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Right, Child = limit }, new StackPanel { Children = { new TextBlock { Text = "Search stops after" }, new TextBlock { Text = "results (0 = never)", Foreground = Fmt.Brush("#8A97AA"), FontSize = 11 } } } } }));
    }

    void Transfers()
    {
        Head("Transfers");
        Toggle("Verify every copy", "Read both copies back and compare them (xxHash3); a move deletes only what matched", Settings.Verify, v => Settings.Verify = v);
        Toggle("Small files several at once", "On SSDs and network shares; one by one on spinning disks", Settings.ParallelCopy, v => Settings.ParallelCopy = v);
        Toggle("F5 copies to the other pane", "As in other two-pane file managers; Ctrl+R then refreshes. Off: F5 refreshes, Ctrl+F5 copies", Settings.F5Copies, v => { Settings.F5Copies = v; MainWindow.Instance?.ApplyOptions(); });
        var ways = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (id, text) in new[] { ("button", "Only the button"), ("window", "In their own window"), ("bottom", "Under the panes") })
        {
            var b = new Button { Content = text, Classes = { "job" }, FontWeight = Settings.TransfersShow == id ? FontWeight.SemiBold : FontWeight.Normal, Background = Settings.TransfersShow == id ? Fmt.Brush("#334C8DFF") : null };
            var way = id;
            b.Click += (_, _) => { Settings.TransfersShow = way; Settings.Save(); MainWindow.Instance?.ApplyOptions(); Show(activeNav, Transfers); };
            ways.Children.Add(b);
        }
        page.Children.Add(Card(new StackPanel { Children = { new TextBlock { Text = "Show copies and moves", Margin = new(0, 0, 0, 8) }, ways,
            new TextBlock { Text = "Only the button: nothing opens, the Transfers button at the bottom lights up while a copy or move runs, red if one failed. Their own window: it opens with each one. Under the panes: a card per transfer below the file lists. A question (files already there) opens the window, except under the panes.", Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) } } }));
        if (Settings.TransfersShow == "window")
            Toggle("Close the window when everything is done", "It stays when something failed or a question waits", Settings.CloseTransfersWhenDone, v => Settings.CloseTransfersWhenDone = v);
    }

    void Linux()
    {
        Head("Linux");
        var terminals = Shell.InstalledTerminals.ToList();
        var combo = new ComboBox { ItemsSource = new[] { "The first one found" }.Concat(terminals).ToList(), Width = 220 };
        combo.SelectedIndex = Math.Max(0, terminals.IndexOf(Settings.Terminal) + 1);
        combo.SelectionChanged += (_, _) => { Settings.Terminal = combo.SelectedIndex > 0 ? terminals[combo.SelectedIndex - 1] : ""; Settings.Save(); };
        page.Children.Add(Card(new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Right, Child = combo }, new TextBlock { Text = "Terminal (Ctrl+`)", VerticalAlignment = VerticalAlignment.Center } } }));

        bool isDefault = DefaultFileManager() == "filelabs.desktop";
        var make = new Button { Content = isDefault ? "File Labs opens folders" : "Open folders with File Labs", Classes = { "job" }, IsEnabled = !isDefault };
        make.Click += (_, _) =>
        {
            try { Process.Start("xdg-mime", new[] { "default", "filelabs.desktop", "inode/directory" })?.WaitForExit(4000); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            Show(activeNav, Linux);
        };
        page.Children.Add(Card(new StackPanel
        {
            Children =
            {
                new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Right, Child = make }, new TextBlock { Text = "Default file manager", VerticalAlignment = VerticalAlignment.Center } } },
                new TextBlock { Text = isDefault ? "Folders opened from other apps (Downloads in the browser, Open folder) come to File Labs." : $"Now: {DefaultFileManager() ?? "none set"}. This sets inode/directory for your user (xdg-mime); the desktop's own file manager stays installed.", Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) },
            },
        }));
    }

    static string DefaultFileManager()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("xdg-mime", "query default inode/directory") { RedirectStandardOutput = true, UseShellExecute = false });
            var s = p?.StandardOutput.ReadToEnd().Trim(); p?.WaitForExit(3000);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    void About()
    {
        Head("About");
        page.Children.Add(new TextBlock { Text = $"File Labs {MainWindow.Version} for Linux", FontSize = 14 });
        Note("Free and open source (MIT). No account, no telemetry. Settings live in ~/.config/ProtagonistLabs/File Labs.");
        var site = new Button { Content = "protagonistlabs.app/filelabs", Classes = { "job" } };
        site.Click += (_, _) => Shell.Open("https://protagonistlabs.app/filelabs/?utm_source=filelabs-linux&utm_medium=app&utm_campaign=about");
        var source = new Button { Content = "Source on GitHub", Classes = { "job" } };
        source.Click += (_, _) => Shell.Open("https://github.com/limburatorul/file-labs");
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { site, source } });
    }
}
