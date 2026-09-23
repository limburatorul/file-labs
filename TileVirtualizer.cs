using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FileExplorer;

// The icon and list views used a plain WrapPanel, which builds every item: switching C:\Windows\System32
// (5 000 files) to XL icons froze the window for seconds. This panel lays items out in a grid of
// equal cells and only creates the ones on screen, the way VirtualizingStackPanel does for rows.
//
// Horizontal (icons): items fill a row, then wrap to the next; scrolls vertically.
// Vertical (List view): items fill a column, then wrap to the next; scrolls horizontally.
public class TileVirtualizer : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(TileVirtualizer), new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty TileHeightProperty =
        DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(TileVirtualizer), new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(TileVirtualizer), new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    bool Wraps => Orientation == Orientation.Horizontal; // rows that wrap downwards

    int lanes = 1;      // items across a row (or down a column)
    int first, last;    // the range currently realized
    Size extent, viewport;
    Point offset;

    protected override Size MeasureOverride(Size available)
    {
        _ = InternalChildren; // touching it first is what hooks up the item generator (WPF requires this)
        var owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        // an infinite dimension (inside a ScrollViewer that doesn't scroll us) means "one lane"
        double across = Wraps ? available.Width : available.Height;
        if (double.IsInfinity(across)) across = Wraps ? TileWidth : TileHeight;
        lanes = Math.Max(1, (int)(across / (Wraps ? TileWidth : TileHeight)));
        int strips = (int)Math.Ceiling(count / (double)lanes);

        extent = Wraps ? new Size(lanes * TileWidth, strips * TileHeight) : new Size(strips * TileWidth, lanes * TileHeight);
        viewport = new Size(double.IsInfinity(available.Width) ? extent.Width : available.Width,
                            double.IsInfinity(available.Height) ? extent.Height : available.Height);
        offset = new Point(Math.Max(0, Math.Min(offset.X, extent.Width - viewport.Width)),
                           Math.Max(0, Math.Min(offset.Y, extent.Height - viewport.Height)));
        ScrollOwner?.InvalidateScrollInfo();

        // one strip of slack on each side, so scrolling doesn't create items exactly at the edge
        double start = Wraps ? offset.Y : offset.X, size = Wraps ? TileHeight : TileWidth;
        double window = Wraps ? viewport.Height : viewport.Width;
        int firstStrip = Math.Max(0, (int)(start / size) - 1);
        int lastStrip = Math.Min(strips - 1, (int)((start + window) / size) + 1);
        first = count == 0 ? 0 : firstStrip * lanes;
        last = count == 0 ? -1 : Math.Min(count - 1, lastStrip * lanes + lanes - 1);

        Realize(count);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(TileWidth, TileHeight));
        return new Size(Math.Min(available.Width, extent.Width), Math.Min(available.Height, extent.Height));
    }

    void Realize(int count)
    {
        if (count == 0) { Drop(0, -1); return; }
        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(first);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            for (int i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out bool isNew);
                if (child == null) break;
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
            }
        Drop(first, last);
    }

    /// Throws away the containers that fell outside the visible range.
    void Drop(int keepFrom, int keepTo)
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int item = generator.IndexFromGeneratorPosition(position);
            if (item >= keepFrom && item <= keepTo) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size size)
    {
        var generator = ItemContainerGenerator;
        foreach (UIElement child in InternalChildren)
        {
            int item = generator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(child), 0));
            if (item < 0) continue;
            int strip = item / lanes, lane = item % lanes;
            var at = Wraps ? new Point(lane * TileWidth - offset.X, strip * TileHeight - offset.Y)
                           : new Point(strip * TileWidth - offset.X, lane * TileHeight - offset.Y);
            child.Arrange(new Rect(at, new Size(TileWidth, TileHeight)));
        }
        return size;
    }

    // When items are added or removed, the containers we hold no longer match their items.
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            RemoveInternalChildRange(0, InternalChildren.Count);
        InvalidateMeasure();
    }

    // ---- IScrollInfo: the panel scrolls itself, which is what lets it virtualize ----
    public ScrollViewer ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => offset.X;
    public double VerticalOffset => offset.Y;

    public void SetHorizontalOffset(double to) => Scroll(to - offset.X, 0);
    public void SetVerticalOffset(double to) => Scroll(0, to - offset.Y);

    void Scroll(double dx, double dy)
    {
        var next = new Point(Math.Max(0, Math.Min(offset.X + dx, Math.Max(0, extent.Width - viewport.Width))),
                             Math.Max(0, Math.Min(offset.Y + dy, Math.Max(0, extent.Height - viewport.Height))));
        if (next == offset) return;
        offset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    const double Step = 48; // a comfortable wheel/line step in pixels

    public void LineUp() => Scroll(0, -Step);
    public void LineDown() => Scroll(0, Step);
    public void LineLeft() => Scroll(-Step, 0);
    public void LineRight() => Scroll(Step, 0);
    public void MouseWheelUp() => Scroll(0, -Step * 2);
    public void MouseWheelDown() => Scroll(0, Step * 2);
    public void MouseWheelLeft() => Scroll(-Step * 2, 0);
    public void MouseWheelRight() => Scroll(Step * 2, 0);
    public void PageUp() => Scroll(0, -viewport.Height);
    public void PageDown() => Scroll(0, viewport.Height);
    public void PageLeft() => Scroll(-viewport.Width, 0);
    public void PageRight() => Scroll(viewport.Width, 0);

    /// Keyboard navigation and ScrollIntoView land here.
    public Rect MakeVisible(System.Windows.Media.Visual visual, Rect rectangle)
    {
        var child = visual as UIElement;
        while (child != null && !InternalChildren.Contains(child))
            child = System.Windows.Media.VisualTreeHelper.GetParent(child) as UIElement;
        if (child == null) return rectangle;
        int item = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(child), 0));
        if (item < 0) return rectangle;

        int strip = item / lanes;
        if (Wraps)
        {
            double top = strip * TileHeight;
            if (top < offset.Y) SetVerticalOffset(top);
            else if (top + TileHeight > offset.Y + viewport.Height) SetVerticalOffset(top + TileHeight - viewport.Height);
        }
        else
        {
            double left = strip * TileWidth;
            if (left < offset.X) SetHorizontalOffset(left);
            else if (left + TileWidth > offset.X + viewport.Width) SetHorizontalOffset(left + TileWidth - viewport.Width);
        }
        return rectangle;
    }

    /// Scrolls to an item that has no container yet — Ctrl+End, ScrollIntoView, type-ahead. Without
    /// this override WPF cannot reach anything outside the realized range.
    protected override void BringIndexIntoView(int index)
    {
        if (lanes <= 0 || index < 0) return;
        int strip = index / lanes;
        if (Wraps) SetVerticalOffset(strip * TileHeight);
        else SetHorizontalOffset(strip * TileWidth);
        UpdateLayout(); // the caller expects the container to exist when this returns
    }
}
