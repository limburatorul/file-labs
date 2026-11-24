using System.Windows;
using System.Windows.Media;

namespace FileExplorer;

/// Themes, as in Shelf: File Labs is drawn for a dark ground, and every colour, corner and the font of its windows is a
/// key (App.xaml, "Theme keys") holding that dark look's value. A theme maps each of those values to its own: the
/// white veils (hovers, lines, cards) become its ink, the light text greys its text, the dark grounds its surface;
/// vivid colours (file kinds, warnings) stay, a little darker on a light ground. Code that builds its own colours goes
/// through MainWindow.Hex, which maps the same way. Switched live from Settings → Appearance.
public sealed record Theme(
    string Id, string Name, bool Light,
    Color Surface, Color Popup, Color Fg, Color FgDim, Color Ink, double InkGain, Color Shade, double ShadeGain,
    string Accent, string Font, double Radius, bool Solid, double Vivid = 1)
{
    /// Keys this theme sets to a brush of its own rather than a mapped colour (a title bar's gradient, say).
    public Dictionary<string, Func<Brush>> Brushes { get; init; } = new();
    /// The file lists' ground, where it isn't the window's (the white lists of the classic systems).
    public Color? Field { get; init; }
    /// Every grey edge drawn with this instead (Windows 98's raised bevel).
    public Func<Brush> Edge { get; init; }
    /// The tab strip is dark (a title bar's blue): what sits on it keeps the dark look's light colours.
    public bool DarkStrip { get; init; }
    /// The active tab carries an accent line along its top (off in macOS 27, which marks it by its text).
    public bool TabLine { get; init; } = true;
}

