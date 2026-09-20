using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileExplorer;

// The sidebar's three sections as panels: a header that collapses them, and drag-and-drop on that
// header to reorder. Order and collapsed state are remembered.
public partial class MainWindow
{
    // The content panels live in MainWindow.xaml; a section is just a header wrapped around one.
    (string Key, string Title, string Glyph, Panel Content)[] Sections => new[]
    {
        ("quick",     "Quick access", "", (Panel)QuickAccess),
        ("favorites", "Favorites",    "", (Panel)Favorites),
        ("drives",    "Drives",       "", (Panel)Drives),
    };

    void BuildSidebar()
    {
        // One drop target for the whole sidebar. Per-section DragEnter/DragLeave flickered: those fire
        // again for every child the pointer crosses inside a section.
        if (!SidebarHost.AllowDrop)
        {
            SidebarHost.AllowDrop = true;
            SidebarHost.DragOver += Sidebar_DragOver;
            // DragLeave bubbles up from every child the pointer crosses, so it only counts when the
            // pointer is really outside the sidebar — otherwise the line blinked off and on again.
            SidebarHost.DragLeave += (_, e) =>
            {
                var p = e.GetPosition(SidebarHost);
                if (p.X < 0 || p.Y < 0 || p.X > SidebarHost.ActualWidth || p.Y > SidebarHost.ActualHeight) Highlight(null);
            };
            SidebarHost.Drop += Sidebar_Drop;
        }
        SidebarHost.Children.Clear();
        var known = Sections.ToDictionary(s => s.Key);
        // saved order first, then anything it doesn't mention (a section added in a later version)
        var order = Settings.SidebarOrder.Where(known.ContainsKey)
            .Concat(Sections.Select(s => s.Key).Where(k => !Settings.SidebarOrder.Contains(k))).ToList();
        Settings.SidebarOrder = order;
        foreach (var key in order) SidebarHost.Children.Add(SectionPanel(known[key]));
    }

    FrameworkElement SectionPanel((string Key, string Title, string Glyph, Panel Content) section)
    {
        bool collapsed = Settings.SidebarCollapsed.Contains(section.Key);

        var chevron = new TextBlock
        {
            Text = collapsed ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 9,
            Foreground = Hex("#8A97AA"), Margin = new(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock { Text = section.Title, Foreground = (Brush)FindResource("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var grip = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11, Foreground = Hex("#59FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0,
        };

        var headerRow = new DockPanel();
        DockPanel.SetDock(grip, Dock.Right);
        headerRow.Children.Add(grip);
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(title);

        // Hover is a style trigger, not code: setting Background on every MouseEnter/MouseLeave made
        // the panel under the pointer flicker as the mouse crossed the header's children.
        var header = new Border { Child = headerRow, Padding = new(8, 5, 6, 5), Cursor = Cursors.Hand, Style = (Style)FindResource("SidebarHeader") };
        grip.SetBinding(UIElement.OpacityProperty, new System.Windows.Data.Binding("IsMouseOver")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Border), 1),
            Converter = new BoolToOpacity(),
        });

        section.Content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (section.Key == "quick") QuickHint.Visibility = collapsed ? Visibility.Collapsed : QuickHint.Visibility;

        var body = new StackPanel();
        body.Children.Add(header);
        if (section.Content.Parent is Panel old) old.Children.Remove(section.Content);
        body.Children.Add(section.Content);
        if (section.Key == "quick")
        {
            if (QuickHint.Parent is Panel hintParent) hintParent.Children.Remove(QuickHint);
            body.Children.Add(QuickHint);
        }

        var wrapper = new Border
        {
            Child = body, Tag = section.Key, CornerRadius = new(7),
            BorderThickness = new(1), BorderBrush = Hex("#16FFFFFF"), Background = Hex("#08FFFFFF"), Margin = new(0, 0, 0, 8), Padding = new(2, 2, 2, 4),
        };

        header.MouseLeftButtonDown += (_, e) => { dragFrom = e.GetPosition(SidebarHost); dragKey = section.Key; };
        header.MouseMove += (_, e) =>
        {
            if (dragKey != section.Key || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(SidebarHost) - dragFrom;
            if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragKey = null;
            DragDrop.DoDragDrop(wrapper, new DataObject("FileLabsSection", section.Key), DragDropEffects.Move);
        };
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (dragKey != section.Key) return; // a plain click: collapse or expand
            dragKey = null;
            Toggle(section.Key, section.Content, chevron);
        };

        return wrapper;
    }

    Border dropTarget;
    bool dropAfter;

    // Which section the pointer is over, and which half of it.
    (Border Target, bool After) SectionAt(DragEventArgs e)
    {
        var y = e.GetPosition(SidebarHost).Y;
        foreach (Border child in SidebarHost.Children)
        {
            var top = child.TranslatePoint(new Point(0, 0), SidebarHost).Y;
            if (y < top + child.ActualHeight) return (child, y > top + child.ActualHeight / 2);
        }
        return SidebarHost.Children.Count > 0 ? ((Border)SidebarHost.Children[^1], true) : (null, false);
    }

    void Sidebar_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileLabsSection") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) { Highlight(null); return; }
        var (target, after) = SectionAt(e);
        if (target == dropTarget && after == dropAfter) return; // nothing changed: leave the visuals alone
        dropAfter = after;
        Highlight(target);
    }

    // A line drawn over the sidebar where the section would land. Nothing in the list changes size,
    // so the sections cannot shift under the pointer and re-trigger the hit test — that was the flicker.
    void Highlight(Border target)
    {
        dropTarget = target;
        if (target == null) { DropLine.Visibility = Visibility.Collapsed; return; }
        // centre the line in the gap between two panels, which is where the dragged one will go
        var top = target.TranslatePoint(new Point(0, 0), SidebarHost).Y;
        double gap = target.Margin.Bottom / 2;
        double y = dropAfter ? top + target.ActualHeight + gap : top - gap;
        Canvas.SetTop(DropLine, Math.Max(0, y - DropLine.Height / 2));
        Canvas.SetLeft(DropLine, 2);
        DropLine.Width = Math.Max(0, SidebarHost.ActualWidth - 4);
        DropLine.Visibility = Visibility.Visible;
    }

    void Sidebar_Drop(object s, DragEventArgs e)
    {
        var target = dropTarget;
        bool after = dropAfter;
        Highlight(null);
        if (e.Data.GetData("FileLabsSection") is not string moved || target?.Tag is not string onto || moved == onto) return;
        Settings.SidebarOrder.Remove(moved);
        int at = Settings.SidebarOrder.IndexOf(onto) + (after ? 1 : 0);
        Settings.SidebarOrder.Insert(Math.Clamp(at, 0, Settings.SidebarOrder.Count), moved);
        Settings.Save();
        BuildSidebar();
        e.Handled = true;
    }

    // 0 when the header isn't hovered, 1 when it is — for the drag grip.
    class BoolToOpacity : System.Windows.Data.IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is true ? 1.0 : 0.0;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    Point dragFrom;
    string dragKey;

    void Toggle(string key, Panel content, TextBlock chevron)
    {
        bool collapsed = !Settings.SidebarCollapsed.Remove(key);
        if (collapsed) Settings.SidebarCollapsed.Add(key);
        content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (key == "quick") QuickHint.Visibility = collapsed || pinned.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        chevron.Text = collapsed ? "" : "";
        Settings.Save();
    }
}
