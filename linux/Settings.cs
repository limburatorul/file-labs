using System.Globalization;

namespace FileExplorer;

// key=value lines in ~/.config/ProtagonistLabs/File Labs/settings.txt: the same keys and format as on Windows
// (%AppData%\ProtagonistLabs\File Labs there), so a setting means the same thing in both.
public static class Settings
{
    // FILELABS_PROFILE=<folder>: a copy for testing beside the installed one, with its own settings
    public static readonly string Profile = Environment.GetEnvironmentVariable("FILELABS_PROFILE") is { Length: > 0 } p ? p : null;
    public static string Dir => Profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs", "File Labs");
    internal static string FilePath => Path.Combine(Dir, "settings.txt");
    static string QuickFile => Path.Combine(Dir, "quickaccess.txt");

    public static string Accent = "#4C8DFF";
    public static double AccentStrength = 1.0;
    public static double RowPad = 3;
    public static bool Animations = true;
    public static bool SinglePane;
    public static bool Preview;
    public static double PreviewWidth = 380;
    public static int Stats = 2;
    public static bool ItemTips = true;
    public static bool InternalEditor = true;
    public static bool Verify;
    public static bool ParallelCopy = true;
    public static int SearchLimit = 10_000;
    public static bool ShowHidden;
    // as on Windows: where copies and moves show ("button": only the Transfers button lights up; "window"; "bottom":
    // cards under the panes), whether that window closes itself, F5 copying, the age badge, sidebar parts left out
    public static string TransfersShow = "button";
    public static bool CloseTransfersWhenDone = true;
    public static bool F5Copies;
    public static bool AgeBadges = true;
    public static readonly HashSet<string> SidebarHidden = new();
    public static bool DeleteSound = true; // the question before a permanent delete
    public static string LastSeenVersion = "";
    public static string BackgroundImage = "";
    public static double ImageOpacity = 40;
    public static double ImageBrightness;
    public static double TextScale = 1;
    public static bool CloseEmptyPane;
    public static string Terminal = ""; // "" = the first one found (Terminal.Find)

    public const int RecentMax = 20, DestinationsMax = 8;
    public static List<string> RecentFolders = new();
    public static List<string> RecentDestinations = new();
    // Quick access lists, as on Windows: quickaccess.txt holds "# Name" headers, each followed by its folders; one list
    // named "Quick access" is written without a header, which is also how a file from before the lists reads.
    public sealed class QuickList { public string Name; public List<string> Folders = new(); }
    public const string FirstQuickList = "Quick access";
    public static List<QuickList> QuickLists = new() { new() { Name = FirstQuickList } };
    public static int ShownQuickList; // the list the sidebar shows
    public static QuickList CurrentQuickList => QuickLists[Math.Clamp(ShownQuickList, 0, QuickLists.Count - 1)];
    public static List<string> QuickAccess => CurrentQuickList.Folders; // Ctrl+D pins to it

    public static List<QuickList> ParseQuickLists(IEnumerable<string> lines)
    {
        var lists = new List<QuickList>();
        foreach (var line in lines.Where(l => l.Trim() != ""))
            if (line.StartsWith("# ") && line[2..].Trim() is { Length: > 0 } name) lists.Add(new QuickList { Name = name });
            else
            {
                if (lists.Count == 0) lists.Add(new QuickList { Name = FirstQuickList });
                if (!lists[^1].Folders.Contains(line)) lists[^1].Folders.Add(line);
            }
        if (lists.Count == 0) lists.Add(new QuickList { Name = FirstQuickList });
        return lists;
    }

    public static IEnumerable<string> QuickListLines(List<QuickList> lists) =>
        lists is [{ Name: FirstQuickList } only] ? only.Folders : lists.SelectMany(q => q.Folders.Prepend("# " + q.Name));

