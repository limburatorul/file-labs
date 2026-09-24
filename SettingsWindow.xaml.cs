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
