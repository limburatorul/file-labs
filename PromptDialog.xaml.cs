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

    /// A few labelled boxes in the same glass, each with its own unit buttons when it has `Units` (KB, MB…).
    /// Empty boxes are allowed. `check` names what's wrong with the entries, shown under them while the
    /// dialog stays open, or returns null to accept. Returns each box's text and chosen unit, or null for Cancel.
    public static (string Text, int Unit)[] Fields(Window owner, string title, string hint,
        (string Label, string Text, string[] Units, int Unit)[] rows, Func<(string Text, int Unit)[], string> check)
    {
        var w = new Window
        {
            Owner = owner, Title = title, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = System.Windows.Media.Brushes.Transparent,
            Foreground = MainWindow.Hex("#E8EEF6"), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12,
        };
        w.SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(w);
        var tab = (Style)Application.Current.FindResource("Tab");
        var panel = new System.Windows.Controls.StackPanel { Margin = new(18, 16, 18, 14) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = hint, Foreground = MainWindow.Hex("#8A97AA"), TextWrapping = TextWrapping.Wrap, Margin = new(2, 0, 0, 10) });
        var boxes = new System.Windows.Controls.TextBox[rows.Length];
        var units = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int index = i;
            units[i] = rows[i].Unit;
            var line = new System.Windows.Controls.DockPanel { Margin = new(0, 0, 0, 8) };
            line.Children.Add(new System.Windows.Controls.TextBlock { Text = rows[i].Label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            System.Windows.Controls.DockPanel.SetDock(line.Children[0], System.Windows.Controls.Dock.Left);
            if (rows[i].Units is { } names)
            {
                var choice = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new(6, 0, 0, 0) };
                for (int u = 0; u < names.Length; u++)
                {
                    int unit = u;
                    var b = new System.Windows.Controls.Button { Content = names[u], Style = tab, Padding = new(8, 5, 8, 5), Tag = u == units[i] ? "on" : null };
                    b.Click += (_, _) =>
                    {
                        units[index] = unit;
                        foreach (System.Windows.Controls.Button other in choice.Children) other.Tag = other == b ? "on" : null;
                    };
                    choice.Children.Add(b);
                }
                System.Windows.Controls.DockPanel.SetDock(choice, System.Windows.Controls.Dock.Right);
                line.Children.Add(choice);
            }
            boxes[i] = new System.Windows.Controls.TextBox { Text = rows[i].Text, Style = (Style)Application.Current.FindResource("Bare"), FontSize = 13 };
            System.Windows.Automation.AutomationProperties.SetName(boxes[i], rows[i].Label);
            line.Children.Add(new System.Windows.Controls.Border
            {
                Background = MainWindow.Hex("#12FFFFFF"), CornerRadius = new(6), Padding = new(10, 6, 10, 6), BorderBrush = MainWindow.Hex("#2EFFFFFF"), BorderThickness = new(1),
                Child = boxes[i],
            });
            panel.Children.Add(line);
        }
        var error = new System.Windows.Controls.TextBlock { Foreground = MainWindow.Hex("#FF7A7A"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new(2, 0, 0, 4) };
        panel.Children.Add(error);
        foreach (var box in boxes) box.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed; // being corrected
        (string, int)[] result = null;
        (string Text, int Unit)[] Read() => boxes.Select((b, i) => (b.Text.Trim(), units[i])).ToArray();
        void Accept()
        {
            var entries = Read();
            if (check(entries) is { } wrong) { error.Text = wrong; error.Visibility = Visibility.Visible; return; }
            result = entries;
            w.Close();
        }
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Style = tab, Padding = new(14, 6, 14, 6), Margin = new(0, 0, 6, 0) };
        cancel.Click += (_, _) => w.Close();
        var ok = new System.Windows.Controls.Button { Content = "Use", Style = tab, Tag = "on", Padding = new(16, 6, 16, 6) };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        w.Content = new System.Windows.Controls.Grid { Background = MainWindow.Tint(), Children = { panel } };
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        w.Loaded += (_, _) => { boxes[0].Focus(); boxes[0].SelectAll(); };
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