    public static double Split = 0.5;
    public static bool Maximized;
    public static (double X, double Y, double W, double H)? WindowBounds;
    public static double SidebarWidth = 270;
    public static readonly Dictionary<string, List<string>> Tabs = new();
    public static readonly Dictionary<string, int> ActiveTab = new();
    public static readonly Dictionary<string, ViewMode> FolderViews = new(); // folder → its view; absent = Details
    public static readonly Dictionary<string, (string Key, bool Desc)> FolderSorts = new();
    public static readonly HashSet<string> SidebarCollapsed = new();
    public static readonly HashSet<string> DateGroupFolders = new();

    static double D(string v, double min, double max, double fallback) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? Math.Clamp(d, min, max) : fallback;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line[..i], v = line[(i + 1)..];
                    switch (k)
                    {
                        case "accent" when System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"): Accent = v; break;
                        case "accentstrength": AccentStrength = D(v, 0.3, 2.5, AccentStrength); break;
                        case "rowpad": RowPad = D(v, 0, 10, RowPad); break;
                        case "animations": Animations = v == "1"; break;
                        case "singlepane": SinglePane = v == "1"; break;
                        case "preview": Preview = v == "1"; break;
                        case "previewwidth": PreviewWidth = D(v, 200, 1600, PreviewWidth); break;
                        case "stats" when int.TryParse(v, out var st) && st is >= 0 and <= 2: Stats = st; break;
                        case "itemtips": ItemTips = v == "1"; break;
                        case "internaleditor": InternalEditor = v == "1"; break;
                        case "verify": Verify = v == "1"; break;
                        case "parallel": ParallelCopy = v != "0"; break;
                        case "searchlimit" when int.TryParse(v, out var sl) && sl >= 0: SearchLimit = sl; break;
                        case "hidden": ShowHidden = v == "1"; break;
                        case "transfers" when v is "button" or "window" or "bottom": TransfersShow = v; break;
                        case "transfersclose": CloseTransfersWhenDone = v != "0"; break;
                        case "f5copies": F5Copies = v == "1"; break;
                        case "agebadges": AgeBadges = v != "0"; break;
                        case "sidebarhidden": SidebarHidden.Clear(); SidebarHidden.UnionWith(v.Split(',').Where(x => x != "")); break;
                        case "deletesound": DeleteSound = v == "1"; break;
                        case "lastseen": LastSeenVersion = v; break;
                        case "bgimage": BackgroundImage = v; break;
                        case "bgopacity": ImageOpacity = D(v, 5, 100, ImageOpacity); break;
                        case "bgbright": ImageBrightness = D(v, -100, 100, ImageBrightness); break;
                        case "textscale": TextScale = D(v, 1, 1.6, TextScale); break;
                        case "closeemptypane": CloseEmptyPane = v == "1"; break;
                        case "terminal": Terminal = v; break;
                        case "recent": RecentFolders = v.Split('|').Where(x => x != "").Take(RecentMax).ToList(); break;
                        case "destinations": RecentDestinations = v.Split('|').Where(x => x != "").Take(DestinationsMax).ToList(); break;
                        case "split": Split = D(v, 0.15, 0.85, Split); break;
                        case "maximized": Maximized = v == "1"; break;
                        case "window" when v.Split(',') is { Length: 4 } r && r.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)):
                            var n = r.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                            if (n[2] > 200 && n[3] > 150) WindowBounds = (n[0], n[1], n[2], n[3]);
                            break;
                        case "sidebar": SidebarWidth = D(v, 160, 600, SidebarWidth); break;
                        case "quicklist" when int.TryParse(v, out var ql) && ql >= 0: ShownQuickList = ql; break;
                        case "sidebarcollapsed": SidebarCollapsed.Clear(); SidebarCollapsed.UnionWith(v.Split(',').Where(x => x != "")); break;
                        case var t when t.StartsWith("tabs."): Tabs[t[5..]] = v.Split('|').Where(x => x != "").ToList(); break;
                        case var a when a.StartsWith("activetab.") && int.TryParse(v, out var ai): ActiveTab[a[10..]] = ai; break;
                        // folderview=IconsL|/home/me/Photos — the path last, as in sort. The old per-pane view.Left/Right are dropped.
                        case "folderview" when v.Split('|', 2) is [var fm, var fp] && Enum.TryParse<ViewMode>(fm, out var vm) && fp != "": FolderViews[fp] = vm; break;
                        case "sort" when v.Split('|', 3) is [var sk, var sd, var sp] && Entry.SortKeys.Contains(sk) && sp != "": FolderSorts[sp] = (sk, sd == "1"); break;
                        case "groupdate" when v != "": DateGroupFolders.Add(v); break;
                    }
                }
            if (File.Exists(QuickFile)) QuickLists = ParseQuickLists(File.ReadAllLines(QuickFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // unreadable: the defaults
    }

    static IEnumerable<string> Lines()
    {
        static string N(double d) => d.ToString(CultureInfo.InvariantCulture);
        yield return $"accent={Accent}";
        yield return $"accentstrength={N(AccentStrength)}";
        yield return $"rowpad={N(RowPad)}";
        yield return $"animations={(Animations ? 1 : 0)}";
        yield return $"singlepane={(SinglePane ? 1 : 0)}";
        yield return $"preview={(Preview ? 1 : 0)}";
        yield return $"previewwidth={N(PreviewWidth)}";
        yield return $"stats={Stats}";
        yield return $"itemtips={(ItemTips ? 1 : 0)}";
        yield return $"internaleditor={(InternalEditor ? 1 : 0)}";
        yield return $"verify={(Verify ? 1 : 0)}";
        yield return $"parallel={(ParallelCopy ? 1 : 0)}";
        yield return $"searchlimit={SearchLimit}";
        yield return $"hidden={(ShowHidden ? 1 : 0)}";
        yield return $"transfers={TransfersShow}";
        yield return $"transfersclose={(CloseTransfersWhenDone ? 1 : 0)}";
        yield return $"f5copies={(F5Copies ? 1 : 0)}";
        yield return $"agebadges={(AgeBadges ? 1 : 0)}";
        yield return $"sidebarhidden={string.Join(',', SidebarHidden)}";
        yield return $"deletesound={(DeleteSound ? 1 : 0)}";
        yield return $"lastseen={LastSeenVersion}";
        yield return $"bgimage={BackgroundImage}";
        yield return $"bgopacity={N(ImageOpacity)}";
        yield return $"bgbright={N(ImageBrightness)}";
        yield return $"textscale={N(TextScale)}";
        yield return $"closeemptypane={(CloseEmptyPane ? 1 : 0)}";
        yield return $"terminal={Terminal}";
        yield return $"recent={string.Join('|', RecentFolders)}";
        yield return $"destinations={string.Join('|', RecentDestinations)}";
        yield return $"split={N(Split)}";
        yield return $"maximized={(Maximized ? 1 : 0)}";
        if (WindowBounds is { } b) yield return $"window={N(b.X)},{N(b.Y)},{N(b.W)},{N(b.H)}";
        yield return $"sidebar={N(SidebarWidth)}";
        yield return $"quicklist={ShownQuickList}";
        yield return $"sidebarcollapsed={string.Join(',', SidebarCollapsed)}";
        foreach (var (k, v) in Tabs) yield return $"tabs.{k}={string.Join('|', v)}";
        foreach (var (k, v) in ActiveTab) yield return $"activetab.{k}={v}";
        foreach (var (p, v) in FolderViews) yield return $"folderview={v}|{p}";
        foreach (var (p, (k, d)) in FolderSorts) yield return $"sort={k}|{(d ? 1 : 0)}|{p}";
        foreach (var g in DateGroupFolders) yield return $"groupdate={g}";
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            WriteAllLines(FilePath, Lines());
            WriteAllLines(QuickFile, QuickListLines(QuickLists));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void AddRecent(List<string> list, string path, int max)
    {
        list.RemoveAll(x => x == path);
        list.Insert(0, path);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }
}
