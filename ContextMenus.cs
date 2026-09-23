using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileExplorer;

// Right-click: File Labs' own menu, or the real Windows one (Settings → Files → Context menu).
public partial class MainWindow
{
    void HookContextMenu(PaneView p)
    {
        p.List.ContextMenuOpening += (_, e) =>
        {
            SetActive(p);
            // right-click on empty space = the folder itself, not whatever is still selected
            var row = PaneView.FindRow(e.OriginalSource as DependencyObject);
            if (row == null && e.CursorLeft >= 0) p.List.SelectedItems.Clear();
            var sel = p.Selected;
            e.Handled = true;

            if (Settings.NativeMenu)
            {
                if (ShellMenu.Show(this, p.Dir, sel.Select(x => x.Path).ToList()) == "rename") Rename();
                p.Refresh();
                return;
            }
            var menu = sel.Count > 0 ? ItemMenu(p, sel) : FolderMenu(p);
            menu.PlacementTarget = p.List;
            menu.IsOpen = true;
        };
    }

    static MenuItem Item(string header, string glyph, string gesture, Action run, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header = header, InputGestureText = gesture, IsEnabled = enabled,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 },
        };
        mi.Click += (_, _) => run();
        return mi;
    }

    ContextMenu ItemMenu(PaneView p, List<Entry> sel)
    {
        var first = sel[0];
        bool one = sel.Count == 1, dir = one && first.IsDir;
        var ext = Path.GetExtension(first.Name);
        var m = new ContextMenu();
        m.Items.Add(Item("Open", "", "Enter", () => p.OpenSelected()));
        if (dir)
        {
            m.Items.Add(Item("Open in new tab", "", "Middle click", () => { p.NewTab(); p.Navigate(first.Path); }));
            m.Items.Add(Item("Open in other pane", "", "", () => Other.Navigate(first.Path)));
        }
        else if (one)
        {
            m.Items.Add(Item("Open with…", "", "", () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {first.Path}")));
            if (Runnable.Contains(ext)) m.Items.Add(Item("Run as administrator", "", "", () => RunAsAdmin(first.Path)));
        }
        if (p.InSearch && one)
            m.Items.Add(Item("Open file location", "", "", () => { p.Navigate(Path.GetDirectoryName(first.Path), first.Name); p.FocusList(); }));
        if (NotepadPlusPlus is { } npp && sel.Any(x => !x.IsDir))
            m.Items.Add(Item("Edit with Notepad++", "", "", () =>
                Process.Start(npp, string.Join(" ", sel.Where(x => !x.IsDir).Select(x => $"\"{x.Path}\"")))));
        m.Items.Add(Item("Quick View", "", "Space", QuickView));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Cut", "", "Ctrl+X", () => ClipboardPut(cut: true)));
        m.Items.Add(Item("Copy", "", "Ctrl+C", () => ClipboardPut(cut: false)));
        m.Items.Add(Item("Paste", "", "Ctrl+V", ClipboardPaste, Clipboard.ContainsFileDropList()));
        m.Items.Add(Item("Copy to other pane", "", "F5", () => Transfer(false)));
        m.Items.Add(Item("Move to other pane", "", "F6", () => Transfer(true)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Copy path", "", "Ctrl+Shift+C", () => Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Path)))));
        m.Items.Add(Item("Copy name", "", "", () => Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Name)))));
        m.Items.Add(one ? Item("Rename", "", "F2", Rename) : Item("Rename all…", "", "F2", RenameMany));
        m.Items.Add(Item("Delete", "", "Del", () => Delete()));
        m.Items.Add(Item("Delete permanently", "", "Shift+Del", () => Delete(permanent: true)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Compress to ZIP", "", "", Compress));
        if (one && ZipExt.Contains(ext))
        {
            m.Items.Add(Item("Extract here", "", "", () => Extract(first, toFolder: false)));
            m.Items.Add(Item($"Extract to {Path.GetFileNameWithoutExtension(first.Name)}\\", "", "", () => Extract(first, toFolder: true)));
        }
        if (one && !dir) m.Items.Add(Item("Checksum (SHA-256)", "", "", () => Checksum(first)));
        m.Items.Add(Item("Create shortcut", "", "", CreateShortcuts));
        m.Items.Add(new Separator());
        if (sel.Any(x => x.IsDir)) m.Items.Add(Item("Pin to Quick access", "", "Ctrl+D", PinSelected));
        m.Items.Add(Item("Show in File Explorer", "", "", () => Process.Start("explorer.exe", $"/select,\"{first.Path}\"")));
        m.Items.Add(Item("Windows menu…", "", "", () => { if (ShellMenu.Show(this, p.Dir, sel.Select(x => x.Path).ToList()) == "rename") Rename(); }));
        m.Items.Add(Item("Properties", "", "Alt+Enter", () => Properties(sel.Select(x => x.Path).ToList())));
        return m;
    }

    ContextMenu FolderMenu(PaneView p)
    {
        var m = new ContextMenu();
        if (undo.Count > 0) m.Items.Add(Item($"Undo {undo[^1].What}", "", "Ctrl+Z", Undo));
        m.Items.Add(Item("Paste", "", "Ctrl+V", ClipboardPaste, Clipboard.ContainsFileDropList()));
        m.Items.Add(new Separator());
        m.Items.Add(Item("New folder", "", "Ctrl+Shift+N", MkDir));
        m.Items.Add(Item("New file…", "", "Ctrl+N", NewFile));
        m.Items.Add(new Separator());
        var sort = new MenuItem
        {
            Header = $"Sort by  ·  {p.SortKey}",
            Icon = new TextBlock { Text = p.SortDescending ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 },
        };
        foreach (var item in p.SortMenuItems()) sort.Items.Add(item);
        m.Items.Add(sort);
        m.Items.Add(Item("Select by pattern…", "", "Num +", () => SelectByPattern(true)));
        m.Items.Add(Item("Invert selection", "", "Num *", p.InvertSelection));
        m.Items.Add(Item("Compare with other pane", "", "Shift+F2", ComparePanes));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", "", "Ctrl+R", () => { SizeCache.Clear(); p.Refresh(); }));
        m.Items.Add(Item(Settings.ShowHidden ? "Hide hidden files" : "Show hidden files", "", "Ctrl+H", ToggleHidden));
        m.Items.Add(Item("Pin to Quick access", "", "Ctrl+D", PinSelected));
        m.Items.Add(Item("Open terminal here", "", "Ctrl+`", OpenTerminal));
        m.Items.Add(Item("Open in File Explorer", "", "", () => Process.Start("explorer.exe", $"\"{p.Dir}\"")));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Windows menu…", "", "", () => ShellMenu.Show(this, p.Dir, Array.Empty<string>())));
        m.Items.Add(Item("Properties", "", "", () => Properties(p.Dir)));
        return m;
    }

    // Notepad++ path from its installer's registry entries (App Paths, then its own key), or null if not installed.
    static readonly Lazy<string> nppPath = new(() =>
    {
        string Reg(string key, string value = "") => Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)?.GetValue(value) as string;
        var candidates = new[]
        {
            Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad++.exe"),
            Reg(@"SOFTWARE\Notepad++") is { } dir ? Path.Combine(dir, "notepad++.exe") : null,
            Reg(@"SOFTWARE\WOW6432Node\Notepad++") is { } dir32 ? Path.Combine(dir32, "notepad++.exe") : null,
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    });
    static string NotepadPlusPlus => nppPath.Value;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, int type, string name, string page);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] static extern int SHCreateDataObject(IntPtr folder, uint count, IntPtr[] pidls, IntPtr inner, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object dataObject);
    [DllImport("shell32.dll")] static extern int SHMultiFileProperties([MarshalAs(UnmanagedType.Interface)] object dataObject, int flags);

    void Properties(string path) => Properties(new[] { path });

    [DllImport("shell32.dll")] static extern IntPtr ILFindChild(IntPtr parent, IntPtr child);

    // Several items get Explorer's combined dialog (total size, counts). The shell wants them as IDs
    // relative to one folder (relative to the desktop it answers "properties not available"), so
    // that folder is the deepest one they share — search results from several subfolders work too.
    void Properties(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1) { SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(this).Handle, 2 /* SHOP_FILEPATH */, paths[0], null); return; }
        var common = paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p).Aggregate(SharedFolder);
        if (common == null) { Status.Text = "Properties of items on different drives can't be shown together"; return; }
        var pidls = new List<IntPtr>();
        try
        {
            if (SHParseDisplayName(common, IntPtr.Zero, out var folder, 0, out _) != 0) return;
            pidls.Add(folder);
            var children = new List<IntPtr>();
            foreach (var p in paths)
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) { pidls.Add(pidl); children.Add(ILFindChild(folder, pidl)); }
            var iid = new Guid("0000010e-0000-0000-C000-000000000046"); // IDataObject
            if (children.Count > 0 && SHCreateDataObject(folder, (uint)children.Count, children.ToArray(), IntPtr.Zero, ref iid, out var data) == 0)
                SHMultiFileProperties(data, 0);
        }
        finally { foreach (var p in pidls) Marshal.FreeCoTaskMem(p); }
    }

    /// The deepest folder containing both, or null when they're on different drives.
    static string SharedFolder(string a, string b)
    {
        if (a == null || b == null) return null;
        for (var d = a; d != null; d = Path.GetDirectoryName(d))
            if (b.Equals(d, StringComparison.OrdinalIgnoreCase) || b.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }
}
