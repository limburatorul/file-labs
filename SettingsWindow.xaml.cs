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
        OpacitySlider.Value = Settings.Opacity;
        HiddenBox.IsChecked = Settings.ShowHidden;
        VerifyBox.IsChecked = Settings.Verify;
        DefaultBox.IsChecked = Settings.IsDefaultFileManager;
        AnimationsBox.IsChecked = Settings.Animations;
        StrengthSlider.Value = Settings.AccentStrength * 100;
        BuildSwatches();
        BuildWheel();
        AboutText.Text = $"File Labs {typeof(App).Assembly.GetName().Version?.ToString(3)} · Protagonist Labs\n" +
                         "Transfers run on the Ferry engine. Settings live in " + Settings.Dir;
        Refresh();
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
        if (!IsLoaded || syncing) return;
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
        if (!IsLoaded) return;
        Settings.AccentStrength = e.NewValue / 100;
        Changed();
    }

    void RowHeight_Click(object s, RoutedEventArgs e)
    {
        Settings.RowPad = s == RowCompact ? 1 : s == RowRoomy ? 6 : 3;
        Changed();
    }

    void Animations_Click(object s, RoutedEventArgs e) { Settings.Animations = AnimationsBox.IsChecked == true; Settings.Save(); }

    void Refresh()
    {
        BdAcrylic.Tag = Settings.Backdrop == 3 ? "on" : null;
        BdMica.Tag = Settings.Backdrop == 2 ? "on" : null;
        BdSolid.Tag = Settings.Backdrop == 1 ? "on" : null;
        OpacitySlider.IsEnabled = Settings.Backdrop != 1;
        MenuOwn.Tag = Settings.NativeMenu ? null : "on";
        MenuWin.Tag = Settings.NativeMenu ? "on" : null;
        OpacityText.Text = Settings.Backdrop == 1 ? "—" : $"{Settings.Opacity:0}%";
        RowCompact.Tag = Settings.RowPad <= 1 ? "on" : null;
        RowNormal.Tag = Settings.RowPad is > 1 and < 6 ? "on" : null;
        RowRoomy.Tag = Settings.RowPad >= 6 ? "on" : null;
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

    void Opacity_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        Settings.Opacity = e.NewValue;
        Changed();
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
    void Verify_Click(object s, RoutedEventArgs e) { Settings.Verify = VerifyBox.IsChecked == true; Settings.Save(); }

    void Default_Click(object s, RoutedEventArgs e)
    {
        try { Settings.SetDefaultFileManager(DefaultBox.IsChecked == true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change the default file manager");
        }
        DefaultBox.IsChecked = Settings.IsDefaultFileManager; // show what actually happened
    }
}
