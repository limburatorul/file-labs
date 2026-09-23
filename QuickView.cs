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
        if (host != null) host.Failed += msg => { if (v == quickVersion) quick.Content = Summary(e, msg); };
        return host;
    }

    void QuickView()
    {
        if (quick != null) { quick.Close(); return; }
        quick = new Window
        {
            Owner = this, Width = 1000, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent, Foreground = Foreground, FontFamily = FontFamily, FontSize = 12, ShowInTaskbar = false,
        };
        quick.SourceInitialized += (_, _) => ApplyAcrylic(quick);
        quick.Background = Tint();
        quick.Closed += (_, _) => { StopMedia(); quickVersion++; quick = null; active.FocusList(); };
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
        if (active.List.SelectedItem is not Entry e || e.IsUp) { quick.Content = null; quick.Title = ""; return; }
        quick.Title = $"{e.Name}  ·  {e.SizeText}";
        var ext = Path.GetExtension(e.Name);
        try
        {
            UIElement content =
                e.IsDir ? TextView(FolderListing(e.Path)) :
                ImageExt.Contains(ext) ? ImageView(e.Path) :
                VideoExt.Contains(ext) ? MediaView(e, video: true) :
                AudioExt.Contains(ext) ? MediaView(e, video: false) :
                ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? await PdfView(e.Path, v) :
                ZipExt.Contains(ext) ? TextView(ZipListing(e.Path)) :
                FontExt.Contains(ext) ? FontView(e.Path) :
                RenderedExt.Contains(ext) && Handler(e, v) is { } rendered ? rendered :
                ReadText(e.Path) is { } text ? TextView(text) :
                Handler(e, v) ?? TextView(HexDump(e.Path));
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
        quick.Content = content;
        if (Settings.Animations)
            content.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    static UIElement ImageView(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bmp.DecodePixelWidth = 2000;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
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
        quick.Title += $"  ·  {doc.PageCount} pages";
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
        var play = new Button { Style = (Style)FindResource("Ghost"), Content = "", FontFamily = new FontFamily("Segoe Fluent Icons") };
        play.Click += (_, _) => TogglePlay();
        slider.PreviewMouseLeftButtonUp += (_, _) => media.Position = TimeSpan.FromSeconds(slider.Value);
        media.MediaOpened += (_, _) => { if (media.NaturalDuration.HasTimeSpan) slider.Maximum = media.NaturalDuration.TimeSpan.TotalSeconds; };
        media.MediaFailed += (_, ev) => { StopMedia(); quick.Content = Summary(e, "Can't play this file (missing codec?): " + ev.ErrorException.Message); };
        media.Tag = (slider, time, play);
        media.MouseLeftButtonUp += (_, _) => TogglePlay();

        var bar = new DockPanel { Margin = new(12, 8, 12, 10) };
        DockPanel.SetDock(play, Dock.Left); DockPanel.SetDock(time, Dock.Right);
        bar.Children.Add(play); bar.Children.Add(time); bar.Children.Add(slider);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        if (video) root.Children.Add(new Border { Background = Brushes.Black, Child = media });
        else
        {
            var g = new Grid();
            g.Children.Add(media); // invisible for audio but still plays
            g.Children.Add(Summary(e));
            root.Children.Add(g);
        }
        media.Play();
        mediaTimer.Start();
        return root;
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
        sp.Children.Add(new TextBlock { Text = e.Icon, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 56, Foreground = e.IconBrush, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = e.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        var info = e.IsDir ? $"Folder · {e.SizeText}" : $"{Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant()} file · {e.SizeText}";
        sp.Children.Add(new TextBlock { Text = $"{info}\nModified {e.FullDate}", Foreground = Hex("#99FFFFFF"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        if (error != null) sp.Children.Add(new TextBlock { Text = error, Foreground = Hex("#F87171"), Margin = new(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Center });
        return sp;
    }
}
