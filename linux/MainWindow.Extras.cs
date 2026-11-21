using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using F = FileExplorer.Fmt;

namespace FileExplorer;

// The parts of the window that came after the first Linux build, each as on Windows: the Drop bin, Select by pattern,
// Compare, Open with, the folder tree, and updates for the AppImage.
public partial class MainWindow
{
    // ---- Drop bin: files gathered from anywhere (dropped on it, or Ctrl+B), then copied or moved together into the
    // active pane's folder. Paths only: nothing moves until Copy or Move; the bin lasts for the session. ----
    readonly ObservableCollection<string> bin = new();
    readonly HashSet<string> inBin = new(StringComparer.Ordinal);
    Border binPanel; TextBlock binSummary, binCount; ListBox binList;

    void BuildBin()
    {
        binCount = new TextBlock { FontSize = 11, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (IBrush)Application.Current!.FindResource("Accent") };
        var binButton = new Button { Classes = { "ghost", "icon" }, Width = double.NaN, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = Glyph.Bin, Classes = { "icon" }, FontSize = 16, VerticalAlignment = VerticalAlignment.Center }, binCount } } };
        ToolTip.SetTip(binButton, "Drop bin: collect files from several folders, then copy or move them all at once (Ctrl+B adds the selection)");
        binButton.Click += (_, _) => binPanel.IsVisible = !binPanel.IsVisible;
        BinDrop(binButton);
        DockPanel.SetDock(binButton, Dock.Left);
        BottomRow.Children.Insert(0, binButton);

        binSummary = new TextBlock { Foreground = (IBrush)Application.Current!.FindResource("Muted"), FontSize = 11, Margin = new(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap };
        binList = new ListBox { Classes = { "files" }, MaxHeight = 190, ItemsSource = bin };
        binList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((p, _) =>
        {
            var remove = new Button { Classes = { "ghost" }, Padding = new(3), Content = new TextBlock { Text = Glyph.Close, Classes = { "icon" }, FontSize = 9 } };
            remove.Click += (_, _) => { inBin.Remove(p); bin.Remove(p); ShowBin(); };
            var row = new DockPanel { Children = { remove, new TextBlock { Text = Path.GetFileName(p.TrimEnd('/')), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } };
            DockPanel.SetDock(remove, Dock.Right);
            ToolTip.SetTip(row, p);
            return row;
        }, supportsRecycling: true);
        Button B(string text, bool on, Action run) { var b = new Button { Content = text, Classes = { "job" }, FontSize = 12, Padding = new(10, 4), MinHeight = 28 }; if (on) b.Classes.Add("on"); b.Click += (_, _) => run(); return b; }
        var hide = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = Glyph.Close, Classes = { "icon" }, FontSize = 10 } };
        hide.Click += (_, _) => binPanel.IsVisible = false;
        var clear = new Button { Classes = { "ghost" }, Content = "Clear", FontSize = 11 };
        clear.Click += (_, _) => { bin.Clear(); inBin.Clear(); ShowBin(); };
        var head = new DockPanel { Children = { hide, clear, new TextBlock { Text = "Drop bin", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        DockPanel.SetDock(hide, Dock.Right); DockPanel.SetDock(clear, Dock.Right);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0), Children = { B("Copy here", true, () => EmptyBin(false)), B("Move here", false, () => EmptyBin(true)) } };
        var body = new DockPanel { Children = { head, binSummary, actions, binList } };
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(binSummary, Dock.Top); DockPanel.SetDock(actions, Dock.Bottom);
        binPanel = new Border { IsVisible = false, Margin = new(8, 4, 8, 0), Padding = new(10, 8), CornerRadius = new(8), Background = F.Brush("#0DFFFFFF"), BorderBrush = F.Brush("#1AFFFFFF"), BorderThickness = new(1), Child = body };
        BinDrop(binPanel);
        DockPanel.SetDock(binPanel, Dock.Bottom);
        SidebarPanel.Children.Insert(SidebarPanel.Children.IndexOf(PizzaButton) + 1, binPanel);
        ShowBin();
    }

    void BinDrop(Control c)
    {
        DragDrop.SetAllowDrop(c, true);
        DragDrop.AddDragOverHandler(c, (_, e) => { e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        DragDrop.AddDropHandler(c, (_, e) => { AddToBin(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null) ?? Enumerable.Empty<string>()); e.Handled = true; });
    }

    void AddToBin(IEnumerable<string> paths)
    {
        int before = bin.Count;
        foreach (var p in paths) if (inBin.Add(p)) bin.Add(p);
        binPanel.IsVisible = true;
        ShowBin();
        Say($"{bin.Count - before} added to the Drop bin");
    }

    void ShowBin()
    {
        binCount.Text = bin.Count > 0 ? bin.Count.ToString("N0") : "";
        int folders = bin.Select(p => Path.GetDirectoryName(p.TrimEnd('/'))).Distinct().Count();
        binSummary.Text = bin.Count == 0 ? "Drop files here, or Ctrl+B on a selection" : $"{bin.Count:N0} item(s) from {folders:N0} folder(s)";
    }

    void EmptyBin(bool move)
    {
        var items = bin.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0) { Say("The Drop bin is empty"); return; }
        Submit(items, active.Dir, move);
        if (move) { bin.Clear(); inBin.Clear(); ShowBin(); }
    }

    // ---- Num+ / Num-: select or deselect by pattern, pre-filled with the selected file's extension ----
    async void SelectByPattern(bool select)
    {
        var ext = active.Current is { IsDir: false } cur ? Path.GetExtension(cur.Name) : "";
        var pattern = await Dialogs.Ask(this, select ? "Select" : "Deselect", "Files matching (several: *.jpg;*.png)", ext == "" ? "*" : "*" + ext);
        if (pattern != null) active.SelectMatching(pattern, select);
    }

    // ---- Compare the two panes (Shift+F2): selects, on each side, what is missing on the other or newer than its twin ----
    void ComparePanes()
    {
        if (Settings.SinglePane) { Say("Compare needs two panes (Ctrl+P)"); return; }
        if (Left.InSearch || Right.InSearch) { Say("Compare works on two folders, not on search results"); return; }
        var l = Left.Items.Where(e => !e.IsUp).ToDictionary(e => e.Name, StringComparer.Ordinal);
        var r = Right.Items.Where(e => !e.IsUp).ToDictionary(e => e.Name, StringComparer.Ordinal);
        static bool Differs(Entry a, Dictionary<string, Entry> other)
        {
            if (!other.TryGetValue(a.Name, out var b) || a.IsDir != b.IsDir) return true;
            if (a.IsDir) return false;
            var seconds = (a.Modified - b.Modified).TotalSeconds;
            return seconds > 2 || Math.Abs(seconds) <= 2 && a.Size != b.Size; // 2 s: FAT and some shares round times
        }
        Left.SetSelection(e => !e.IsUp && Differs(e, r));
        Right.SetSelection(e => !e.IsUp && Differs(e, l));
        int nl = Left.Selected.Count, nr = Right.Selected.Count;
        Say(nl + nr == 0 ? "The two folders have the same files" : $"Different: {nl} on the left, {nr} on the right  ·  Ctrl+F5 copies the selection across");
    }

    // ---- Open with: the apps the desktop lists for the file's type, and making one the default ----
    sealed record App(string Id, string Name, string Exec);

    // The file's type the way the desktop decides it: shared-mime-info's globs2 (weight:type:pattern) by the name, the
    // heaviest match winning; `file --mime-type` on its first bytes when the name says nothing.
    static Dictionary<string, (int Weight, string Mime)> globs;
    static Dictionary<string, List<string>> parents;
    static IEnumerable<string> MimeDirs() =>
        new[] { Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(PaneView.Home, ".local", "share") }
            .Concat((Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share").Split(':'))
            .Select(p => Path.Combine(p, "mime")).Where(Directory.Exists);

    static void LoadMime()
    {
        if (globs != null) return;
        globs = new(StringComparer.OrdinalIgnoreCase); parents = new();
        foreach (var dir in MimeDirs())
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "globs2")))
                    foreach (var line in File.ReadLines(Path.Combine(dir, "globs2")))
                        if (line.Split(':') is [var w, var mime, var glob, ..] && int.TryParse(w, out var weight) && glob.StartsWith("*.") && glob.IndexOfAny(new[] { '*', '?', '[' }, 2) < 0
                            && (!globs.TryGetValue(glob[1..], out var had) || had.Weight < weight))
                            globs[glob[1..]] = (weight, mime);
                if (File.Exists(Path.Combine(dir, "subclasses")))
                    foreach (var line in File.ReadLines(Path.Combine(dir, "subclasses")))
                        if (line.Split(' ') is [var child, var parent]) (parents.TryGetValue(child, out var l) ? l : parents[child] = new()).Add(parent);
            }
            catch (IOException) { }
        }
    }

    static string MimeOf(string path)
    {
        LoadMime();
        var name = Path.GetFileName(path);
        for (int i = name.IndexOf('.'); i >= 0; i = name.IndexOf('.', i + 1)) // ".tar.gz" before ".gz"
            if (globs.TryGetValue(name[i..], out var m)) return m.Mime;
        try
        {
            var p = Process.Start(new ProcessStartInfo("file", new[] { "--brief", "--mime-type", path }) { RedirectStandardOutput = true, UseShellExecute = false });
            var s = p?.StandardOutput.ReadToEnd().Trim(); p?.WaitForExit(3000);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// The type and those it is a kind of (text/markdown is a text/plain), nearest first.
    static List<string> Lineage(string mime)
    {
        LoadMime();
        var all = new List<string> { mime };
        for (int i = 0; i < all.Count && i < 8; i++)
            foreach (var p in parents.GetValueOrDefault(all[i]) ?? new()) if (!all.Contains(p)) all.Add(p);
        if (mime.StartsWith("text/") && !all.Contains("text/plain")) all.Add("text/plain");
        return all;
    }

    static IEnumerable<string> AppDirs()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(PaneView.Home, ".local", "share");
        var sys = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share").Split(':');
        return new[] { home }.Concat(sys).Concat(new[] { "/var/lib/flatpak/exports/share", Path.Combine(home, "flatpak", "exports", "share") })
                             .Select(p => Path.Combine(p, "applications")).Where(Directory.Exists).Distinct();
    }

    /// The apps whose .desktop file lists this type (or its family, "image/*"), the default first.
    static List<App> AppsFor(string mime)
    {
        var found = new Dictionary<string, App>();
        var family = mime.Split('/')[0] + "/*";
        var kinds = Lineage(mime).Append(family).ToHashSet();
        foreach (var dir in AppDirs())
            foreach (var f in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories))
            {
                var id = Path.GetRelativePath(dir, f).Replace('/', '-');
                if (found.ContainsKey(id)) continue; // the user's own copy comes first and wins
                string name = null, exec = null, types = ""; bool hidden = false, inEntry = false;
                try
                {
                    foreach (var line in File.ReadLines(f))
                    {
                        if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
                        if (!inEntry) continue;
                        if (line.StartsWith("Name=") && name == null) name = line[5..];
                        else if (line.StartsWith("Exec=")) exec = line[5..];
                        else if (line.StartsWith("MimeType=")) types = line[9..];
                        else if (line is "NoDisplay=true" or "Hidden=true") hidden = true;
                    }
                }
                catch (IOException) { continue; }
                found[id] = hidden || exec == null || name == null ? null : new App(id, name, exec);
                if (found[id] != null && !types.Split(';').Any(kinds.Contains)) found[id] = null;
            }
        var def = DefaultApp(mime);
        return found.Values.Where(a => a != null).OrderBy(a => a.Id != def).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string DefaultApp(string mime)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("xdg-mime", new[] { "query", "default", mime }) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var s = p?.StandardOutput.ReadToEnd().Trim(); p?.WaitForExit(3000);
            return s;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// The app's Exec line with the file in place of its field codes (%f %F %u %U), the rest dropped, as the spec says.
    static void Launch(App app, IEnumerable<string> paths)
    {
        var args = Tokens(app.Exec).ToList();
        if (args.Count == 0) return;
        var files = paths.ToList();
        var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false };
        bool placed = false;
        foreach (var a in args.Skip(1))
        {
            if (a is "%f" or "%u") { psi.ArgumentList.Add(files[0]); placed = true; }
            else if (a is "%F" or "%U") { foreach (var f in files) psi.ArgumentList.Add(f); placed = true; }
            else if (a.Length == 2 && a[0] == '%') continue; // %i %c %k and the old ones
            else psi.ArgumentList.Add(a.Replace("%%", "%"));
        }
        if (!placed) foreach (var f in files) psi.ArgumentList.Add(f);
        try { Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"{app.Name} didn't start: {ex.Message}"); }
    }

    static IEnumerable<string> Tokens(string exec)
    {
        var sb = new System.Text.StringBuilder(); bool quoted = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == '\\' && quoted && i + 1 < exec.Length) { sb.Append(exec[++i]); continue; }
            if (c == ' ' && !quoted) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    MenuItem OpenWithMenu(List<Entry> sel)
    {
        var menu = new MenuItem { Header = "Open with", Icon = new TextBlock { Text = Glyph.OpenWith, Classes = { "icon" }, FontSize = 14 } };
        var mime = MimeOf(sel[0].Path);
        var apps = mime == null ? new() : AppsFor(mime);
        var def = mime == null ? null : DefaultApp(mime);
        foreach (var a in apps.Take(20))
        {
            var app = a;
            var mi = new MenuItem { Header = app.Name + (app.Id == def ? "  (default)" : "") };
            mi.Click += (_, _) => Launch(app, sel.Select(x => x.Path));
            menu.Items.Add(mi);
        }
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = mime == null ? "The type isn't known" : $"No app lists {mime}", IsEnabled = false });
        if (apps.Count > 1 && mime != null)
        {
            menu.Items.Add(new Separator());
            var setDefault = new MenuItem { Header = $"Always open {mime} with" };
            foreach (var a in apps.Where(a => a.Id != def).Take(20))
            {
                var app = a;
                var mi = new MenuItem { Header = app.Name };
                mi.Click += (_, _) => { Shell.Start("xdg-mime", "default", app.Id, mime); Say($"{mime} opens with {app.Name} from now on"); };
                setDefault.Items.Add(mi);
            }
            menu.Items.Add(setDefault);
        }
        return menu;
    }

    // ---- the folder tree: the active pane's folder in its place, from Home and from /, opened as far as needed ----
    TreeView tree;
    Control BuildTree()
    {
        tree = new TreeView { Background = Brushes.Transparent, MaxHeight = 420 };
        foreach (var (name, path) in new[] { ("Home", PaneView.Home), ("File System", "/") })
            tree.Items.Add(TreeNode(name, path));
        tree.SelectionChanged += (_, _) => { if (tree.SelectedItem is TreeViewItem { Tag: string p } && p != active.Dir && !syncing) active.Navigate(p); };
        return tree;
    }

    TreeViewItem TreeNode(string name, string path)
    {
        var item = new TreeViewItem
        {
            Tag = path, Padding = new(2, 1),
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = Glyph.Folder, Classes = { "icon" }, FontSize = 13, Foreground = F.Brush("#F5C451"), Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        if (HasSubfolders(path)) item.Items.Add(new TreeViewItem { Header = "…" }); // so it shows an arrow; filled on opening
        item.PropertyChanged += (_, e) => { if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded) Fill(item); };
        return item;
    }

    void Fill(TreeViewItem item)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
        item.Items.Clear();
        foreach (var d in Subfolders((string)item.Tag!)) item.Items.Add(TreeNode(Path.GetFileName(d), d));
    }

    static IEnumerable<string> Subfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(d => (Settings.ShowHidden || !d.Name.StartsWith('.')) && !(path == "/" && d.Name is "proc" or "sys" or "dev" or "run"))
                .Select(d => d.FullName).OrderBy(d => d, NaturalOrder.Instance).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }
    static bool HasSubfolders(string path) => Subfolders(path).Any();

    bool syncing;
    /// Opens the tree down to the folder shown, from Home when it is inside Home.
    void SyncTree(string dir, bool reveal = true)
    {
        if (tree == null || !tree.IsEffectivelyVisible) return;
        syncing = true;
        try
        {
            var home = PaneView.Home;
            var root = tree.Items.Cast<TreeViewItem>().First(i => (string)i.Tag! == (dir == home || dir.StartsWith(home + "/") ? home : "/"));
            var node = root;
            var start = (string)root.Tag!;
            if (dir != start)
                foreach (var part in dir[(start == "/" ? 1 : start.Length + 1)..].Split('/'))
                {
                    node.IsExpanded = true; Fill(node);
                    var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(i => Path.GetFileName((string)i.Tag!) == part);
                    if (next == null) break;
                    node = next;
                }
            tree.SelectedItem = node;
            if (reveal) node.BringIntoView(); // a rebuilt sidebar stays where it was scrolled
        }
        finally { syncing = false; }
    }

    // ---- updates: the AppImage replaces itself from the latest GitHub release, checked against the SHA-256 GitHub
    // publishes, after asking (the update button lights up). Only an AppImage updates; a tarball shows the release page. ----
    const string Releases = "https://api.github.com/repos/limburatorul/file-labs/releases/latest";
    (Version Version, string Url, string Sha, long Size)? update;
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };

    void StartUpdates()
    {
        UpdateButton.Click += async (_, _) =>
        {
            if (update == null) { await CheckUpdate(manual: true); return; }
            await InstallUpdate();
        };
        updateTimer.Tick += async (_, _) => await CheckUpdate(manual: false);
        updateTimer.Start();
        DispatcherTimer.RunOnce(async () => await CheckUpdate(manual: false), TimeSpan.FromSeconds(5));
    }

    async Task CheckUpdate(bool manual)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FileLabs-Linux");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Releases));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v');
            if (!System.Version.TryParse(tag, out var latest)) return;
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString()!.EndsWith("-x86_64.AppImage"));
            var mine = System.Version.Parse(Version.Split('-')[0]);
            if (latest <= mine || asset.ValueKind != JsonValueKind.Object) { if (manual) Say(latest <= mine ? $"File Labs {Version} is the latest" : $"{latest} has no Linux build yet"); return; }
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString()?.Replace("sha256:", "") : null;
            update = (latest, asset.GetProperty("browser_download_url").GetString(), digest, asset.GetProperty("size").GetInt64());
            UpdateButton.Foreground = (IBrush)Application.Current!.FindResource("Accent");
            ToolTip.SetTip(UpdateButton, $"File Labs {latest} is out: click to update");
            Say($"File Labs {latest} is out · the update button installs it");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        { if (manual) Say("Couldn't check for updates: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("libc")] static extern int fcntl(int fd, int cmd, int arg);

    async Task InstallUpdate()
    {
        var me = Environment.GetEnvironmentVariable("APPIMAGE");
        if (me == null || update is not { } u) { Shell.Open("https://github.com/limburatorul/file-labs/releases/latest"); return; }
        if (!await Dialogs.Confirm(this, "Update", $"Update File Labs to {u.Version}? It restarts when done.", "Update")) return;
        var tmp = me + ".new";
        try
        {
            Say($"Downloading {u.Version}…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var s = await http.GetStreamAsync(u.Url))
            using (var f = File.Create(tmp)) await s.CopyToAsync(f);
            var got = new FileInfo(tmp).Length;
            string sha; using (var f = File.OpenRead(tmp)) sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
            if (got != u.Size || u.Sha != null && sha != u.Sha) { File.Delete(tmp); Say("The download didn't match what GitHub published: not installed"); return; }
            File.SetUnixFileMode(tmp, File.GetUnixFileMode(me));
            File.Move(tmp, me, overwrite: true);
            SaveState();
            // the AppImage runtime leaves us its keep-alive pipe and a descriptor on its mount without close-on-exec: the
            // restarted copy would inherit them and keep the old image mounted (and its runtime running) while it lives.
            // .NET's own descriptors are close-on-exec already, so marking every one above stderr changes nothing else.
            foreach (var fd in Directory.GetFileSystemEntries("/proc/self/fd")) // not GetFiles: it skips links to folders
                if (int.TryParse(Path.GetFileName(fd), out var n) && n > 2) fcntl(n, 2 /* F_SETFD */, 1 /* FD_CLOEXEC */);
            Process.Start(new ProcessStartInfo(me) { UseShellExecute = false });
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        { try { File.Delete(tmp); } catch (IOException) { } Say("Update not installed: " + ex.Message); }
    }
}
