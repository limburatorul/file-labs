using System.Windows;

namespace FileExplorer;

public partial class SettingsWindow : Window
{
    readonly MainWindow main;

    public SettingsWindow(MainWindow owner)
    {
        InitializeComponent();
        Owner = main = owner;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        // its cards took 130-200 ms to lay out before the first frame: the window comes up empty, they follow on the next
        ContentRendered += (_, _) => Body.Visibility = Visibility.Visible;
        ApplyTextSize();
        ShowValues();
        // A double-click on a slider's handle puts it back to its default (Settings.cs), as in Frostpane: one
        // setting can be tried out and undone without a reset of them all.
        foreach (var (slider, value) in new (System.Windows.Controls.Slider, double)[]
        {
            (OpacitySlider, 70), (BgOpacitySlider, 40), (BgBlurSlider, 0), (BgBrightSlider, 0), (ValueSlider, 100), (StrengthSlider, 100),
            (ScaleSidebar, 100), (ScaleTabs, 100), (ScalePath, 100), (ScaleList, 100), (TipDelaySlider, 1500),
        })
            slider.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2 || !OnThumb(e.OriginalSource as DependencyObject, slider)) return;
                slider.Value = value;
                e.Handled = true;
            };
        BuildSwatches();
        BuildWheel();
        AboutText.Text = $"File Labs {typeof(App).Assembly.GetName().Version?.ToString(3)} · Protagonist Labs\n" +
                         "Transfers run on the Ferry engine. Settings live in " + Settings.Dir;
        BuildWorksWith();
        Refresh();
    }

    /// Every control set from what is in use: at opening, and after a reset.
    void ShowValues()
    {
        OpacitySlider.Value = Settings.Opacity;
        BgOpacitySlider.Value = Settings.ImageOpacity;
        BgBlurSlider.Value = Settings.ImageBlur;
        BgBrightSlider.Value = Settings.ImageBrightness;
        HiddenBox.IsChecked = Settings.ShowHidden;
        ShortcutFoldersBox.IsChecked = Settings.ShortcutsWithFolders;
        VerifyBox.IsChecked = Settings.Verify;
        ParallelBox.IsChecked = Settings.ParallelCopy;
        EditorBox.IsChecked = Settings.InternalEditor;
        DefaultBox.IsChecked = Settings.IsDefaultFileManager;
        WinEBox.IsChecked = Settings.TakeWinE;
        TrayBox.IsChecked = Settings.CloseToTray;
        StartupBox.IsChecked = Settings.StartWithWindows; // off unless the user turns it on: nothing else writes it
        StartupText.Text = StartupClock.Last;
        AnimationsBox.IsChecked = Settings.Animations;
        SmoothScrollBox.IsChecked = Settings.SmoothScroll;
        SinglePaneBox.IsChecked = Settings.SinglePane;
        StatsBox.IsChecked = Settings.Stats > 0;
        StatsCompactBox.IsChecked = Settings.Stats == 1;
        StatsCompactBox.IsEnabled = Settings.Stats > 0;
        ScaleSidebar.Value = Settings.SidebarScale * 100; ScaleTabs.Value = Settings.TabsScale * 100;
        ScalePath.Value = Settings.PathScale * 100; ScaleList.Value = Settings.ListScale * 100;
        QuickLookBox.IsChecked = Settings.QuickLook;
        ItemTipsBox.IsChecked = Settings.ItemTips;
        DeleteSoundBox.IsChecked = Settings.DeleteSound;
        StrengthSlider.Value = Settings.AccentStrength * 100;
        CloseEmptyPaneBox.IsChecked = Settings.CloseEmptyPane;
        AutoscrollBox.IsChecked = Settings.Autoscroll;
        AutoscrollUpDownBox.IsChecked = !Settings.AutoscrollSideways;
        AutoscrollUpDownBox.IsEnabled = Settings.Autoscroll;
        TipDelaySlider.Value = Settings.TipDelay;
        TipDelayText.Text = $"{Settings.TipDelay / 1000.0:0.0} s";
    }

    // ---- Reset to defaults: a card's settings, or all three cards, as File Labs comes (Settings.Reset) ----
    // Windows integration is left out: it changes Windows (the default file manager, Win+E, sign-in), not File Labs.
    internal static readonly string[] AppearanceKeys =
    {
        "backdrop", "opacity", "bgimage", "bgopacity", "bgblur", "bgbright", "accent", "accentstrength", "rowpad", "textscale",
        "scale.sidebar", "scale.tabs", "scale.path", "scale.list", "singlepane", "closeemptypane", "stats", "animations", "smoothscroll",
        "autoscroll", "autoscrollx",
    };
    internal static readonly string[] FilesKeys = { "hidden", "shortcutfolders", "internaleditor", "quicklook", "itemtips", "tipdelay", "deletesound", "nativemenu", "searchlimit" };
    internal static readonly string[] TransfersKeys = { "verify", "parallel" };
    /// What File Labs keeps that isn't a choice in these cards: Windows integration, and what it remembers by itself.
    internal static readonly string[] NotReset =
    {
        "wine", "tray", "preview", "previewwidth", "lastseen", "recent", "destinations", "sidebar", "sidebarorder", "sidebarcollapsed", "split", "maximized", "window", "quicklist",
    };

    void ResetAll_Click(object s, RoutedEventArgs e) =>
        Reset(AppearanceKeys.Concat(FilesKeys).Concat(TransfersKeys), "Reset every setting to its default?",
              "Appearance, Files and Transfers go back to how File Labs comes. Windows integration stays as it is, and so do your tabs, Quick access lists and folder sizes.");

    void ResetSection_Click(object s, RoutedEventArgs e)
    {
        var (keys, card) = s == ResetAppearance ? (AppearanceKeys, "Appearance") : s == ResetFiles ? (FilesKeys, "Files") : (TransfersKeys, "Transfers");
        Reset(keys, $"Reset {card} to its defaults?", $"Only the settings in the {card} card change.");
    }

    void Reset(IEnumerable<string> keys, string question, string detail)
    {
        if (PromptDialog.Choose(this, question, detail, "Reset", "Cancel") != 0) return;
        bool onePane = Settings.SinglePane;
        Settings.Reset(keys);
        // one pane or two goes through the main window, which keeps the split and moves the keyboard
        if (Settings.SinglePane != onePane) { var want = Settings.SinglePane; Settings.SinglePane = onePane; main.SetSinglePane(want); }
        Settings.Save();
        main.ApplyAppearance();
        main.ApplyStats();
        main.ApplyCloseEmptyPane();
        main.RefreshPanes(); // hidden files
        main.ResortPanes(); // shortcuts with the folders
        MainWindow.SetBackdrop(this);
        loading = true; // the controls take the defaults without each one saving and repainting again
        ShowValues();
        loading = false;
        Refresh();
    }

    bool loading;

    /// Whether a press landed on the slider's handle rather than on its track.
    static bool OnThumb(DependencyObject hit, System.Windows.Controls.Slider slider)
    {
        for (var d = hit; d != null && d != slider; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.Thumb) return true;
        return false;
    }

    // ---- Works with: the sister apps that add something here (ecosystem contract v1.1, Sisters.cs) ----
    // Most useful first: only the first three missing ones are shown.
    static readonly (string Id, string Name, string Has, string Would)[] WorksWithApps =
    {
        ("shelf", "Shelf", "adds folders and .exe files as games, from the right-click menu", "would add folders and .exe files as games, from the right-click menu"),
        ("reel", "Reel", "adds folders and videos to your film library from the right-click menu, and puts a film's poster and watched state above it in the preview", "would add folders and videos to a film library from the right-click menu, and put a film's poster and watched state above it in the preview"),
        ("backuplabs", "Backup Labs", "backs up the selection, from the right-click menu", "would back up the selection, from the right-click menu"),
        ("spacescan", "SpaceScan", "shows what fills a folder, from the right-click menu", "would show what fills a folder, from the right-click menu"),
        ("lume", "Lume", "finds your Quick access folders and opens them here", "would find your Quick access folders and open them here"),
    };
    const string WorksUtm = "utm_source=filelabs&utm_medium=app&utm_campaign=works-with";

    void BuildWorksWith()
    {
        var lines = WorksWith.Inlines;
        void Line(string name, Action open, string what)
        {
            if (lines.Count > 0) lines.Add(new System.Windows.Documents.LineBreak());
            lines.Add(Link(name, open));
            lines.Add(" · " + what);
        }
        int missing = 0;
        foreach (var (id, name, has, would) in WorksWithApps)
        {
            if (MainWindow.SisterExe(id) is { } exe) Line(name, () => System.Diagnostics.Process.Start(exe), has);
            else if (missing++ < 3) Line(name, () => Open($"https://protagonistlabs.app/{id}/?{WorksUtm}"), would);
        }
        // one quiet last line for someone who bought a paid app: the others at 30% off (Paddle SECOND30 / SECOND30SHELF)
        var offer = Sisters.Paid.Where(p => MainWindow.SisterExe(p.Id) == null).Take(4).ToList();
        if (offer.Count == 0 || !Sisters.OwnsPaid()) return;
        lines.Add(new System.Windows.Documents.LineBreak());
        lines.Add("Owners of one of our apps get 30% off the others: ");
        foreach (var (id, name) in offer)
        {
            if (id != offer[0].Id) lines.Add(" · ");
            lines.Add(Link(name, () => Open($"https://protagonistlabs.app/{id}/?code={(id == "shelf" ? "SECOND30SHELF" : "SECOND30")}&{WorksUtm}")));
        }
    }

    static void Open(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    static System.Windows.Documents.Hyperlink Link(string text, Action open)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text));
        link.Click += (_, _) => open();
        return link;
    }
    // Protagonist Labs blue first, then the colours the app already uses for file kinds and recency.
    static readonly string[] Palette = { "#4C8DFF", "#3DDCC8", "#34D399", "#FFD23F", "#FB923C", "#F87171", "#F472B6", "#A78BFA" };

    void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var colour = hex;
            var dot = new System.Windows.Controls.Border
            {
                Width = 22, Height = 22, CornerRadius = new(11), Margin = new(0, 0, 6, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = MainWindow.Hex(hex), BorderThickness = new(2), ToolTip = hex,
            };
            dot.MouseLeftButtonDown += (_, _) => SetAccent(colour);
            Swatches.Children.Add(dot);
        }
    }

    void SetAccent(string hex)
    {
        Settings.Accent = hex;
        Changed();
    }

    // ---- colour wheel ----
    const int WheelSize = 132;
    double hue, sat, val = 1; // what the wheel and the slider show
    bool syncing;             // the controls being set from the accent, not by the user

    void BuildWheel()
    {
        var px = new byte[WheelSize * WheelSize * 4];
        double r = WheelSize / 2.0;
        for (int y = 0; y < WheelSize; y++)
            for (int x = 0; x < WheelSize; x++)
            {
                double dx = x + 0.5 - r, dy = y + 0.5 - r, d = Math.Sqrt(dx * dx + dy * dy);
                double a = Math.Clamp(r - d, 0, 1); // a soft 1 px rim instead of jagged edges
                if (a == 0) continue;
                var c = FromHsv(Math.Atan2(dy, dx) * 180 / Math.PI + 180, Math.Min(1, d / r), 1);
                int i = (y * WheelSize + x) * 4;
                px[i] = (byte)(c.B * a); px[i + 1] = (byte)(c.G * a); px[i + 2] = (byte)(c.R * a); px[i + 3] = (byte)(255 * a); // premultiplied
            }
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(WheelSize, WheelSize, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), px, WheelSize * 4, 0);
        Wheel.Source = bmp;
    }

    static System.Windows.Media.Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return System.Windows.Media.Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static (double H, double S, double V) ToHsv(System.Windows.Media.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    static string ToHex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// Wheel and slider follow the accent, whichever way it was chosen (swatch, hex, wheel).
    void ShowAccentOnWheel()
    {
        var c = MainWindow.AccentColor;
        var (h, s, v) = ToHsv(c);
        if (ToHex(FromHsv(hue, sat, val)) != ToHex(c)) (hue, sat, val) = (h, s, v); // keep the hue of a grey the user is dragging through
        syncing = true;
        ValueSlider.Value = val * 100;
        syncing = false;
        double r = WheelSize / 2.0, a = (hue - 180) * Math.PI / 180;
        System.Windows.Controls.Canvas.SetLeft(WheelMark, r + Math.Cos(a) * sat * r - WheelMark.Width / 2);
        System.Windows.Controls.Canvas.SetTop(WheelMark, r + Math.Sin(a) * sat * r - WheelMark.Height / 2);
        WheelShade.Opacity = 1 - val; // the wheel darkens with the brightness it will produce
        AccentPreview.Background = new System.Windows.Media.SolidColorBrush(c);
    }

    void PickFromWheel(Point p)
    {
        double r = WheelSize / 2.0, dx = p.X - r, dy = p.Y - r;
        hue = Math.Atan2(dy, dx) * 180 / Math.PI + 180;
        sat = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / r);
        Preview(FromHsv(hue, sat, val));
    }

    // While dragging: the whole app follows the colour live; it is saved once, on release.
    void Preview(System.Windows.Media.Color c)
    {
        Settings.Accent = ToHex(c);
        AccentHex.Text = Settings.Accent;
        main.ApplyAppearance();
        ShowAccentOnWheel();
    }

    void Wheel_Down(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        var grid = (System.Windows.Controls.Grid)s;
        grid.CaptureMouse();
        PickFromWheel(e.GetPosition(grid));
    }

    void Wheel_Move(object s, System.Windows.Input.MouseEventArgs e)
    {
        var grid = (System.Windows.Controls.Grid)s;
        if (grid.IsMouseCaptured) PickFromWheel(e.GetPosition(grid));
    }

    void Wheel_Up(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        var grid = (System.Windows.Controls.Grid)s;
        if (!grid.IsMouseCaptured) return;
        grid.ReleaseMouseCapture();
        Changed(); // saves, and the swatches show that none of them is the accent any more
    }

    void Value_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || syncing || loading) return;
        val = e.NewValue / 100;
        Settings.Accent = ToHex(FromHsv(hue, sat, val));
        Changed();
    }

    void AccentHex_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var hex = AccentHex.Text.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$")) SetAccent(hex.ToUpperInvariant());
        else AccentHex.Text = Settings.Accent; // not a colour: put back what is in use
    }

    void Strength_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.AccentStrength = e.NewValue / 100;
        Changed();
    }

    void RowHeight_Click(object s, RoutedEventArgs e)
    {
        Settings.RowPad = s == RowCompact ? 1 : s == RowRoomy ? 6 : 3;
        Changed();
    }

    void TextSize_Click(object s, RoutedEventArgs e)
    {
        Settings.TextScale = s == Text115 ? 1.15 : s == Text130 ? 1.3 : s == Text150 ? 1.5 : 1;
        Changed();
        ApplyTextSize();
    }

    /// Settings → Text size covers this window too (Rico, 1.0.61), as it does the main window: the whole of it, scaled.
    void ApplyTextSize() => Root.LayoutTransform = Settings.TextScale > 1 ? new System.Windows.Media.ScaleTransform(Settings.TextScale, Settings.TextScale) : null;

    void Scale_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        var v = Math.Round(e.NewValue) / 100;
        if (s == ScaleSidebar) Settings.SidebarScale = v;
        else if (s == ScaleTabs) Settings.TabsScale = v;
        else if (s == ScalePath) Settings.PathScale = v;
        else Settings.ListScale = v;
        Changed();
    }

    void SinglePane_Click(object s, RoutedEventArgs e) => main.SetSinglePane(SinglePaneBox.IsChecked == true);

    void Stats_Click(object s, RoutedEventArgs e)
    {
        Settings.Stats = StatsBox.IsChecked != true ? 0 : StatsCompactBox.IsChecked == true ? 1 : 2;
        StatsCompactBox.IsEnabled = Settings.Stats > 0;
        Settings.Save();
        main.ApplyStats();
    }

    void Animations_Click(object s, RoutedEventArgs e) { Settings.Animations = AnimationsBox.IsChecked == true; Settings.Save(); }
    void CloseEmptyPane_Click(object s, RoutedEventArgs e) { Settings.CloseEmptyPane = CloseEmptyPaneBox.IsChecked == true; Settings.Save(); main.ApplyCloseEmptyPane(); }
    void Autoscroll_Click(object s, RoutedEventArgs e)
    {
        Settings.Autoscroll = AutoscrollBox.IsChecked == true;
        Settings.AutoscrollSideways = AutoscrollUpDownBox.IsChecked != true;
        AutoscrollUpDownBox.IsEnabled = Settings.Autoscroll;
        Settings.Save();
    }
    void TipDelay_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.TipDelay = (int)Math.Round(e.NewValue);
        TipDelayText.Text = $"{Settings.TipDelay / 1000.0:0.0} s";
        Settings.Save();
    }
    void SmoothScroll_Click(object s, RoutedEventArgs e) { Settings.SmoothScroll = SmoothScrollBox.IsChecked == true; Settings.Save(); }

    void Refresh()
    {
        BdAcrylic.Tag = Settings.Backdrop == 3 ? "on" : null;
        BdMica.Tag = Settings.Backdrop == 2 ? "on" : null;
        BdSolid.Tag = Settings.Backdrop == 1 ? "on" : null;
        OpacitySlider.IsEnabled = Settings.Backdrop != 1;
        MenuOwn.Tag = Settings.NativeMenu ? null : "on";
        MenuWin.Tag = Settings.NativeMenu ? "on" : null;
        foreach (var (b, v) in new[] { (Limit1k, 1_000), (Limit10k, 10_000), (Limit100k, 100_000), (LimitNone, 0) })
            b.Tag = Settings.SearchLimit == v ? "on" : null;
        OpacityText.Text = Settings.Backdrop == 1 ? "—" : $"{Settings.Opacity:0}%";
        BgChoose.Tag = Settings.BackgroundImage != "" ? "on" : null;
        BgNone.Tag = Settings.BackgroundImage == "" ? "on" : null;
        BgChoose.ToolTip = Settings.BackgroundImage != "" ? Settings.BackgroundImage : "Pick a picture";
        BgOpacitySlider.IsEnabled = BgBlurSlider.IsEnabled = BgBrightSlider.IsEnabled = Settings.BackgroundImage != "";
        BgBlurText.Text = Settings.BackgroundImage != "" ? $"{Settings.ImageBlur:0}" : "—";
        BgBrightText.Text = Settings.BackgroundImage != "" ? $"{Settings.ImageBrightness:+0;-0;0}" : "—";
        BgOpacityText.Text = Settings.BackgroundImage != "" ? $"{Settings.ImageOpacity:0}%" : "—";
        RowCompact.Tag = Settings.RowPad <= 1 ? "on" : null;
        RowNormal.Tag = Settings.RowPad is > 1 and < 6 ? "on" : null;
        RowRoomy.Tag = Settings.RowPad >= 6 ? "on" : null;
        ScaleSidebarText.Text = $"{Settings.SidebarScale * 100:0}%"; ScaleTabsText.Text = $"{Settings.TabsScale * 100:0}%";
        ScalePathText.Text = $"{Settings.PathScale * 100:0}%"; ScaleListText.Text = $"{Settings.ListScale * 100:0}%";
        foreach (var (b, v) in new[] { (Text100, 1.0), (Text115, 1.15), (Text130, 1.3), (Text150, 1.5) })
            b.Tag = Math.Abs(Settings.TextScale - v) < 0.01 ? "on" : null;
        AccentHex.Text = Settings.Accent;
        ShowAccentOnWheel();
        StrengthText.Text = $"{Settings.AccentStrength * 100:0}%";
        foreach (System.Windows.Controls.Border dot in Swatches.Children)
            dot.BorderBrush = (string)dot.ToolTip == Settings.Accent ? MainWindow.Hex("#FFFFFF") : System.Windows.Media.Brushes.Transparent;
        Root.Background = MainWindow.Tint();
    }

    void Changed()
    {
        Settings.Save();
        Refresh();
        main.ApplyAppearance();
        MainWindow.SetBackdrop(this);
    }

    void Backdrop_Click(object s, RoutedEventArgs e)
    {
        Settings.Backdrop = s == BdAcrylic ? 3 : s == BdMica ? 2 : 1;
        Changed();
    }

    void BgChoose_Click(object s, RoutedEventArgs e)
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Background image", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*" };
        if (pick.ShowDialog(this) != true) return;
        Settings.BackgroundImage = pick.FileName;
        Changed();
    }

    void BgNone_Click(object s, RoutedEventArgs e) { Settings.BackgroundImage = ""; Changed(); }

    void BgOpacity_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.ImageOpacity = e.NewValue;
        Changed();
    }

    void BgBlur_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.ImageBlur = Math.Round(e.NewValue);
        Changed();
    }

    void BgBright_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.ImageBrightness = Math.Round(e.NewValue);
        Changed();
    }

    void Opacity_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loading) return;
        Settings.Opacity = e.NewValue;
        Changed();
    }

    void SearchLimit_Click(object s, RoutedEventArgs e)
    {
        Settings.SearchLimit = s == Limit1k ? 1_000 : s == Limit10k ? 10_000 : s == Limit100k ? 100_000 : 0;
        Settings.Save();
        Refresh();
    }

    void Menu_Click(object s, RoutedEventArgs e)
    {
        Settings.NativeMenu = s == MenuWin;
        Settings.Save();
        Refresh();
    }

    // The manual check ignores a "Later": asking explicitly deserves an answer.
    async void Update_Click(object s, RoutedEventArgs e)
    {
        UpdateCheck.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await main.CheckForUpdate(manual: true) ?? "";
        UpdateCheck.IsEnabled = true;
    }

    void Link_Navigate(object s, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    void Hidden_Click(object s, RoutedEventArgs e) { Settings.ShowHidden = HiddenBox.IsChecked == true; Settings.Save(); main.RefreshPanes(); }
    void ShortcutFolders_Click(object s, RoutedEventArgs e) { Settings.ShortcutsWithFolders = ShortcutFoldersBox.IsChecked == true; Settings.Save(); main.ResortPanes(); }
    void Editor_Click(object s, RoutedEventArgs e) { Settings.InternalEditor = EditorBox.IsChecked == true; Settings.Save(); }
    void QuickLook_Click(object s, RoutedEventArgs e) { Settings.QuickLook = QuickLookBox.IsChecked == true; Settings.Save(); }
    void ItemTips_Click(object s, RoutedEventArgs e)
    {
        Settings.ItemTips = ItemTipsBox.IsChecked == true; Settings.Save();
        Application.Current.Resources["ItemTips"] = Settings.ItemTips;
    }
    void DeleteSound_Click(object s, RoutedEventArgs e) { Settings.DeleteSound = DeleteSoundBox.IsChecked == true; Settings.Save(); }
    void Verify_Click(object s, RoutedEventArgs e) { Settings.Verify = VerifyBox.IsChecked == true; Settings.Save(); }
    void Parallel_Click(object s, RoutedEventArgs e) { Settings.ParallelCopy = ParallelBox.IsChecked == true; Settings.Save(); }
    void Tray_Click(object s, RoutedEventArgs e)
    {
        Settings.CloseToTray = TrayBox.IsChecked == true;
        Settings.Save();
        main.ApplyTray();
        // starting hidden makes no sense without the icon that brings the window back
        if (!Settings.CloseToTray && StartupBox.IsChecked == true) { StartupBox.IsChecked = false; Startup_Click(s, e); }
    }

    void Startup_Click(object s, RoutedEventArgs e)
    {
        // starting hidden needs the icon by the clock to bring the window back, so it turns that on too
        if (StartupBox.IsChecked == true && !Settings.CloseToTray) { TrayBox.IsChecked = true; Settings.CloseToTray = true; Settings.Save(); main.ApplyTray(); }
        try { Settings.StartWithWindows = StartupBox.IsChecked == true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { StartupBox.IsChecked = Settings.StartWithWindows; }
    }
    void WhatsNew_Click(object s, RoutedEventArgs e) => main.OpenWhatsNew();

    void Default_Click(object s, RoutedEventArgs e)
    {
        try { Settings.SetDefaultFileManager(DefaultBox.IsChecked == true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change the default file manager");
        }
        DefaultBox.IsChecked = Settings.IsDefaultFileManager; // show what actually happened
    }

    void WinE_Click(object s, RoutedEventArgs e)
    {
        Settings.TakeWinE = WinEBox.IsChecked == true;
        Settings.Save(); // the agent reads it from the file at each Win+E
        if (!Settings.IsDefaultFileManager) return; // taken or not when File Labs becomes the default
        try { Settings.SetWinE(Settings.TakeWinE); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change Win+E");
        }
    }
}
