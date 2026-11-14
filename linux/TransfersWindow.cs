using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace FileExplorer;

/// Every copy and move, as on Windows: a card per job with a band of its own colour, progress, figures, its buttons,
/// and the conflicts it found with the four answers (Keep both is the default).
sealed class TransfersWindow : Window
{
    readonly List<Job> jobs;
    readonly MainWindow main;
    readonly StackPanel cards = new() { Margin = new(14, 12, 14, 6) };

    public TransfersWindow(List<Job> jobs, MainWindow main)
    {
        this.jobs = jobs; this.main = main;
        Title = "Transfers — File Labs";
        Width = 640; Height = 260; MinWidth = 460; MinHeight = 160;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Rebuild();
    }

    public void Rebuild()
    {
        cards.Children.Clear();
        foreach (var j in jobs) cards.Children.Add(Card(j));
        Height = Math.Clamp(40 + jobs.Sum(j => j.HasConflicts ? 250 : 130), 170, 720);
    }

    static Binding B(string path) => new(path) { Mode = BindingMode.OneWay };

    Control Card(Job j)
    {
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, B(nameof(Job.Title)));
        var pct = new TextBlock { FontSize = 13, Margin = new(12, 0, 0, 0) };
        pct.Bind(TextBlock.TextProperty, B(nameof(Job.PercentText)));
        var head = new DockPanel { Children = { pct, title } };
        DockPanel.SetDock(pct, Dock.Right);

        var bar = new ProgressBar { Height = 6, Margin = new(0, 8, 0, 6), Foreground = j.Hue };
        bar.Bind(RangeBase.ValueProperty, B(nameof(Job.Fraction)));

        var current = new TextBlock { Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        current.Bind(TextBlock.TextProperty, B(nameof(Job.Current)));
        var currentBar = new ProgressBar { Width = 100, Height = 3, Margin = new(10, 0, 0, 0), Foreground = Fmt.Brush("#8A97AA") };
        currentBar.Bind(RangeBase.ValueProperty, B(nameof(Job.CurrentFraction)));
        var currentRow = new DockPanel { Margin = new(0, 0, 0, 4), Children = { currentBar, current } };
        DockPanel.SetDock(currentBar, Dock.Right);
        currentRow.Bind(IsVisibleProperty, B(nameof(Job.Copying)));

        var errors = new TextBlock { Foreground = Fmt.Brush("#F87171"), FontSize = 11, Margin = new(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        errors.Bind(TextBlock.TextProperty, B(nameof(Job.ErrorSummary)));
        errors.Bind(IsVisibleProperty, B(nameof(Job.HasErrors)));
        errors.Bind(ToolTip.TipProperty, B(nameof(Job.ErrorDetail)));

        Button Action(string glyph, string text, Action run, string visibleWhen)
        {
            var t = new TextBlock();
            var icon = new Run(glyph) { FontFamily = new FontFamily(Glyph.Font) };
            t.Inlines!.Add(icon); t.Inlines.Add(new Run("  " + text));
            var b = new Button { Classes = { "job" }, Content = t };
            b.Bind(IsVisibleProperty, B(visibleWhen));
            b.Click += (_, _) => run();
            return b;
        }
        var pause = Action(Glyph.Pause, "Pause", () => { if (j.Paused) j.Resume(); else j.Pause(); }, nameof(Job.Copying));
        j.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (pause.Content is TextBlock tb && tb.Inlines is { Count: 2 } l) { ((Run)l[0]).Text = j.PauseGlyph; ((Run)l[1]).Text = "  " + j.PauseText; }
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                pause,
                Action(Glyph.Close, "Cancel", j.Cancel, nameof(Job.Active)),
                Action(Glyph.Undo, "Undo", () => main.UndoJob(j), nameof(Job.CanUndo)),
                Action(Glyph.Check, "Dismiss", () => main.DismissJob(j), nameof(Job.Finished)),
            },
        };
        var stats = new TextBlock { Foreground = Fmt.Brush("#8A97AA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        stats.Bind(TextBlock.TextProperty, B(nameof(Job.Stats)));
        var foot = new DockPanel { Children = { buttons, stats } };
        DockPanel.SetDock(buttons, Dock.Right);

        // what it needs from you: the files already there, and one answer for all of them
        var conflictTitle = new TextBlock { Foreground = Fmt.Brush("#FFD23F"), FontWeight = FontWeight.SemiBold };
        conflictTitle.Bind(TextBlock.TextProperty, B(nameof(Job.ConflictTitle)));
        var list = new StackPanel();
        var choices = new UniformGrid { Rows = 1, Margin = new(0, 0, -6, 0) };
        foreach (var (text, c, on) in new[] { ("Keep both", Choice.Rename, true), ("Replace if newer", Choice.Newer, false), ("Replace", Choice.Overwrite, false), ("Skip", Choice.Skip, false) })
        {
            var b = new Button { Content = text, Classes = { "job", "choice" } };
            if (on) b.Classes.Add("on");
            var choice = c;
            b.Click += (_, _) => j.Resolve(choice);
            choices.Children.Add(b);
        }
        var conflicts = new StackPanel { Margin = new(0, 10, 0, 0), Children = { conflictTitle, new ScrollViewer { MaxHeight = 96, Margin = new(0, 4, 0, 8), Content = list }, choices } };
        conflicts.Bind(IsVisibleProperty, B(nameof(Job.HasConflicts)));
        j.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!j.HasConflicts || list.Children.Count > 0) return;
            foreach (var c in j.Conflicts.Take(200))
            {
                var detail = new TextBlock { Text = c.Detail, Foreground = Fmt.Brush("#8A97AA"), FontSize = 11 };
                var row = new DockPanel { Margin = new(0, 1), Children = { detail, new TextBlock { Text = c.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 12, 0) } } };
                DockPanel.SetDock(detail, Dock.Right);
                list.Children.Add(row);
            }
            Rebuild();
        });

        var body = new StackPanel { Children = { head, bar, currentRow, errors, foot, conflicts } };
        return new Border
        {
            DataContext = j, Background = Fmt.Brush("#0DFFFFFF"), BorderBrush = j.Hue, BorderThickness = new(3, 0, 0, 0), CornerRadius = new(4, 10, 10, 4), Margin = new(0, 0, 0, 8),
            Child = new Border { BorderBrush = Fmt.Brush("#16FFFFFF"), BorderThickness = new(0, 1, 1, 1), CornerRadius = new(0, 10, 10, 0), Padding = new(12, 10), Child = body },
        };
    }
}
