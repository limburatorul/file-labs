using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FileExplorer;

// One-line prompt in the app's own style (New folder, Rename) — the VB InputBox it replaces was a
// Windows 95 dialog in the middle of a dark window.
public partial class PromptDialog : Window
{
    PromptDialog(Window owner, string title, string label, string text, string okText, bool selectAll)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        Label.Text = label;
        OkButton.Content = okText;
        Input.Text = text;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Root.Background = MainWindow.Tint();
        Loaded += (_, _) =>
        {
            Input.Focus();
            // select the name without its extension: renaming report.txt usually keeps the .txt
            var dot = Path.GetFileNameWithoutExtension(text).Length;
            Input.Select(0, dot > 0 && !selectAll ? dot : text.Length);
        };
    }

    /// The text the user entered, or null if cancelled or left empty. `selectAll` for text that isn't a
    /// file name (a pattern, a hash), where keeping the "extension" out of the selection makes no sense.
    public static string Ask(Window owner, string title, string label, string text, string okText, bool selectAll = false)
    {
        var dialog = new PromptDialog(owner, title, label, text, okText, selectAll);
        return dialog.ShowDialog() == true && dialog.Input.Text.Trim() != "" ? dialog.Input.Text.Trim() : null;
    }

    /// A question with buttons, in the same glass as Ask: returns the index of the button pressed, or -1
    /// for Esc / the close box. The first button is the highlighted default (Enter). `detail` is shown
    /// dimmer under the question, for file names or an error message.
    public static int Choose(Window owner, string question, string detail, params string[] buttons)
    {
        var w = new Window
        {
            Owner = owner, Title = "File Labs", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Background = System.Windows.Media.Brushes.Transparent,
            Foreground = MainWindow.Hex("#E8EEF6"), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12,
        };
        w.SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(w);
        int result = -1;
        var panel = new System.Windows.Controls.StackPanel { Margin = new(20, 18, 18, 14) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = question, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = detail, Foreground = MainWindow.Hex("#8A97AA"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new System.Windows.Controls.Button
            {
                Content = buttons[i], Style = (Style)Application.Current.FindResource("Tab"), Padding = new(14, 6, 14, 6), Margin = new(6, 0, 0, 0),
                Tag = i == 0 ? "on" : null, IsDefault = i == 0, MaxWidth = double.PositiveInfinity,
            };
            b.Click += (_, _) => { result = index; w.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.Content = new System.Windows.Controls.Grid { Background = MainWindow.Tint(), Children = { panel } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.ShowDialog();
        return result;
    }

    void Ok_Click(object s, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object s, RoutedEventArgs e) => Close();

    void Input_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) Close();
    }
}
