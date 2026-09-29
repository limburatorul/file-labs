using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FileExplorer;

// F4 editor: Monaco (VS Code's editor) in WebView2, one window with a tab per file. Editor\editor.html
// does the editing, the tabs and the status bar; this window reads, writes and watches the files, keeps
// each one's encoding, asks before losing changes, and remembers the open files and the settings.
public sealed class EditorWindow : Window
{
    const string Host = "filelabs.editor";
    const long MaxBytes = 20 * 1024 * 1024; // Monaco copes, but past this it's a log, not something to edit
    static readonly string EditorDir = Path.Combine(AppContext.BaseDirectory, "Editor");
    static readonly string StateFile = Path.Combine(Settings.Dir, "editor.json");
    static Task<CoreWebView2Environment> env;
    static EditorWindow instance;

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
        ".apex", ".trigger", ".cypher", ".ecl", ".flow", ".ftl", ".mips", ".pla", ".postiats", ".redis", ".sb", ".tsp",
    };
    static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "CMakeLists.txt", "Jenkinsfile", "Vagrantfile", "Gemfile",
        "Rakefile", "Procfile", "LICENSE", "README", "CHANGELOG", "AUTHORS", "CODEOWNERS", "hosts",
    };

    public static bool IsText(string path) => TextExt.Contains(Path.GetExtension(path)) || TextNames.Contains(Path.GetFileName(path));

    sealed class Doc
    {
        public string Path;
        public string Encoding;          // utf8, utf8bom, utf16le, utf16be, legacy — see Decode
        public bool Dirty;
        public DateTime KnownWrite;      // the file's stamp when we last read or wrote it
        public FileSystemWatcher Watcher;
    }

    readonly WebView2 web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0f, 0x13, 0x1b) };
    readonly Dictionary<int, Doc> docs = new();
    readonly List<string> queue = new(); // messages for the page, held until it is ready
    bool ready, closing;
    int nextId = 1;

    EditorWindow()
    {
        Width = 1200; Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = MainWindow.Hex("#0f131b");
        Title = "File Labs editor";
        Content = web;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Loaded += async (_, _) => await Start();
    }

    /// <summary>Opens a file in the editor window, as a new tab or by switching to its tab.</summary>
    public static void Open(string path)
    {
        var fresh = instance == null;
        if (fresh)
        {
            instance = new EditorWindow();
            instance.RestoreSession(except: path);
        }
        instance.AddFile(path, activate: true, line: 1, column: 1, quiet: false);
        Show(fresh);
    }

    /// <summary>Two files side by side, differences marked, in a read-only tab.</summary>
    public static void Compare(string left, string right)
    {
        var fresh = instance == null;
        instance ??= new EditorWindow();
        if (instance.Read(left, quiet: false) is not { } l || instance.Read(right, quiet: false) is not { } r)
        {
            if (fresh) instance = null;
            return;
        }
        instance.Post(new JsonObject
        {
            ["type"] = "diff", ["id"] = instance.nextId++,
            ["left"] = new JsonObject { ["name"] = Path.GetFileName(left), ["text"] = l.Text },
            ["right"] = new JsonObject { ["name"] = Path.GetFileName(right), ["text"] = r.Text },
        });
        Show(fresh);
    }

    static void Show(bool fresh)
    {
        if (fresh) instance.Show();
        if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
        instance.Activate();
    }

    /// <summary>The accent changed in Settings.</summary>
    public static void AccentChanged() => instance?.Post(new JsonObject { ["type"] = "accent", ["accent"] = Settings.Accent });

    void AddFile(string path, bool activate, int line, int column, bool quiet)
    {
        path = Path.GetFullPath(path);
        var open = docs.FirstOrDefault(d => string.Equals(d.Value.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open.Value != null) { if (activate) Post(new JsonObject { ["type"] = "focus", ["id"] = open.Key }); return; }
        if (Read(path, quiet) is not { } read) return;
        var (text, encoding) = read;
        var id = nextId++;
        var doc = new Doc { Path = path, Encoding = encoding, KnownWrite = Stamp(path) };
        docs[id] = doc;
        Watch(id, doc);
        Post(new JsonObject
        {
            ["type"] = "open", ["id"] = id, ["name"] = Path.GetFileName(path), ["path"] = path, ["text"] = text,
            ["encoding"] = encoding, ["activate"] = activate, ["line"] = line, ["column"] = column, ["detectIndent"] = true,
        });
    }

    (string Text, string Encoding)? Read(string path, bool quiet)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytes) return Refuse($"{Path.GetFileName(path)} is over 20 MB, too big for the editor.");
            return Decode(File.ReadAllBytes(path)) is { } decoded ? decoded : Refuse($"{Path.GetFileName(path)} isn't a text file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(ex.Message);
        }

        (string, string)? Refuse(string message)
        {
            if (!quiet) PromptDialog.Choose(IsVisible ? this : Application.Current.MainWindow, "The editor can't open this file", message, "OK");
            return null;
        }
    }

    static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    // BOM first; otherwise strict UTF-8; otherwise Latin-1, which maps every byte to one character and
    // back, so a file in an old code page is saved byte for byte as it was, apart from the edits.
    // A NUL in the first 8 KB means binary.
    static (string, string)? Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), "utf8bom");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "utf16le");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "utf16be");
        if (Array.IndexOf(b, (byte)0, 0, Math.Min(b.Length, 8192)) >= 0) return null;
        try { return (new UTF8Encoding(false, true).GetString(b), "utf8"); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), "legacy"); }
    }

    static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf8bom" => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
        "utf16le" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
        "utf16be" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        "legacy" => Encoding.Latin1.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    // Another program changing the file shows a bar in its tab: Reload, or keep what's in the editor.
    // Our own saves update KnownWrite first, so they don't count.
    void Watch(int id, Doc doc)
    {
        try
        {
            doc.Watcher = new FileSystemWatcher(Path.GetDirectoryName(doc.Path)!, Path.GetFileName(doc.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // a share that won't be watched: the file still edits, just without the bar
        }
        void Changed() => Dispatcher.BeginInvoke(() =>
        {
            if (!docs.ContainsKey(id)) return;
            var stamp = Stamp(doc.Path);
            if (stamp == doc.KnownWrite) return;
            doc.KnownWrite = stamp;
            Post(new JsonObject { ["type"] = "diskChanged", ["id"] = id, ["deleted"] = stamp == DateTime.MinValue });
        });
        doc.Watcher.Changed += (_, _) => Changed();
        doc.Watcher.Created += (_, _) => Changed();
        doc.Watcher.Deleted += (_, _) => Changed();
        doc.Watcher.Renamed += (_, _) => Changed();
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
            PromptDialog.Choose(Application.Current.MainWindow, "The editor needs the Microsoft Edge WebView2 Runtime",
                "It is part of Windows 11 but not installed here. Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703", "OK");
            closing = true; Close();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // F5 would reload the page and drop every open tab; Ctrl+P would print it
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, EditorDir, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/")) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{Host}/editor.html");
    }

    void Post(JsonObject message)
    {
        var json = message.ToJsonString();
        if (ready) web.CoreWebView2.PostWebMessageAsJson(json); else queue.Add(json);
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        int Id() => m.GetProperty("id").GetInt32();
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                ready = true;
                web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "init", ["accent"] = Settings.Accent, ["settings"] = LoadState()?["settings"]?.DeepClone() }.ToJsonString());
                foreach (var json in queue) web.CoreWebView2.PostWebMessageAsJson(json);
                queue.Clear();
                break;
            case "dirty":
                if (docs.TryGetValue(Id(), out var d)) d.Dirty = m.GetProperty("value").GetBoolean();
                break;
            case "active":
                Title = m.GetProperty("title").GetString() is { Length: > 0 } t ? $"{Path.GetFileName(t)}  ·  {Path.GetDirectoryName(t)}" : "File Labs editor";
                break;
            case "save":
                SaveTab(Id(), m.GetProperty("text").GetString(), m.GetProperty("encoding").GetString(), m.GetProperty("version").GetInt32());
                break;
            case "closeRequest":
                _ = CloseDirtyTab(Id());
                break;
            case "closed":
                if (docs.Remove(Id(), out var gone)) gone.Watcher?.Dispose();
                if (m.GetProperty("empty").GetBoolean()) Close();
                break;
            case "reload":
                Reload(Id());
                break;
            case "settings":
                SaveState(settings: JsonNode.Parse(m.GetProperty("settings").GetRawText()), session: null);
                break;
        }
    }

    bool SaveTab(int id, string text, string encoding, int version)
    {
        var d = docs[id];
        if (!Write(d.Path, Encode(text, encoding))) return false;
        d.Encoding = encoding;
        d.KnownWrite = Stamp(d.Path);
        d.Dirty = false;
        Post(new JsonObject { ["type"] = "saved", ["id"] = id, ["version"] = version, ["encoding"] = encoding });
        return true;
    }

    // Written next to the file, then swapped in: a failed write never leaves a half-written file.
    // File.Replace keeps the original's attributes and permissions. A file the user may not write
    // (hosts, anything under Program Files) can be saved through an elevated copy instead.
    bool Write(string path, byte[] bytes)
    {
        var temp = path + ".filelabs-tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return PromptDialog.Choose(this, $"Save {Path.GetFileName(path)} as administrator?",
                "You don't have permission to change this file. Windows will ask to confirm.", "Save as administrator", "Cancel") == 0
                && WriteElevated(path, bytes);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            PromptDialog.Choose(this, $"Couldn't save {Path.GetFileName(path)}", ex.Message, "OK");
            return false;
        }
    }

    // The new contents go to %TEMP% first; an elevated cmd copies them over the file, which keeps the
    // file's own permissions. Windows shows its UAC prompt for it.
    bool WriteElevated(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"filelabs-save-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temp, bytes);
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c copy /y /b \"{temp}\" \"{path}\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p.WaitForExit();
            if (p.ExitCode == 0) return true;
            PromptDialog.Choose(this, $"Couldn't save {Path.GetFileName(path)} as administrator either", null, "OK");
            return false;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt declined
        finally { TryDelete(temp); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void Reload(int id)
    {
        var d = docs[id];
        if (Read(d.Path, quiet: false) is not { } read) return;
        var (text, encoding) = read;
        d.Encoding = encoding;
        d.KnownWrite = Stamp(d.Path);
        Post(new JsonObject { ["type"] = "reloaded", ["id"] = id, ["text"] = text, ["encoding"] = encoding });
    }

    async Task CloseDirtyTab(int id)
    {
        var answer = PromptDialog.Choose(this, $"Save changes to {Path.GetFileName(docs[id].Path)}?",
            "Your changes are lost if you don't save them.", "Save", "Don't save", "Cancel");
        if (answer is 2 or -1) return;
        if (answer == 0)
        {
            var t = JsonNode.Parse(await web.CoreWebView2.ExecuteScriptAsync($"textOf({id})"))!;
            if (!SaveTab(id, (string)t["text"], (string)t["encoding"], -1)) return;
        }
        Post(new JsonObject { ["type"] = "close", ["id"] = id });
    }

    // ---- window close: one question for every unsaved tab, then the session is remembered ----
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closing || !ready) return;
        e.Cancel = true;
        _ = CloseAll();
    }

    async Task CloseAll()
    {
        var dirty = docs.Where(d => d.Value.Dirty).ToList();
        if (dirty.Count > 0)
        {
            var names = string.Join("\n", dirty.Select(d => Path.GetFileName(d.Value.Path)));
            var answer = PromptDialog.Choose(this, $"Save changes to {(dirty.Count == 1 ? "this file" : $"these {dirty.Count} files")}?",
                names, dirty.Count == 1 ? "Save" : "Save all", "Don't save", "Cancel");
            if (answer is 2 or -1) return;
            if (answer == 0)
                foreach (var t in JsonNode.Parse(await web.CoreWebView2.ExecuteScriptAsync("dirtyTexts()"))!.AsArray())
                    if (!SaveTab((int)t!["id"], (string)t["text"], (string)t["encoding"], -1)) return;
        }
        var session = JsonNode.Parse(await web.CoreWebView2.ExecuteScriptAsync("sessionState()"));
        SaveState(settings: null, session: session);
        closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        foreach (var d in docs.Values) d.Watcher?.Dispose();
        web.Dispose();
        instance = null;
    }

    // ---- editor.json: { settings: {...}, session: { active, files: [{ path, line, column }] } } ----
    static JsonObject LoadState()
    {
        try { return JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static void SaveState(JsonNode settings, JsonNode session)
    {
        var state = LoadState() ?? new JsonObject();
        if (settings != null) state["settings"] = settings;
        if (session != null) state["session"] = session;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            Settings.WriteAllLines(StateFile, new[] { state.ToJsonString() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // only convenience is lost
    }

    // The tabs open when the editor was last closed come back, cursors where they were. Files since
    // deleted or unreadable are skipped without a message.
    void RestoreSession(string except)
    {
        if (LoadState()?["session"]?["files"] is not JsonArray files) return;
        foreach (var f in files)
        {
            var path = (string)f?["path"];
            if (path == null || !File.Exists(path) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase)) continue;
            AddFile(path, activate: false, line: (int?)f["line"] ?? 1, column: (int?)f["column"] ?? 1, quiet: true);
        }
    }
}
