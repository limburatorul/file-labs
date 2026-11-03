using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileExplorer;

// Quick View (Space). Up/Down browse the list while open, Space/Esc close.
// Media: click or Enter = play/pause, Left/Right = seek 5 s.
public partial class MainWindow
{
    Window quick;
    int quickVersion; // bumps on every selection change so slow loads (PDF) don't land on the wrong file
    MediaElement media;
    readonly DispatcherTimer mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> ImageExt = Set(".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic");
    static readonly HashSet<string> VideoExt = Set(".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".ts");
    static readonly HashSet<string> AudioExt = Set(".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus");
    static readonly HashSet<string> ZipExt = Set(".zip", ".jar", ".nupkg", ".apk", ".vsix", ".whl");
    static readonly HashSet<string> FontExt = Set(".ttf", ".otf");
    // text formats better seen rendered than as source, when a preview handler exists for them
    static readonly HashSet<string> RenderedExt = Set(".html", ".htm", ".mht", ".mhtml", ".svg", ".rtf", ".md");

    // Windows' preview handler for the type (Office, HTML, mail, …), or null when there is none.
    UIElement Handler(Entry e, int v)
    {
        var host = PreviewHost.For(e.Path);
        if (host != null) host.Failed += msg => { if (v == quickVersion) QuickTarget.Content = Summary(e, msg); };
        return host;
    }

    // Where a preview goes: the Quick View window while it is open, otherwise the preview pane (Alt+P).
    ContentControl QuickTarget => quick != null ? quick : PreviewBody;
    void QuickTitle(string text, bool append = false)
    {
        if (quick != null) quick.Title = append ? quick.Title + text : text;
        else PreviewTitle.Text = append ? PreviewTitle.Text + text : text;
    }

    // ---- Preview pane: the active pane's selected file beside the panes, as in Explorer (Alt+P) ----
    readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    string previewShown; // the file on show, so the many StatsChanged that aren't a new selection cost nothing

    void ApplyPreview()
    {
        var on = Settings.Preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = PreviewSplitter.Visibility = on;
        PreviewGap.Width = new GridLength(Settings.Preview ? 10 : 0);
        PreviewColumn.Width = new GridLength(Settings.Preview ? Settings.PreviewWidth : 0);
        PreviewColumn.MinWidth = Settings.Preview ? 200 : 0;
        FitPreview();
        PlaceCaption();
        previewShown = null;
        if (Settings.Preview) PreviewFollow();
        else if (quick == null) { StopMedia(); quickVersion++; PreviewBody.Content = null; } // a video in it stops with it
    }

    void TogglePreview()
    {
        Settings.Preview = !Settings.Preview;
        Settings.Save();
        ApplyPreview();
        if (Settings.Preview) Motion.Enter(PreviewPane); // a fade, not a growing column: that would relayout the panes every frame
    }

    void Preview_Click(object s, RoutedEventArgs e) { TogglePreview(); active.FocusList(); }

    /// The preview gets what the panes leave (250 each and 10 between, or one pane's 250), never more. Its splitter
    /// only resizes the preview's own column and knew no limit: dragged to the window's left edge it made the column
    /// 8,267 px wide in a 1,500 px window, so the picture, centred in it, ran off to the right and out of sight.
    void FitPreview()
    {
        double panes = Settings.SinglePane ? 250 : 510;
        PreviewColumn.MaxWidth = Settings.Preview ? Math.Max(PreviewColumn.MinWidth, PaneArea.ActualWidth - 10 - panes) : double.PositiveInfinity;
    }

    void PaneArea_SizeChanged(object s, SizeChangedEventArgs e) => FitPreview();

    void PreviewSplitter_DragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Settings.PreviewWidth = PreviewColumn.ActualWidth;
        Settings.Save();
    }

    /// The selection moved: show it after a short pause, so holding an arrow key doesn't decode every photo on the way.
    void PreviewFollow()
    {
        if (!Settings.Preview || quick != null) return;
        var path = (active.List.SelectedItem as Entry)?.Path;
        if (path == previewShown) return;
        previewShown = path;
        previewDelay.Stop();
        previewDelay.Start();
    }

