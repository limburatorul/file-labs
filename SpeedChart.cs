using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FileExplorer;

// Transfer speed over time, after Shelf's move chart (ScanProgressOverlay.tsx): smooth curve,
// accent fill fading to nothing, three grid lines with speeds, "Ns ago … now", and a ceiling that
// holds the fastest speed seen (50 MB/s floor) so a stretch of small files sits low instead of
// filling the chart. Drawn in real pixels — no stretched strokes.
public class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(object), typeof(SpeedChart), // object: XAML templates reject array-typed properties
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    public static readonly DependencyProperty CeilingProperty = DependencyProperty.Register(
        nameof(Ceiling), typeof(double), typeof(SpeedChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Ceiling { get => (double)GetValue(CeilingProperty); set => SetValue(CeilingProperty, value); }

    const double PadL = 58, PadR = 6, PadT = 6, PadB = 16;
    static readonly Brush Axis = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xAA)));
    static readonly Pen Grid = FrozenPen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), 1);
    static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        var s = Samples as (double at, double value)[];
        double w = ActualWidth, h = ActualHeight, floor = h - PadB, right = w - PadR;
        if (s == null || s.Length < 2 || w < PadL + 20 || Ceiling <= 0) return;
        var accent = (Color)((SolidColorBrush)FindResource("Accent")).Color;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            double y = Math.Round(PadT + (floor - PadT) * frac) + 0.5;
            dc.DrawLine(Grid, new Point(PadL, y), new Point(w, y));
            Label(dc, frac == 1 ? "0" : AxisSpeed(Ceiling * (1 - frac)), PadL - 8, y - 7, dpi, alignRight: true);
        }

        // placed by timestamp, not by index: reports aren't evenly spaced
        double first = s[0].at, span = Math.Max(1, s[^1].at - first);
        var pts = s.Select(p => new Point(PadL + (p.at - first) / span * (right - PadL),
                                          PadT + (floor - PadT) * (1 - Math.Min(1, p.value / Ceiling)))).ToArray();

        // Shelf's smoothPath: cubic per segment, controls at the horizontal midpoint holding each
        // endpoint's height — smooth, and it can't overshoot below zero the way Catmull-Rom does.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var a = area.Open())
        using (var l = line.Open())
        {
            a.BeginFigure(new Point(pts[0].X, floor), true, true);
            a.LineTo(pts[0], false, false);
            l.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++)
            {
                double mid = (pts[i - 1].X + pts[i].X) / 2;
                var c1 = new Point(mid, pts[i - 1].Y); var c2 = new Point(mid, pts[i].Y);
                a.BezierTo(c1, c2, pts[i], false, true);
                l.BezierTo(c1, c2, pts[i], true, true);
            }
            a.LineTo(new Point(pts[^1].X, floor), false, false);
        }
        area.Freeze(); line.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(0x61, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(accent), 1.5) { LineJoin = PenLineJoin.Round }, line);

        double seconds = span / 1000;
        if (seconds >= 1) Label(dc, $"{Math.Round(seconds)}s ago", PadL, h - 13, dpi);
        Label(dc, "now", right, h - 13, dpi, alignRight: true);
    }

    static void Label(DrawingContext dc, string text, double x, double y, double dpi, bool alignRight = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Axis, dpi);
        dc.DrawText(ft, new Point(alignRight ? x - ft.Width : x, y));
    }

    /// Axis marks drop the decimal the live reading keeps.
    static string AxisSpeed(double bps)
    {
        double mb = bps / (1024 * 1024);
        return mb >= 1 ? $"{Math.Round(mb)} MB/s" : $"{Math.Round(bps / 1024)} KB/s";
    }

    /// A round number at or above the fastest seen, never below 50 MB/s.
    public static double NiceCeiling(double bps)
    {
        double mb = Math.Max(bps, 50.0 * 1024 * 1024) / (1024 * 1024);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(mb)));
        double step = new[] { 1.0, 2, 5, 10 }.First(x => mb <= x * magnitude);
        return step * magnitude * 1024 * 1024;
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
}
