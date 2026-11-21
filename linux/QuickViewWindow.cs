using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FileExplorer;

/// Quick View (Space) and the preview pane (Alt+P), as on Windows: pictures, text and code, a PDF's first page (with
/// poppler's pdftoppm), what is inside a ZIP or tar and a Stash or Backup Labs backup, a folder's list; anything else
/// gets its summary and the first bytes. Up and Down walk the pane's list; Space or Esc closes.
sealed class QuickViewWindow : Window
{
    readonly PaneView pane;
    readonly ContentControl body = new();

    public QuickViewWindow(PaneView pane, Entry e)
    {
        this.pane = pane;
        Width = 900; Height = 640; MinWidth = 360; MinHeight = 240;
        Background = Fmt.Brush("#161A22"); Foreground = Fmt.Brush("#E8EEF6"); FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = body;
        Show(e);
        KeyDown += (_, k) =>
        {
            switch (k.Key)
            {
                case Key.Space or Key.Escape: Close(); break;
                case Key.Up or Key.Down:
                    var list = pane.List;
                    list.SelectedIndex = Math.Clamp(list.SelectedIndex + (k.Key == Key.Down ? 1 : -1), 0, list.ItemCount - 1);
                    if (list.SelectedItem is Entry n) { list.ScrollIntoView(n); Show(n); }
                    break;
                default: return;
            }
            k.Handled = true;
        };
    }

    void Show(Entry e)
    {
        Title = $"{e.Name}  ·  {e.SizeText}";
        body.Content = Body(e, small: false);
    }

    static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".ts", ".ogv", ".wmv" };
    static readonly HashSet<string> Sounds = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma" };
    static readonly HashSet<string> Texty = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".cs", ".js", ".ts", ".py", ".json", ".xml", ".html", ".css", ".xaml", ".axaml", ".sh", ".conf", ".ini", ".toml",
        ".yaml", ".yml", ".c", ".cpp", ".h", ".rs", ".go", ".java", ".kt", ".csv", ".tsv", ".desktop", ".service", ".csproj", ".sln", ".gitignore", ".env",
    };

    public static Control Body(Entry e, bool small)
    {
        try
        {
            if (e.IsDir) return Text(FolderListing(e.Path));
            var ext = Path.GetExtension(e.Name);
            if (Pictures.Contains(ext)) return Picture(e.Path, small ? 900 : 2000);
            if ((Videos.Contains(ext) || Sounds.Contains(ext)) && MediaView.TryCreate(e.Path, Videos.Contains(ext), play: !small, why => Summary(e, why ?? "")) is { } media) return media;
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && PdfPage(e.Path) is { } page) return page;
            if (Backups.Named(e.Path) && Backups.Inspect(e.Path, details: true) is { } b)
                return Text($"{b.App} backup{(b.Made is { } m ? $", made {m:yyyy-MM-dd HH:mm}" : "")}{(b.Encrypted ? " (password protected)" : "")}\n\n" + string.Join("\n", b.Rows.Select(r => $"{r.Name}: {r.Value}")));
            if (e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".tar") || e.Name.EndsWith(".tar.gz") || e.Name.EndsWith(".tgz"))
            {
                var items = Archives.List(e.Path);
                return Text($"{items.Count:N0} item(s){(items.Count >= 2000 ? " (the first 2,000)" : "")}\n\n" + string.Join("\n", items.Select(i => $"{Fmt.Size(i.Size),12}  {i.Name}")));
            }
            if (Texty.Contains(ext) || LooksLikeText(e.Path)) return Text(ReadText(e.Path), mono: true);
            return Summary(e, HexDump(e.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { return Summary(e, ex.Message); }
    }

    static Control Picture(string path, int width)
    {
        using var s = File.OpenRead(path);
        var bmp = Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
        return new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new(12) };
    }

    /// The first page through pdftoppm (poppler), when it is installed.
    static Control PdfPage(string path)
    {
        if (!Shell.Exists("pdftoppm")) return null;
        var outBase = Path.Combine(Path.GetTempPath(), $"filelabs-pdf-{Environment.ProcessId}");
        var psi = new ProcessStartInfo("pdftoppm") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-png", "-f", "1", "-l", "1", "-singlefile", "-scale-to", "1400", path, outBase }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null || !p.WaitForExit(8000) || p.ExitCode != 0 || !File.Exists(outBase + ".png")) return null;
        var c = Picture(outBase + ".png", 1400);
        File.Delete(outBase + ".png");
        return new ScrollViewer { Content = new Border { Background = Brushes.White, Child = c, Margin = new(12), HorizontalAlignment = HorizontalAlignment.Center } };
    }

    static Control Text(string text, bool mono = false) => new ScrollViewer
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Content = new SelectableTextBlock
        {
            Text = text, Margin = new(16, 12), FontSize = mono ? 12 : 12.5,
            FontFamily = mono ? new FontFamily("JetBrains Mono, Fira Code, Cascadia Mono, DejaVu Sans Mono, Noto Sans Mono, monospace") : FontFamily.Default,
        },
    };

    static Control Summary(Entry e, string more)
    {
        var open = new Button { Content = "Open with its app", Classes = { "job" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 16, 0, 0) };
        open.Click += (_, _) => Shell.Open(e.Path);
        return new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Top, Margin = new(0, 40, 0, 16), HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = e.Icon, Classes = { "icon" }, FontSize = 56, Foreground = e.IconBrush, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = e.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new(0, 12, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"{Fmt.TypeName(e.Name, e.IsDir)} · {e.SizeText}\nModified {e.Modified:yyyy-MM-dd HH:mm}", Foreground = Fmt.Brush("#99FFFFFF"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                        open,
                    },
                },
                Text(more, mono: true),
            },
        };
    }

    static bool LooksLikeText(string path)
    {
        var buf = new byte[4096];
        using var s = File.OpenRead(path);
        int n = s.Read(buf, 0, buf.Length);
        return n > 0 && !buf.AsSpan(0, n).Contains((byte)0);
    }

    static string ReadText(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[Math.Min(s.Length, 512 * 1024)];
        int n = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        var t = Encoding.UTF8.GetString(buf, 0, n);
        return s.Length > n ? t + "\n\n… (the first 512 KB)" : t;
    }

    static string HexDump(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[1024];
        int n = s.Read(buf, 0, buf.Length);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i += 16)
        {
            var line = buf.AsSpan(i, Math.Min(16, n - i));
            sb.Append($"{i:X8}  ").Append(string.Join(" ", line.ToArray().Select(b => b.ToString("X2"))).PadRight(48)).Append("  ");
            foreach (var b in line) sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string FolderListing(string dir)
    {
        var lines = new List<string>();
        foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).OrderBy(x => x is FileInfo).ThenBy(x => x.Name, NaturalOrder.Instance).Take(500))
            lines.Add(fsi is FileInfo f ? $"{Fmt.Size(f.Length),12}  {f.Name}" : $"{"",12}  {fsi.Name}/");
        return lines.Count == 0 ? "This folder is empty" : string.Join("\n", lines);
    }
}