    void PreviewTick(object s, EventArgs e)
    {
        previewDelay.Stop();
        if (!Settings.Preview || quick != null) return;
        bool focused = active.List.IsKeyboardFocusWithin;
        FillQuickView();
        // a preview handler (Office, PDF) takes the keyboard when it starts; the arrows belong to the list
        if (focused) Dispatcher.BeginInvoke(() => { if (!active.List.IsKeyboardFocusWithin && quick == null) active.FocusList(); }, DispatcherPriority.ApplicationIdle);
    }

    // QuickLook (QL-Win), for those who prefer it and have it running: Space toggles its preview and a
    // new selection switches it, the way it follows Explorer. Spoken to over its own named pipe with
    // its own messages ("<message>|<path>|<options>"); nothing is started when it is not running.
    static readonly string QuickLookPipe = "QuickLook.App.Pipe." + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
    bool quickLookUsed; // Switch is only sent once QuickLook has been asked for something

    static bool QuickLookSend(string message, string path)
    {
        path = ShortcutTarget(path); // a shortcut to a Word file shows the document, not the shortcut
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", QuickLookPipe, System.IO.Pipes.PipeDirection.Out);
            pipe.Connect(150); // not running: give up quickly and use Quick View
            using var w = new StreamWriter(pipe);
            w.WriteLine($"QuickLook.App.PipeMessages.{message}|{path}|");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    void QuickLookFollow(PaneView p)
    {
        if (!quickLookUsed || p != active || p.Selected is not { Count: > 0 } sel) return;
        var path = sel[0].Path;
        Task.Run(() => QuickLookSend("Switch", path)); // only acts while its window is open
    }

    void QuickView()
    {
        if (quick != null) { quick.Close(); return; }
        if (Settings.QuickLook && active.Selected is { Count: > 0 } sel && QuickLookSend("Toggle", sel[0].Path)) { quickLookUsed = true; return; }
        quick = new Window
        {
            Owner = this, Width = 1000, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent, Foreground = Foreground, FontFamily = FontFamily, FontSize = 12, ShowInTaskbar = false,
        };
        quick.SourceInitialized += (_, _) => ApplyAcrylic(quick);
        quick.Background = Tint();
        quick.Closed += (_, _) => { StopMedia(); quickVersion++; quick = null; active.FocusList(); previewShown = null; PreviewFollow(); };
        quick.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space: case Key.Escape: quick.Close(); break;
                case Key.Up: case Key.Down:
                    var l = active.List;
                    l.SelectedIndex = Math.Clamp(l.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, l.Items.Count - 1);
                    l.ScrollIntoView(l.SelectedItem);
                    FillQuickView();
                    break;
                case Key.Enter when media != null: TogglePlay(); break;
                case Key.Left when media != null: media.Position -= TimeSpan.FromSeconds(5); break;
                case Key.Right when media != null: media.Position += TimeSpan.FromSeconds(5); break;
                default: return;
            }
            e.Handled = true;
        };
        mediaTimer.Tick -= MediaTick; mediaTimer.Tick += MediaTick;
        FillQuickView();
        quick.Show();
    }

    async void FillQuickView()
    {
        int v = ++quickVersion;
        StopMedia();
        if (active.List.SelectedItem is not Entry e || e.IsUp) { QuickTarget.Content = null; QuickTitle(""); return; }
        QuickTitle($"{e.Name}  ·  {e.SizeText}");
        var ext = Path.GetExtension(e.Name);
        try
        {
            // Disk reads off the UI thread: the preview pane follows every selection, and a file or folder
            // on a share that is asleep answers only after the SMB timeout.
            UIElement content =
                e.IsDir ? TextView(await Task.Run(() => FolderListing(e.Path))) :
                ImageExt.Contains(ext) ? await ImageView(e.Path) :
                VideoExt.Contains(ext) ? MediaView(e, video: true) :
                AudioExt.Contains(ext) ? MediaView(e, video: false) :
                ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? await PdfView(e.Path, v) :
                // null for a zip that only has a backup's name: listed below like any zip
                Backups.Named(e.Path) && await Task.Run(() => Backups.Inspect(e.Path, details: true, () => v != quickVersion)) is { } backup ? BackupView(e, backup) :
                ZipExt.Contains(ext) ? TextView(await Task.Run(() => ZipListing(e.Path))) :
                FontExt.Contains(ext) ? FontView(e.Path) :
                RenderedExt.Contains(ext) && Handler(e, v) is { } rendered ? rendered :
                await Task.Run(() => ReadText(e.Path)) is { } text ? TextView(text) :
                Handler(e, v) ?? TextView(await Task.Run(() => HexDump(e.Path)));
            if (v == quickVersion) Show(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                   or FileFormatException or InvalidDataException or System.Runtime.InteropServices.COMException)
        {
            if (v == quickVersion) Show(Summary(e, ex.Message));
        }
    }

    // Stepping through files with the arrow keys looks like one preview dissolving into the next.
    void Show(UIElement content)
    {
        QuickTarget.Content = content;
        Motion.Enter(content, ms: 140);
    }

    // Decoded off the UI thread: a 24 MP photo took 170 ms, and stepping through photos with the arrow
    // keys froze the window for that long at every press.
    static async Task<UIElement> ImageView(string path)
    {
        var bmp = await Task.Run(() =>
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            b.DecodePixelWidth = 2000;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze(); // so the UI thread may use it
            return b;
        });
        return new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new(16) };
    }

    static TextBox TextView(string text) => new()
    {
        Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        Background = Hex("#59000000"), Foreground = Hex("#E6FFFFFF"), BorderThickness = new(0), Padding = new(14),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    // PDF via the built-in Windows PDF renderer; pages appear one by one.
    async Task<UIElement> PdfView(string path, int v)
    {
        var doc = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        var pages = new StackPanel { Margin = new(16) };
        QuickTitle($"  ·  {doc.PageCount} pages", append: true);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            for (uint i = 0; i < Math.Min(doc.PageCount, 100u) && v == quickVersion; i++)
            {
                using var page = doc.GetPage(i);
                var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = 1400 });
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ras.AsStream();
                bmp.EndInit();
                pages.Children.Add(new Border { Child = new Image { Source = bmp }, MaxWidth = 850, Margin = new(0, 0, 0, 14), Background = Brushes.White });
            }
        });
        return new ScrollViewer { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    UIElement MediaView(Entry e, bool video)
    {
        media = new MediaElement { Source = new Uri(e.Path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
        var slider = new Slider { IsMoveToPointEnabled = true, Margin = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { Foreground = Hex("#B3FFFFFF"), VerticalAlignment = VerticalAlignment.Center, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        var play = new Button { Style = (Style)FindResource("Ghost"), Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        play.Click += (_, _) => TogglePlay();
        slider.PreviewMouseLeftButtonUp += (_, _) => media.Position = TimeSpan.FromSeconds(slider.Value);
        media.MediaOpened += (_, _) => { if (media.NaturalDuration.HasTimeSpan) slider.Maximum = media.NaturalDuration.TimeSpan.TotalSeconds; };
        media.MediaFailed += (_, ev) => { StopMedia(); QuickTarget.Content = Summary(e, "Can't play this file (missing codec?): " + ev.ErrorException.Message); };
        media.Tag = (slider, time, play);
        media.MouseLeftButtonUp += (_, _) => TogglePlay();

        var bar = new DockPanel { Margin = new(12, 8, 12, 10) };
        DockPanel.SetDock(play, Dock.Left); DockPanel.SetDock(time, Dock.Right);
        bar.Children.Add(play); bar.Children.Add(time); bar.Children.Add(slider);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        if (video)
        {
            root.Children.Add(new Border { Background = Brushes.Black, Child = media });
            _ = ReelHeader(e.Path, root, quickVersion);
        }
        else
        {
            var g = new Grid();
            g.Children.Add(media); // invisible for audio but still plays
            g.Children.Add(Summary(e));
            root.Children.Add(g);
        }
        // Quick View plays at once; the preview pane waits for a click, or every video passed on the way would start
        if (quick != null) media.Play();
        else { media.Pause(); paused = true; play.Content = "\uE768"; }
        mediaTimer.Start();
        return root;
    }

    // Reel (sister app, Sisters.Reel) knows this video: its poster, title and watched state above it, and "Reel" opens
    // Reel. Reel's list has no id to hand to its --play, so the link only starts Reel; playing stays this preview's
    // own button (one Open, the studio's rule). Nothing at all when Reel isn't installed or doesn't know the file.
    async Task ReelHeader(string path, DockPanel root, int v)
    {
        var (f, poster) = await Task.Run(() => Sisters.Reel(path) is { } r ? (r, r.Poster != null ? Poster(r.Poster) : null) : default);
        if (f == null || v != quickVersion) return;
        var (name, state) = ReelLine(f);
        var text = new TextBlock { Foreground = Hex("#99FFFFFF"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(name) { Foreground = Hex("#E6FFFFFF"), FontWeight = FontWeights.SemiBold });
        text.Inlines.Add($" · {state} · ");
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Reel")) { Foreground = Hex("#B4BFCE"), TextDecorations = null, ToolTip = "Open Reel" };
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.Click += (_, _) => { if (Sisters.Exe("reel") is { } exe) System.Diagnostics.Process.Start(exe); };
        text.Inlines.Add(link);
        var header = new DockPanel { Margin = new(12, 10, 12, 10) };
        if (poster != null)
        {
            var img = new Image { Source = poster, Width = 56, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetName(img, "Poster");
            header.Children.Add(img);
        }
        header.Children.Add(text);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Insert(0, header);
    }

    /// "Title (2019)" or "Series — S02E04 Episode title", and "watched 12 Oct" / "not watched".
    internal static (string Name, string State) ReelLine(Sisters.ReelFile f)
    {
        var name = f.Series == null ? f.Title + (f.Year is { } y ? $" ({y})" : "")
            : f.Series + " — " + (f.Season is { } s && f.Episode is { } n ? $"S{s:00}E{n:00}" + (f.Title != f.Series ? " " + f.Title : "") : f.Title);
        var state = !f.Watched ? "not watched"
            : f.LastWatched is { } d ? "watched " + d.ToString(d.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : "watched";
        return (name, state);
    }

    /// Reel's poster, small: null when it is missing, empty, 10 MB or more, or not a picture. Off the UI thread.
    internal static BitmapImage Poster(string path)
    {
        try
        {
            if (new FileInfo(path) is not { Exists: true, Length: > 0 and < 10 << 20 }) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad; // don't keep Reel's file locked
            b.DecodePixelWidth = 200;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                   or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException) { return null; }
    }

    bool paused;
    void TogglePlay()
    {
        if (media == null) return;
        if (paused) media.Play(); else media.Pause();
        paused = !paused;
        ((ValueTuple<Slider, TextBlock, Button>)media.Tag).Item3.Content = paused ? "" : "";
    }

    void MediaTick(object s, EventArgs e)
    {
        if (media?.Tag is not ValueTuple<Slider, TextBlock, Button> t) return;
        var (slider, time, _) = t;
        if (!slider.IsMouseCaptureWithin) slider.Value = media.Position.TotalSeconds;
        var total = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        time.Text = $"{media.Position:mm\\:ss} / {total:mm\\:ss}";
    }

    void StopMedia()
    {
        mediaTimer.Stop();
        media?.Close();
        media = null;
        paused = false;
    }

    static UIElement FontView(string path)
    {
        var family = Fonts.GetFontFamilies(new Uri(path)).FirstOrDefault()
            ?? throw new FileFormatException("Not a readable font");
        var sp = new StackPanel { Margin = new(24) };
        sp.Children.Add(new TextBlock { Text = string.Join(", ", family.FamilyNames.Values), Foreground = Hex("#99FFFFFF"), Margin = new(0, 0, 0, 12) });
        foreach (var size in new[] { 48, 32, 20, 14 })
            sp.Children.Add(new TextBlock { Text = "The quick brown fox jumps over the lazy dog 0123456789 ăâîșț", FontFamily = family, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // A Stash or Backup Labs backup (Backups.cs): what it is and holds, and a button to open it in its app.
    UIElement BackupView(Entry e, Backups.Info b)
    {
        var sp = new StackPanel { Margin = new(20) };
        sp.Children.Add(new TextBlock { Text = $"{b.App} backup", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var made = b.Made?.ToString("dddd, yyyy-MM-dd HH:mm:ss") ?? e.FullDate;
        var what = b.Unreadable ? "" : b.Encrypted ? " · encrypted" : " · not encrypted";
        sp.Children.Add(new TextBlock { Text = $"{made} · {e.SizeText}{what}", Foreground = Hex("#99FFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 12) });
        if (b.Unreadable)
        {
            sp.Children.Add(new TextBlock { Text = "Could not read this backup", Foreground = Hex("#F87171"), TextWrapping = TextWrapping.Wrap });
            return sp;
        }
        if (b.Encrypted) sp.Children.Add(new TextBlock { Text = $"Encrypted — open it in {b.App} with its password", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var open = new Button { Content = $"Open in {b.App}", Style = (Style)FindResource("Tab"), Tag = "on", Padding = new(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => OpenBackup(b.Id, e.Path);
        sp.Children.Add(open);
        var rows = new Grid { Margin = new(0, 14, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (name, value) in b.Rows)
        {
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var left = new TextBlock { Text = name, Foreground = Hex("#99FFFFFF"), Margin = new(0, 2, 16, 2) };
            var right = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 2) };
            Grid.SetRow(left, rows.RowDefinitions.Count - 1);
            Grid.SetRow(right, rows.RowDefinitions.Count - 1);
            Grid.SetColumn(right, 1);
            rows.Children.Add(left); rows.Children.Add(right);
        }
        sp.Children.Add(rows);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static string FolderListing(string path)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos().Take(1000))
        {
            n++;
            sb.AppendLine(fsi is DirectoryInfo ? $"{"",10}  {fsi.Name}\\" : $"{Fmt(((FileInfo)fsi).Length),10}  {fsi.Name}");
        }
        return n == 0 ? "(empty folder)" : sb.ToString();
    }

    static string ZipListing(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder($"{zip.Entries.Count} entries\n\n");
        foreach (var en in zip.Entries.Take(5000))
            sb.AppendLine($"{(en.FullName.EndsWith('/') ? "" : Fmt(en.Length)),10}  {en.LastWriteTime:yyyy-MM-dd HH:mm}  {en.FullName}");
        return sb.ToString();
    }

    // First 256 KB as text, or null if it looks binary.
    static string ReadText(string path)
    {
        var buf = new byte[256 * 1024];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(buf, 0, buf.Length);
        if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null;
        var text = new StreamReader(new MemoryStream(buf, 0, n), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        return n == buf.Length ? text + "\n\n… (truncated)" : text;
    }

    static string HexDump(string path)
    {
        var buf = new byte[8192];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(buf, 0, buf.Length);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i += 16)
        {
            var row = buf.AsSpan(i, Math.Min(16, n - i));
            sb.Append($"{i:X8}  ").Append(Convert.ToHexString(row).PadRight(32).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b))
              .Append("  ").AppendLine(new string(row.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '·').ToArray()));
        }
        return sb.Append(n == buf.Length ? "\n… (first 8 KB)" : "").ToString();
    }

    static UIElement Summary(Entry e, string error = null)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = e.Icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 56, Foreground = e.IconBrush, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = e.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        var info = e.IsDir ? $"Folder · {e.SizeText}" : $"{Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant()} file · {e.SizeText}";
        sp.Children.Add(new TextBlock { Text = $"{info}\nModified {e.FullDate}", Foreground = Hex("#99FFFFFF"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        if (error != null) sp.Children.Add(new TextBlock { Text = error, Foreground = Hex("#F87171"), Margin = new(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Center });
        return sp;
    }
}
