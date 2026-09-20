using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FileExplorer;

// "New file" (Ctrl+N): the same list Explorer's New menu offers, which is whatever installed apps
// registered under HKCR\.ext\...\ShellNew — so Office, editors and the rest show up by themselves.
public partial class NewFileDialog : Window
{
    public record FileType(string Name, string Extension, ImageSource Icon, RegistryKey ShellNew);

    /// The created file's name, or null when cancelled.
    public string Result { get; private set; }

    public NewFileDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Root.Background = MainWindow.Tint();
        Types.ItemsSource = ReadShellNewTypes();
        Types.SelectedIndex = 0;
        Loaded += (_, _) => { NameBox.Focus(); SelectBaseName(); };
    }

    static List<FileType> ReadShellNewTypes()
    {
        var list = new List<FileType>();
        foreach (var ext in Registry.ClassesRoot.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
            if (extKey == null) continue;
            // the ShellNew key sits either right under the extension or under its ProgID subkey
            var shellNew = extKey.OpenSubKey("ShellNew")
                ?? extKey.GetSubKeyNames().Select(n => extKey.OpenSubKey(n + @"\ShellNew")).FirstOrDefault(k => k != null);
            if (shellNew == null) continue;
            // Command-based entries hand the job to another app's wizard; we only create files ourselves.
            if (shellNew.GetValue("NullFile") == null && shellNew.GetValue("FileName") == null && shellNew.GetValue("Data") == null) continue;
            list.Add(new FileType(FriendlyName(extKey, ext), ext, ExtensionIcon(ext), shellNew));
        }
        // Everyday types Windows may not offer (here .txt was taken over by WPS, leaving no plain
        // "Text Document"), added only when the registry did not already provide that extension.
        foreach (var (ext, name) in new[] { (".txt", "Text Document"), (".md", "Markdown"), (".json", "JSON"),
                                            (".csv", "CSV"), (".ps1", "PowerShell script"), (".bat", "Batch file") })
            if (!list.Any(t => t.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                list.Add(new FileType(name, ext, ExtensionIcon(ext), null));

        // Text Document first, then alphabetical: it's what a "new file" usually means
        return list.GroupBy(t => t.Extension, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                   .OrderByDescending(t => t.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                   .ThenBy(t => t.Name).ToList();
    }

    static string FriendlyName(RegistryKey extKey, string ext)
    {
        if (extKey.GetValue("") is string progId && Registry.ClassesRoot.OpenSubKey(progId)?.GetValue("") is string name && name != "")
            return name;
        return ext.TrimStart('.').ToUpperInvariant() + " file";
    }

    // Icon for a file type without a file: ask the shell about the extension alone.
    static ImageSource ExtensionIcon(string ext)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo("x" + ext, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info, Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x10 | 0x1) == IntPtr.Zero)
            return null;
        try
        {
            var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    FileType Selected => Types.SelectedItem as FileType;

    void Type_Changed(object s, SelectionChangedEventArgs e)
    {
        if (Selected is not { } t) return;
        NameBox.Text = $"New {t.Name}{t.Extension}";
        SelectBaseName();
    }

    void SelectBaseName()
    {
        var dot = NameBox.Text.LastIndexOf('.');
        NameBox.Select(0, dot < 0 ? NameBox.Text.Length : dot);
    }

    void Name_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Create_Click(s, null);
        else if (e.Key == Key.Escape) Close();
    }

    void Cancel_Click(object s, RoutedEventArgs e) => Close();

    void Create_Click(object s, RoutedEventArgs e)
    {
        if (Selected is not { } t || NameBox.Text.Trim() == "") return;
        Result = NameBox.Text.Trim();
        Template = t.ShellNew;
        DialogResult = true;
    }

    RegistryKey Template;

    /// Creates the chosen file in `dir` the way the shell would, and returns its name.
    public string Create(string dir)
    {
        var path = Path.Combine(dir, Result);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"{Result} already exists");
        if (Template?.GetValue("FileName") is string template && template != "")
        {
            var full = Path.IsPathRooted(template) ? template
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Templates), template);
            if (File.Exists(full)) { File.Copy(full, path); return Result; }
        }
        if (Template?.GetValue("Data") is byte[] data) File.WriteAllBytes(path, data);
        else File.Create(path).Dispose();
        return Result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, int size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
