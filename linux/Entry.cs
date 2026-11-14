using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FileExplorer;

public enum ViewMode { Details, List, IconsM, IconsL, IconsXL }

/// One row of a pane: the same model as on Windows (PaneView.xaml.cs there), with Avalonia brushes.
public class Entry : INotifyPropertyChanged
{
    public string Name { get; init; }
    public string Path { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public bool Hidden => Name.StartsWith('.') && !IsUp;
    DateTime modified;
    public DateTime Modified
    {
        get => modified;
        set { modified = value; Changed(nameof(Modified)); Changed(nameof(DateText)); Changed(nameof(Age)); Changed(nameof(AgeBrush)); Changed(nameof(AgeTextBrush)); Changed(nameof(Tip)); }
    }
    public bool IsUp => Name == "..";
    /// A group's heading row (Group by date), not a file: it is never selected, counted or opened.
    public string Header { get; init; }
    public bool IsHeader => Header != null;
    public bool IsFile => Header == null;
    public int HeaderCount { get; init; }

    /// Group by date (Details): Explorer's groups, newest first.
    internal static readonly string[] DateGroups =
        { "Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Last month", "Earlier this year", "A long time ago" };
    internal static int DateGroupOf(DateTime t, DateTime now)
    {
        var day = now.Date;
        if (t >= day) return 0;
        if (t >= day.AddDays(-1)) return 1;
        int back = ((int)day.DayOfWeek - (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var week = day.AddDays(-back);
        if (t >= week) return 2;
        if (t >= week.AddDays(-7)) return 3;
        var month = new DateTime(day.Year, day.Month, 1);
        if (t >= month) return 4;
        if (t >= month.AddMonths(-1)) return 5;
        return t >= new DateTime(day.Year, 1, 1) ? 6 : 7;
    }
    public string Location { get; init; } = ""; // search results: where it was found, relative to where the search started
    public string Where => Location == "" ? "" : "   in " + Location;
    public string FolderTarget => IsDir ? Path : null;

    long size = -1; // -1 = folder size still computing, -2 = not measured
    public long Size { get => size; set { size = value; Changed(nameof(Size)); Changed(nameof(SizeText)); Changed(nameof(Tip)); } }
    Bitmap thumb;
    public Bitmap Thumb { get => thumb; set { thumb = value; Changed(nameof(Thumb)); Changed(nameof(NoThumb)); } }
    public bool NoThumb => thumb == null;
    public int ThumbSize;
    bool renaming;
    public bool Renaming { get => renaming; set { renaming = value; Changed(nameof(Renaming)); Changed(nameof(NotRenaming)); } }
    public bool NotRenaming => !renaming;
    bool isCut;
    public bool IsCut { get => isCut; set { if (isCut != value) { isCut = value; Changed(nameof(IsCut)); Changed(nameof(IconOpacity)); } } }
    public double IconOpacity => isCut ? 0.4 : 1;
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string SizeText => IsUp || size == -2 ? "" : size < 0 ? "…" : Fmt.Size(size);
    bool NoDate => IsUp || Modified == default;

    public event PropertyChangedEventHandler PropertyChanged;
    void Changed(string p) => PropertyChanged?.Invoke(this, new(p));

    // the same kinds and colours as on Windows
    static readonly Dictionary<string, (string, string)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = (Glyph.Image, "#F472B6"), [".jpeg"] = (Glyph.Image, "#F472B6"), [".png"] = (Glyph.Image, "#F472B6"),
        [".gif"] = (Glyph.Image, "#F472B6"), [".webp"] = (Glyph.Image, "#F472B6"), [".svg"] = (Glyph.Image, "#F472B6"),
        [".bmp"] = (Glyph.Image, "#F472B6"), [".heic"] = (Glyph.Image, "#F472B6"),
        [".mp4"] = (Glyph.Video, "#FB923C"), [".mkv"] = (Glyph.Video, "#FB923C"), [".avi"] = (Glyph.Video, "#FB923C"),
        [".mov"] = (Glyph.Video, "#FB923C"), [".webm"] = (Glyph.Video, "#FB923C"),
        [".mp3"] = (Glyph.Music, "#A78BFA"), [".flac"] = (Glyph.Music, "#A78BFA"), [".wav"] = (Glyph.Music, "#A78BFA"),
        [".ogg"] = (Glyph.Music, "#A78BFA"), [".opus"] = (Glyph.Music, "#A78BFA"), [".m4a"] = (Glyph.Music, "#A78BFA"),
        [".zip"] = (Glyph.Archive, "#FACC15"), [".rar"] = (Glyph.Archive, "#FACC15"), [".7z"] = (Glyph.Archive, "#FACC15"),
        [".iso"] = (Glyph.Archive, "#FACC15"), [".tar"] = (Glyph.Archive, "#FACC15"), [".gz"] = (Glyph.Archive, "#FACC15"),
        [".xz"] = (Glyph.Archive, "#FACC15"), [".zst"] = (Glyph.Archive, "#FACC15"), [".bz2"] = (Glyph.Archive, "#FACC15"),
        [".appimage"] = (Glyph.Program, "#34D399"), [".sh"] = (Glyph.Program, "#34D399"), [".run"] = (Glyph.Program, "#34D399"),
        [".exe"] = (Glyph.Program, "#34D399"), [".desktop"] = (Glyph.Program, "#34D399"), [".deb"] = (Glyph.Program, "#34D399"),
        [".rpm"] = (Glyph.Program, "#34D399"), [".flatpakref"] = (Glyph.Program, "#34D399"),
        [".cs"] = (Glyph.Code, "#38BDF8"), [".js"] = (Glyph.Code, "#38BDF8"), [".ts"] = (Glyph.Code, "#38BDF8"), [".py"] = (Glyph.Code, "#38BDF8"),
        [".json"] = (Glyph.Code, "#38BDF8"), [".xml"] = (Glyph.Code, "#38BDF8"), [".html"] = (Glyph.Code, "#38BDF8"), [".xaml"] = (Glyph.Code, "#38BDF8"),
        [".axaml"] = (Glyph.Code, "#38BDF8"), [".c"] = (Glyph.Code, "#38BDF8"), [".cpp"] = (Glyph.Code, "#38BDF8"), [".h"] = (Glyph.Code, "#38BDF8"),
        [".rs"] = (Glyph.Code, "#38BDF8"), [".go"] = (Glyph.Code, "#38BDF8"), [".toml"] = (Glyph.Code, "#38BDF8"), [".yaml"] = (Glyph.Code, "#38BDF8"),
        [".yml"] = (Glyph.Code, "#38BDF8"), [".conf"] = (Glyph.Code, "#38BDF8"), [".ini"] = (Glyph.Code, "#38BDF8"), [".css"] = (Glyph.Code, "#38BDF8"),
        [".pdf"] = (Glyph.Pdf, "#F87171"),
    };
    (string icon, string color) Kind => IsUp ? (Glyph.Up, "#9CA3AF") : IsDir ? (Glyph.Folder, "#60A5FA")
        : Kinds.TryGetValue(System.IO.Path.GetExtension(Name), out var k) ? k : (Glyph.Document, "#9CA3AF");
    public string Icon => Kind.icon;
    public string KindColor => Kind.color;
    public IBrush IconBrush => Fmt.Brush(Kind.color);

    // Recency at a glance: hot red for minutes, through orange, yellow and teal to grey with age (log time).
    static readonly (double hours, Color c)[] Stops =
    {
        (0, Color.FromRgb(0xFF, 0x4D, 0x5E)), (1, Color.FromRgb(0xFF, 0x8A, 0x3D)),
        (24, Color.FromRgb(0xFF, 0xD2, 0x3F)), (24 * 7, Color.FromRgb(0x3D, 0xDC, 0xC8)),
        (24 * 30, Color.FromRgb(0x5B, 0x8D, 0xB8)), (24 * 365, Color.FromRgb(0x4A, 0x4A, 0x4A)),
    };
    Color AgeColor
    {
        get
        {
            double h = Math.Max(0, (DateTime.Now - Modified).TotalHours), x = Math.Log(h + 1);
            for (int i = 1; i < Stops.Length; i++)
            {
                double a = Math.Log(Stops[i - 1].hours + 1), b = Math.Log(Stops[i].hours + 1);
                if (x > b) continue;
                double t = (x - a) / (b - a);
                Color p = Stops[i - 1].c, q = Stops[i].c;
                return Color.FromRgb((byte)(p.R + (q.R - p.R) * t), (byte)(p.G + (q.G - p.G) * t), (byte)(p.B + (q.B - p.B) * t));
            }
            return Stops[^1].c;
        }
    }
    public IBrush AgeBrush => NoDate ? Brushes.Transparent : new SolidColorBrush(AgeColor);
    public IBrush AgeTextBrush { get { var c = AgeColor; return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140 ? Fmt.Brush("#E6111111") : Fmt.Brush("#E6FFFFFF"); } }
    public string Age
    {
        get
        {
            if (NoDate) return "";
            var d = DateTime.Now - Modified;
            return d.TotalHours < 1 ? $"{Math.Max(0, (int)d.TotalMinutes)}'"
                : d.TotalDays < 1 ? $"{(int)d.TotalHours}h {d.Minutes}'"
                : d.TotalDays < 60 ? $"{(int)d.TotalDays}d"
                : d.TotalDays < 730 ? $"{(int)(d.TotalDays / 30)}mo"
                : $"{(int)(d.TotalDays / 365)}y";
        }
    }
    public string DateText => NoDate ? "" : Modified.ToString("yyyy-MM-dd  HH:mm");

    public string Tip => IsUp || IsHeader ? null : string.Join("\n", new[]
    {
        Name, Location != "" ? "In: " + Location : null, "Type: " + Fmt.TypeName(Name, IsDir),
        size >= 0 ? "Size: " + Fmt.Size(size) : null, NoDate ? null : "Date modified: " + Modified.ToString("yyyy-MM-dd HH:mm:ss"),
    }.Where(line => line != null));

    public static readonly string[] SortKeys = { "Name", "Size", "Modified", "Type" };
}

/// Names the way people read them: "file2" before "file10", case not deciding first.
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();
    public int Compare(string a, string b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                int c = string.CompareOrdinal(x, y);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (d != 0) return d;
            i++; j++;
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }
}

/// Folders first, ".." always on top, then by the chosen column.
sealed class EntrySort(string key, bool desc) : IComparer<Entry>
{
    public int Compare(Entry x, Entry y)
    {
        if (x.IsUp != y.IsUp) return x.IsUp ? -1 : 1;
        if (x.IsDir != y.IsDir) return x.IsDir ? -1 : 1;
        int c = key switch
        {
            "Size" => x.Size.CompareTo(y.Size),
            "Modified" => x.Modified.CompareTo(y.Modified),
            "Type" => string.Compare(x.Ext, y.Ext, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
        if (c == 0) c = NaturalOrder.Instance.Compare(x.Name, y.Name);
        return desc ? -c : c;
    }
}

static class Fmt
{
    public static string Size(long b)
    {
        string[] u = { "bytes", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} bytes" : $"{v:0.#} {u[i]}";
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IBrush> brushes = new();
    public static IBrush Brush(string hex) => brushes.GetOrAdd(hex, h => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(h)));

    public static string TypeName(string name, bool dir)
    {
        if (dir) return "Folder";
        var ext = System.IO.Path.GetExtension(name).TrimStart('.');
        return ext == "" ? "File" : ext.ToUpperInvariant() + " file";
    }
}
