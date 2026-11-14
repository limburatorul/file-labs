using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace FileExplorer;

/// The small questions (a name, yes or no), in File Labs' own look instead of a system box, as on Windows (PromptDialog).
static class Dialogs
{
    static Window Frame(string title, Control body, double width = 420)
    {
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Fmt.Brush("#1C2029"), Foreground = Fmt.Brush("#E8EEF6"), FontSize = 13, ShowInTaskbar = false,
            Content = new Border { Padding = new(20, 16, 20, 18), Child = body },
        };
        w.Opened += (_, _) => w.Activate(); // on KDE it could open behind its maximized owner
        return w;
    }

    static Button Choice(string text, bool primary) =>
        new() { Content = text, Classes = { "job", primary ? "on" : "plain" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

    /// A name; null when cancelled. selectStem: the part before the extension is selected, as for a rename.
    public static async Task<string> Ask(Window owner, string title, string label, string initial, bool selectStem = false)
    {
        var box = new TextBox { Text = initial, Margin = new(0, 8, 0, 14), MinHeight = 32 };
        var ok = Choice("OK", true); var cancel = Choice("Cancel", false);
        var w = Frame(title, new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        });
        string answer = null;
        ok.Click += (_, _) => { answer = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { answer = box.Text; w.Close(); } else if (e.Key == Key.Escape) w.Close(); };
        w.Opened += (_, _) =>
        {
            box.Focus();
            int dot = selectStem ? (initial ?? "").LastIndexOf('.') : -1;
            box.SelectionStart = 0; box.SelectionEnd = dot > 0 ? dot : (initial ?? "").Length;
        };
        await w.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }

    /// Yes or no; yes is the first button and the one Enter presses.
    public static async Task<bool> Confirm(Window owner, string title, string text, string yes = "Delete", string no = "Cancel")
    {
        var y = Choice(yes, true); var n = Choice(no, false);
        var w = Frame(title, new StackPanel
        {
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { y, n } },
            },
        });
        bool result = false;
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Opened += (_, _) => n.Focus(); // the safe answer has the keyboard
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task Info(Window owner, string title, string text)
    {
        var ok = Choice("OK", true);
        var w = Frame(title, new StackPanel
        {
            Children =
            {
                new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
            },
        }, 460);
        ok.Click += (_, _) => w.Close();
        w.Opened += (_, _) => ok.Focus();
        await w.ShowDialog(owner);
    }
}
