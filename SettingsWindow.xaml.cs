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
        AboutText.Text = $"File Labs {typeof(App).Assembly.GetName().Version?.ToString(3)} · Protagonist Labs\n" +
                         "Transfers run on the Ferry engine. Settings live in " + Settings.Dir;
        Refresh();
    }

    void Refresh()
    {
        BdAcrylic.Tag = Settings.Backdrop == 3 ? "on" : null;
        BdMica.Tag = Settings.Backdrop == 2 ? "on" : null;
        BdSolid.Tag = Settings.Backdrop == 1 ? "on" : null;
        OpacitySlider.IsEnabled = Settings.Backdrop != 1;
        MenuOwn.Tag = Settings.NativeMenu ? null : "on";
        MenuWin.Tag = Settings.NativeMenu ? "on" : null;
        OpacityText.Text = Settings.Backdrop == 1 ? "—" : $"{Settings.Opacity:0}%";
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
