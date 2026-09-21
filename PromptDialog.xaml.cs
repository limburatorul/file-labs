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

    void Ok_Click(object s, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object s, RoutedEventArgs e) => Close();

    void Input_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) Close();
    }
}
