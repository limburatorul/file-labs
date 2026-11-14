using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FileExplorer;

/// Every key and gesture, grouped (F1).
sealed class HelpWindow : Window
{
    static readonly (string Group, (string Key, string What)[] Rows)[] Groups =
    {
        ("Moving around", new[]
        {
            ("Enter / double-click", "Open: a folder here, a file in its app"), ("Backspace / Alt+Up", "Up one folder"), ("Alt+Left / Alt+Right", "Back and forward"),
            ("Tab", "The other pane"), ("Ctrl+L / click the path", "Type a path"), ("Middle-click", "A folder in a new tab"),
        }),
        ("Finding", new[] { ("Just type", "The letters filter this folder; Down goes to the list, Esc clears"), ("Enter in the filter", "Search every subfolder"), ("* and ?", "Wildcards in the filter") }),
        ("Tabs and panes", new[] { ("Ctrl+T / Ctrl+W", "New tab, close tab"), ("Ctrl+Shift+T", "Reopen the last closed tab"), ("Ctrl+Tab", "Next tab"), ("Ctrl+P", "One pane or two"), ("Alt+P", "Preview beside the panes") }),
        ("Files", new[]
        {
            ("F2", "Rename"), ("Ctrl+F5", "Copy to the other pane"), ("F6", "Move to the other pane"), ("F7 / Ctrl+Shift+N", "New folder"), ("Ctrl+N", "New file"),
            ("F8 / Delete", "Move to the Trash"), ("Shift+Delete", "Delete for good (asks first)"), ("Ctrl+C / X / V", "Copy, cut, paste (with Dolphin and Nautilus too)"),
            ("Ctrl+Shift+C", "Copy the path"), ("Ctrl+Z", "Undo the last change, a transfer included"), ("Ctrl+D", "Pin to Quick access"), ("Alt+Enter", "Properties"),
            ("Drag", "Onto a folder: into it. Onto empty space or past the columns: here. Ctrl copies, Shift moves"),
        }),
        ("Looking", new[] { ("Space", "Quick View (Up and Down step through)"), ("F4", "Edit a text file"), ("Ctrl+Shift+1…5", "Details, List, M, L, XL icons"), ("Ctrl+H", "Hidden files"), ("F5 / Ctrl+R", "Refresh / measure folders again"), ("Ctrl+`", "Terminal here") }),
    };

    public HelpWindow()
    {
        Title = "Help — File Labs";
        Width = 720; Height = 640;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new(24, 18), Spacing = 6 };
        foreach (var (group, rows) in Groups)
        {
            stack.Children.Add(new TextBlock { Text = group, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new(0, 12, 0, 4), Foreground = (IBrush)Application.Current!.FindResource("Accent") });
            foreach (var (key, what) in rows)
                stack.Children.Add(new DockPanel
                {
                    Children =
                    {
                        new TextBlock { Text = key, Width = 200, Foreground = Fmt.Brush("#B4BFCE"), [DockPanel.DockProperty] = Dock.Left },
                        new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap },
                    },
                });
        }
        Content = new ScrollViewer { Content = stack };
        KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Escape or Avalonia.Input.Key.F1) Close(); };
    }
}

/// What's new, from the same whatsnew.txt as Windows: every release, newest first.
sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        Title = "What’s new — File Labs";
        Width = 760; Height = 640;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new(24, 18), Spacing = 4 };
        using var s = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream(typeof(WhatsNewWindow).Assembly.GetManifestResourceNames().First(n => n.EndsWith("whatsnew.txt")));
        using var r = new StreamReader(s!);
        string line; int releases = 0;
        while ((line = r.ReadLine()) != null && releases <= 12)
        {
            if (line.StartsWith('#') && !line.StartsWith("## ")) continue;
            if (line.StartsWith("## "))
            {
                releases++;
                var parts = line[3..].Split('|');
                stack.Children.Add(new TextBlock { Text = parts.Length > 2 ? parts[2].Trim() : line[3..], FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new TextBlock { Text = $"{parts[0].Trim()}  ·  {(parts.Length > 1 ? parts[1].Trim() : "")}", Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, Margin = new(0, 0, 0, 6) });
            }
            else if (line.StartsWith("**") && line.EndsWith("**")) stack.Children.Add(new TextBlock { Text = line.Trim('*'), FontWeight = FontWeight.SemiBold, Margin = new(0, 8, 0, 2), Foreground = (IBrush)Application.Current!.FindResource("Accent") });
            else if (line.StartsWith("- ")) stack.Children.Add(new TextBlock { Text = "•  " + line[2..].Replace("**", ""), TextWrapping = TextWrapping.Wrap, Margin = new(8, 0, 0, 2) });
            else if (line.Trim() != "") stack.Children.Add(new TextBlock { Text = line.Replace("**", ""), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        }
        Content = new ScrollViewer { Content = stack };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }
}
