using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FileExplorer;

// F4 editor: Monaco (VS Code's editor) in WebView2, with colouring for ~80 languages and Format
// Document (Shift+Alt+F) for JSON, JS/TS, HTML and CSS. Editor\editor.html does the editing; this
// window reads and writes the file, in the encoding it came in, and asks before losing changes.
public sealed class EditorWindow : Window
{
    const string Host = "filelabs.editor";
    const long MaxBytes = 20 * 1024 * 1024; // Monaco copes, but past this it's a log, not something to edit
    static readonly string EditorDir = Path.Combine(AppContext.BaseDirectory, "Editor");
    static Task<CoreWebView2Environment> env;

    readonly string path;
    readonly WebView2 web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0f, 0x13, 0x1b) };
    readonly Encoding encoding;
    string text;
    bool dirty, closing;

    EditorWindow(string path, string text, Encoding encoding)
    {
        this.path = path; this.text = text; this.encoding = encoding;
        Width = 1100; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = MainWindow.Hex("#0f131b");
        Content = web;
        SetTitle();
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Loaded += async (_, _) => await Start();
    }

    // What Enter opens in the editor (Settings → "Open code and text files in the File Labs editor"):
    // every extension Monaco colours, plus plain-text formats it shows uncoloured. F4 opens anything.
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".md", ".markdown", ".mdx", ".rst", ".csv", ".tsv", ".nfo", ".diz", ".srt", ".vtt", ".sub",
        ".json", ".jsonc", ".json5", ".jsonl", ".geojson", ".webmanifest", ".har", ".ipynb",
        ".xml", ".xsd", ".xsl", ".xslt", ".xaml", ".axaml", ".svg", ".plist", ".resx", ".config", ".manifest", ".nuspec",
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props", ".targets", ".sln", ".slnx", ".wxs",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".cnf", ".properties", ".env", ".editorconfig", ".gitignore",
        ".gitattributes", ".gitmodules", ".dockerignore", ".npmrc", ".prettierrc", ".eslintrc", ".babelrc", ".reg", ".inf", ".lock",
        ".html", ".htm", ".xhtml", ".shtml", ".css", ".scss", ".sass", ".less", ".styl",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".vue", ".svelte", ".astro",
        ".php", ".phtml", ".twig", ".blade", ".hbs", ".handlebars", ".mustache", ".liquid", ".pug", ".jade", ".ejs", ".cshtml", ".razor",
        ".py", ".pyw", ".pyi", ".rb", ".erb", ".pl", ".pm", ".lua", ".r", ".jl", ".tcl", ".dart", ".ex", ".exs", ".erl", ".clj", ".cljs",
        ".scm", ".rkt", ".lisp", ".el", ".hs", ".ml", ".mli", ".fs", ".fsi", ".fsx", ".scala", ".sc", ".kt", ".kts", ".groovy", ".gradle",
        ".java", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx", ".inl", ".ino", ".m", ".mm", ".swift", ".go", ".rs", ".zig",
        ".nim", ".d", ".v", ".sv", ".svh", ".vhd", ".vhdl", ".asm", ".s", ".pas", ".pp", ".dpr", ".cs", ".csx", ".vb", ".vbs", ".bas",
        ".sol", ".move", ".wgsl", ".glsl", ".hlsl", ".frag", ".vert", ".shader", ".cg", ".proto", ".thrift", ".graphql", ".gql",
        ".sql", ".psql", ".mysql", ".cql", ".sparql", ".rq", ".kql", ".dax", ".pq", ".m4",
        ".ps1", ".psm1", ".psd1", ".ps1xml", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish", ".ksh", ".awk", ".sed", ".mk", ".cmake",
        ".tf", ".tfvars", ".hcl", ".bicep", ".nix", ".dockerfile", ".containerfile", ".http", ".rest", ".tex", ".bib", ".sty", ".cls",
        ".diff", ".patch", ".ahk", ".au3", ".iss", ".nsi", ".nsh", ".qs", ".coffee", ".elm", ".purs", ".re", ".res", ".st", ".abap",
        ".apex", ".cls", ".trigger", ".cypher", ".ecl", ".flow", ".ftl", ".mips", ".pla", ".postiats", ".redis", ".sb", ".tsp",
    };
    static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "CMakeLists.txt", "Jenkinsfile", "Vagrantfile", "Gemfile",
        "Rakefile", "Procfile", "LICENSE", "README", "CHANGELOG", "AUTHORS", "CODEOWNERS", "hosts",
    };

    public static bool IsText(string path) => TextExt.Contains(Path.GetExtension(path)) || TextNames.Contains(Path.GetFileName(path));

    /// <summary>Opens a file in a new editor window, or says why it can't be edited.</summary>
    public static void Open(Window owner, string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytes) { Tell(owner, $"{Path.GetFileName(path)} is over 20 MB, too big for the editor."); return; }
            var bytes = File.ReadAllBytes(path);
            if (Decode(bytes) is not var (text, encoding)) { Tell(owner, $"{Path.GetFileName(path)} isn't a text file."); return; }
            new EditorWindow(path, text, encoding) { Owner = owner }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(owner, ex.Message);
        }
    }

    static void Tell(Window owner, string message) => MessageBox.Show(owner, message, "File Labs", MessageBoxButton.OK, MessageBoxImage.Information);

    // BOM first; otherwise strict UTF-8; otherwise Latin-1, which maps every byte to one character and
    // back, so a file in an old code page is saved byte for byte as it was, apart from the edits.
    // A NUL in the first 8 KB means binary.
    static (string, Encoding)? Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), new UTF8Encoding(true));
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), Encoding.Unicode);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), Encoding.BigEndianUnicode);
        if (Array.IndexOf(b, (byte)0, 0, Math.Min(b.Length, 8192)) >= 0) return null;
        try { return (new UTF8Encoding(false, true).GetString(b), new UTF8Encoding(false)); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), Encoding.Latin1); }
    }

    async Task Start()
    {
        try
        {
            env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLabs", "WebView2"));
            await web.EnsureCoreWebView2Async(await env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            env = null;
            Tell(this, "The editor needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 but not installed here.\n\n" +
                       "Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            closing = true; Close();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, EditorDir, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/")) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{Host}/editor.html");
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "open", text, name = Path.GetFileName(path) }));
                text = null; // the page holds it now
                break;
            case "dirty":
                dirty = m.GetProperty("value").GetBoolean();
                SetTitle();
                break;
            case "save":
                if (Save(m.GetProperty("text").GetString()))
                    web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "saved", version = m.GetProperty("version").GetInt32() }));
                break;
            case "close":
                Close();
                break;
        }
    }

    // Written next to the file, then swapped in: a failed write never leaves a half-written file.
    // File.Replace keeps the original's attributes and permissions.
    bool Save(string content)
    {
        var temp = path + ".filelabs-tmp";
        try
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
            File.WriteAllBytes(temp, bytes);
            File.Replace(temp, path, null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            MessageBox.Show(this, $"Couldn't save {Path.GetFileName(path)}.\n\n{ex.Message}", "File Labs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void SetTitle() => Title = $"{(dirty ? "● " : "")}{Path.GetFileName(path)}  ·  {Path.GetDirectoryName(path)}";

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closing || !dirty) return;
        var answer = MessageBox.Show(this, $"Save changes to {Path.GetFileName(path)}?", "File Labs",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.No) return;
        // The text lives in the page: fetch it, save, then close for real.
        e.Cancel = true;
        _ = SaveAndClose();
    }

    async Task SaveAndClose()
    {
        var json = await web.CoreWebView2.ExecuteScriptAsync("currentText()");
        if (!Save(JsonSerializer.Deserialize<string>(json))) return;
        closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        web.Dispose();
    }
}