public static class Themes
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static LinearGradientBrush Down(params string[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i < stops.Length; i++) b.GradientStops.Add(new GradientStop(C(stops[i]), i / (double)Math.Max(1, stops.Length - 1)));
        b.Freeze();
        return b;
    }

    public static readonly Theme[] All =
    {
        new("default", "File Labs", false, default, default, default, default, default, 1, default, 1, null, "Segoe UI Variable Text, Segoe UI", 1, false),
        // grey 3D chrome, navy selection, Tahoma's older sibling, square corners
        new("win98", "Windows 98", true, C("#C0C0C0"), C("#C0C0C0"), C("#000000"), C("#4A4A4A"), C("#000000"), 1.5, C("#000000"), 0.35,
            "#000080", "Microsoft Sans Serif, Tahoma", 0, true, 0.7)
        {
            Brushes = { ["TabStripBg"] = () => new LinearGradientBrush(C("#000080"), C("#1084D0"), 0) },
            Field = C("#FFFFFF"), DarkStrip = true,
            // light above and left, dark below and right: (0,0)→(1,1) splits a box along its other diagonal
            Edge = () => new LinearGradientBrush(new GradientStopCollection { new(C("#FFFFFF"), 0), new(C("#FFFFFF"), 0.5), new(C("#808080"), 0.5), new(C("#808080"), 1) }, new Point(0, 0), new Point(1, 1)),
        },
        // Luna: the cream ground, the blue task pane down the side, rounded blue selection
        new("xp", "Windows XP", true, C("#ECE9D8"), C("#FFFFFF"), C("#000000"), C("#5A5A5A"), C("#0A246A"), 1.2, C("#3A6EA5"), 0.9,
            "#316AC5", "Tahoma", 0.5, true, 0.75)
        {
            Brushes = { ["TabStripBg"] = () => Down("#0054E3", "#2C72E8", "#0A49C6"), ["SidebarBg"] = () => Down("#7BA2E7", "#6375D6") },
            Field = C("#FFFFFF"), DarkStrip = true, Edge = () => new SolidColorBrush(C("#7F9DB9")),
        },
        // Mac OS X 10.5: the grey unified toolbar, the blue-grey source list, Lucida
        new("leopard", "Mac OS X 10.5", true, C("#E8E8E8"), C("#F6F6F6"), C("#000000"), C("#6E6E6E"), C("#000000"), 1.1, C("#5A6E8C"), 0.55,
            "#3875D7", "Lucida Sans Unicode, Segoe UI", 0.7, true, 0.75)
        {
            Brushes = { ["TabStripBg"] = () => Down("#D3D3D3", "#A8A8A8"), ["SidebarBg"] = () => Down("#DDE4EB", "#D1DBE5") },
            Field = C("#FFFFFF"), Edge = () => new SolidColorBrush(C("#A5A5A5")),
        },
        // macOS 27: Liquid Glass, light and see-through, large corners
        new("macos27", "macOS 27", true, C("#F5F5F7"), C("#FAFAFC"), C("#1D1D1F"), C("#6E6E73"), C("#000000"), 0.85, C("#FFFFFF"), 1.2,
            "#0071E3", "Segoe UI Variable Display, Segoe UI", 1.5, false, 0.8) { TabLine = false },
    };

    public static Theme Current { get; private set; } = All[0];

    /// The dark look's value of every key, taken the first time a theme is applied.
    static Dictionary<string, object> originals;

    public static void Apply(string id)
    {
        Current = All.FirstOrDefault(t => t.Id == id) ?? All[0];
        var res = Application.Current.Resources;
        originals ??= res.Keys.OfType<string>()
            .Where(k => res[k] is CornerRadius || k == "UiFont" || res[k] is SolidColorBrush && !k.StartsWith("Accent"))
            .ToDictionary(k => k, k => res[k]);
        foreach (var (key, value) in originals)
        {
            if (Current.Brushes.TryGetValue(key, out var make)) { var b = make(); if (b.CanFreeze) b.Freeze(); res[key] = b; }
            else if (Current.Edge != null && key[0] == 'e' && value is SolidColorBrush { Color: { R: > 200, G: > 200, B: > 200 } }) { var b = Current.Edge(); b.Freeze(); res[key] = b; }
            else if (value is SolidColorBrush brush) res[key] = Frozen(Map(brush.Color));
            else if (value is CornerRadius r) res[key] = new CornerRadius(r.TopLeft * Current.Radius, r.TopRight * Current.Radius, r.BottomRight * Current.Radius, r.BottomLeft * Current.Radius);
            else if (key == "UiFont") res[key] = new FontFamily(Current.Font);
        }
        foreach (var (key, make) in Current.Brushes.Where(b => !originals.ContainsKey(b.Key))) res[key] = make();

    }

    // what sits on the tab strip and beside it: inactive tabs, the + button, the window buttons, their hovers
    static readonly string[] StripKeys = { "cD0FFFFFF", "cFFB4BECC", "c2EFFFFFF", "c0DFFFFFF", "c18FFFFFF", "cFF8A97AA" };

    /// On a dark strip, what sits on it keeps the dark look's colours; elsewhere the theme's.
    public static void Strip(FrameworkElement host)
    {
        foreach (var key in StripKeys)
            if (Current.DarkStrip && originals != null && originals.TryGetValue(key, out var v)) host.Resources[key] = v;
            else host.Resources.Remove(key);
    }

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// A colour of the dark look, as the current theme has it.
    public static Color Map(Color c)
    {
        var t = Current;
        if (!t.Light) return c;
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        double sat = max == 0 ? 0 : (max - min) / max, l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        if (sat > 0.3 && max > 0.4) return Color.FromArgb(c.A, (byte)(c.R * t.Vivid), (byte)(c.G * t.Vivid), (byte)(c.B * t.Vivid));
        if (l >= 0.7 && c.A < 0x60) return With(t.Ink, c.A * t.InkGain);           // white veils: hovers, lines, cards
        double alpha = c.A;
        if (l >= 0.7 && c.A < 0xC8) { l = 0.55 + 0.37 * (c.A - 0x60) / (double)(0xC8 - 0x60); alpha = 255; } // half-white text: dim to full by its strength, opaque
        if (l >= 0.4)                                                                // text, from the dim greys to the brightest
        {
            double k = Math.Clamp((l - 0.55) / 0.37, 0, 1);
            return With(Color.FromRgb((byte)(t.FgDim.R + (t.Fg.R - t.FgDim.R) * k), (byte)(t.FgDim.G + (t.Fg.G - t.FgDim.G) * k), (byte)(t.FgDim.B + (t.Fg.B - t.FgDim.B) * k)), alpha);
        }
        if (max < 0.04) return With(t.Shade, c.A * t.ShadeGain);                     // black veils: sidebar, tab strip, shades
        return With(c.A >= 0xE0 ? t.Popup : t.Surface, t.Solid && c.A >= 0x80 ? 255 : c.A); // the dark grounds
    }

    static Color With(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(Math.Round(alpha), 0, 255), c.R, c.G, c.B);
}
