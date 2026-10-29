using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
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
            // right-click on empty space (in Details also right of the columns) = the folder itself, not
            // whatever is still selected
            var row = p.RowAt(e.OriginalSource as DependencyObject);
            if (row == null && e.CursorLeft >= 0) p.List.SelectedItems.Clear();
            e.Handled = true;
            ShowMenu(p);
        };
        p.RightDragSelected += () => { SetActive(p); ShowMenu(p); };
    }

    /// The menu for the pane's selection, or for its folder when nothing is selected.
    async void ShowMenu(PaneView p)
    {
        // This PC: Windows' menu for the drive (Properties, Eject, Format…), or for This PC itself (Map network drive…)
        if (p.Dir == PaneView.ThisPC)
        {
            var drive = p.List.SelectedItem as Entry;
            // Explorer adds these two to This PC's menu from its own window, so the shell's menu has neither: Windows' dialogs
            void Net(bool map)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if ((map ? WNetConnectionDialog(hwnd, 1) : WNetDisconnectDialog(hwnd, 1)) == 0) { RefreshDrives(); p.Refresh(); } // RESOURCETYPE_DISK
            }
            (string, Action)[] net = { ("&Map network drive…", () => Net(true)), ("&Disconnect network drive…", () => Net(false)) };
            ShellMenu.Show(this, PaneView.ThisPC, drive != null ? new[] { drive.Path } : Array.Empty<string>(), rename: false, top: drive == null ? net : null);
            p.Refresh();
            return;
        }
        if (p.ArchiveRoot is var (archive, _))
        {
            var rows = ArchiveSelection(p);
            var am = new ContextMenu();
            if (rows.Count == 1) am.Items.Add(Item("Open", "", "Enter", p.OpenSelected));
            if (rows.Count > 0)
            {
                am.Items.Add(Item("Extract to other pane", "", "Ctrl+F5", () => ExtractFromArchive(p, Other.Dir), !Settings.SinglePane && !PaneView.IsVirtual(Other.Dir)));
                am.Items.Add(Item("Extract to…", "", "", () => ExtractFromArchiveTo(p)));
                if (Archive.CanChange(archive)) am.Items.Add(Item("Delete from the ZIP", "", "Del", () => DeleteFromArchive(p)));
            }
            else am.Items.Add(Item("Paste into the ZIP", "", "Ctrl+V", () => ClipboardPaste(), Archive.CanChange(archive) && Clipboard.ContainsFileDropList()));
            am.Items.Add(new Separator());
            am.Items.Add(Item("Show the archive", "", "", () => p.Navigate(Path.GetDirectoryName(archive), Path.GetFileName(archive))));
            am.PlacementTarget = p.List;
            am.IsOpen = true;
            return;
        }
        var sel = p.Selected;
        if (Settings.NativeMenu)
        {
            // the folder's menu gets File Labs' View submenu; Paste on a folder, or a shortcut to one, is Windows' own here
            if (ShellMenu.Show(this, p.Dir, sel.Select(x => x.Path).ToList(), sel.Count == 0 ? p.ViewChoices() : null) == "rename") Rename();
            p.Refresh();
            return;
        }
        // A file named like a Stash or Backup Labs backup is looked into off the UI thread (it may be on a share that is
        // asleep); the menu waits for that at most 150 ms, then opens without "Open in …". Other files cost nothing here.
        Backups.Info backup = null;
        if (sel.Count == 1 && !sel[0].IsDir && Backups.Named(sel[0].Path))
        {
            var path = sel[0].Path;
            var look = Task.Run(() => Backups.Inspect(path) is { Unreadable: false } b && BackupStart(b.Id, path) == null ? b : null);
            if (await Task.WhenAny(look, Task.Delay(150)) == look) backup = look.Result;
        }
        var menu = sel.Count > 0 ? ItemMenu(p, sel, backup) : FolderMenu(p);
        menu.PlacementTarget = p.List;
        menu.IsOpen = true;
    }

    // SpaceScan (sister app) publishes where it is installed (before its ecosystem card); without it the menu item sends the user to its page.
    const string SpaceScanPage = "https://protagonistlabs.app/spacescan/?utm_source=filelabs&utm_medium=app&utm_campaign=scan-with-spacescan";

    static string SpaceScanExe
    {
        get
        {
            var exe = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\SpaceScan", "ExePath", null) as string;
            return exe != null && File.Exists(exe) ? exe : null;
        }
    }

    /// A sister app's right-click item: always, or (Settings → Files) only once that app is installed.
    static bool Offer(string id) => Settings.MissingApps || SisterExe(id) != null;

    static void ScanWithSpaceScan(string path) => HandTo("spacescan", SpaceScanPage, new[] { path });

    // Shelf, Reel and Backup Labs (sister apps) take folders and files on the command line, already
    // running or not: Shelf (3.59.0) and Reel (1.9.11) offer to add them, Backup Labs (1.0.2) makes a
    // new job of them. Each is found through its card, else the uninstall entry its installer writes; without either
    // the menu item sends the user to the app's page.
    const string ShelfKey = "93869666-a03d-5775-ba3a-19dde48e73bc", ReelKey = "95693626-e8c4-5132-9e4f-6f4a12169be6";
    const string BackupKey = "{3B9E6A12-8C4D-4F7B-A1E5-6D2F0B8C9E34}_is1";
    const string BackupPage = "https://protagonistlabs.app/backuplabs/?utm_source=filelabs&utm_medium=app&utm_campaign=back-up-with-backuplabs";
    const string ShelfPage = "https://protagonistlabs.app/shelf/?utm_source=filelabs&utm_medium=app&utm_campaign=add-to-shelf";
    const string ReelPage = "https://protagonistlabs.app/reel/?utm_source=filelabs&utm_medium=app&utm_campaign=add-to-reel";
    // the extensions Reel takes as a film
    static readonly HashSet<string> ReelVideo = Set(".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm");

    /// The exe an uninstall entry's DisplayIcon names ("C:\...\Shelf.exe,0"), or null when it is not on disk.
    internal static string ExeOf(string displayIcon)
    {
        var exe = displayIcon?.Split(',')[0].Trim('"');
        return exe != null && File.Exists(exe) ? exe : null;
    }

    /// A sister's exe: its ecosystem card first (Sisters.cs), else what older versions left in the registry; null = not installed.
    internal static string SisterExe(string id) => Sisters.Exe(id) ?? id switch
    {
        "spacescan" => SpaceScanExe ?? Uninstalled(SpaceScanKey),
        "shelf" => Uninstalled(ShelfKey),
        "reel" => Uninstalled(ReelKey),
        "backuplabs" => Uninstalled(BackupKey),
        _ => null,
    };
    const string SpaceScanKey = "{5B7F3C21-9D4A-4E86-B0C7-2A9E6F1D8B53}_is1";

    // Since ecosystem v3 the studio's apps install per machine (Program Files\ProtagonistLabs), so their uninstall entry is
    // in HKLM, in the 64-bit view (Inno in 64-bit mode) or the 32-bit one (an NSIS installer); a per-user install in HKCU.
    static string Uninstalled(string key)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var entry = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + key);
            if (ExeOf(entry?.GetValue("DisplayIcon") as string) is { } exe) return exe;
        }
        return null;
    }

    static void HandTo(string id, string page, IEnumerable<string> paths)
    {
        var exe = SisterExe(id);
        if (exe == null) { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); return; }
        // ArgumentList, not a quoted string: a drive root ends in a backslash, which would escape the closing quote
        var start = new ProcessStartInfo(exe);
        foreach (var p in paths) start.ArgumentList.Add(p);
        Process.Start(start);
    }

    // Stash (2.1) and Backup Labs (1.3.0) open one of their backups (Backups.cs) given `--browse` / `--open` and its
    // full path. Older versions don't know the switch (Backup Labs would make a bare path a new job), so a version
    // below these counts as not installed and the button opens the app's page.
    static readonly Dictionary<string, (string Switch, Version Min, string Page)> BackupApps = new()
    {
        ["stash"] = ("--browse", new(2, 1, 0), "https://protagonistlabs.app/stash/?utm_source=filelabs&utm_medium=app&utm_campaign=open-backup"),
        ["backuplabs"] = ("--open", new(1, 3, 0), "https://protagonistlabs.app/backuplabs/?utm_source=filelabs&utm_medium=app&utm_campaign=open-backup"),
    };

    /// The command that opens this backup in its app, or null when it isn't one, can't be read, or the app is missing or too old.
    internal static ProcessStartInfo BackupStart(string path) =>
        Backups.Inspect(path) is { Unreadable: false } b && BackupStart(b.Id, path) is { } start ? start : null;

    static ProcessStartInfo BackupStart(string id, string path)
    {
        if (SisterExe(id) is not { } exe || !Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) || v < BackupApps[id].Min) return null;
        var start = new ProcessStartInfo(exe);
        start.ArgumentList.Add(BackupApps[id].Switch);
        start.ArgumentList.Add(path);
        return start;
    }

    static void OpenBackup(string id, string path) =>
        Process.Start(BackupStart(id, path) ?? new ProcessStartInfo(BackupApps[id].Page) { UseShellExecute = true });

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

    /// `backup`: the selected file is a backup whose app isn't installed (ShowMenu), so the menu leads to it.
    ContextMenu ItemMenu(PaneView p, List<Entry> sel, Backups.Info backup)
    {
        var first = sel[0];
        bool one = sel.Count == 1, dir = one && first.IsDir;
        var ext = Path.GetExtension(first.Name);
        var m = new ContextMenu();
        // Open with, 7-Zip, WinRAR and Google Drive: their own commands, as in Explorer (ShellMenu.LoadExtensions)
        var shell = ShellMenu.LoadExtensions(this, sel.Select(x => x.Path).ToList());
        m.Closed += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, shell.Dispose); // after a Click
        m.Items.Add(Item("Open", "", "Enter", () => p.OpenSelected()));
        // with Stash / Backup Labs installed, Open already opens their backups there; without, this leads to them
        if (backup != null)
            m.Items.Add(Item($"Open in {backup.App}", "", "", () => OpenBackup(backup.Id, first.Path)));
        if (dir)
        {
            m.Items.Add(Item("Open in new tab", "", "Middle click", () => { p.NewTab(); p.Navigate(first.Path); }));
            m.Items.Add(Item("Open in other pane", "", "", () => Other.Navigate(first.Path)));
            m.Items.Add(Item("Find duplicates in folder…", "", "", () => FindDuplicates(first.Path)));
        }
        else if (sel.Count(x => x.FolderTarget != null) > 1) // several folders: compared with each other
            m.Items.Add(Item("Find duplicates in these folders…", "", "", () => FindDuplicates(sel.Where(x => x.FolderTarget != null).Select(x => x.FolderTarget).ToArray())));
        // Windows' Open with list (its apps in Windows' order, Choose another app, the Store), for several files too
        if (!AddShell(m, shell, "openwith") && one && !dir)
            m.Items.Add(Item("Open with…", "", "", () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {first.Path}")));
        if (one && !dir)
        {
            if (Runnable.Contains(ext)) m.Items.Add(Item("Run as administrator", "", "", () => RunAsAdmin(first.Path)));
        }
        if (p.InSearch && one)
            m.Items.Add(Item("Open file location", "", "", () => { p.Navigate(Path.GetDirectoryName(first.Path), first.Name); p.FocusList(); }));
        if (sel.Any(x => !x.IsDir)) m.Items.Add(Item("Edit", "", "F4", EditSelected));
        if (sel.Count == 2 && sel.All(x => !x.IsDir)) m.Items.Add(Item("Compare in editor", "", "", () => EditorWindow.Compare(sel[0].Path, sel[1].Path)));
        if (NotepadPlusPlus is { } npp && sel.Any(x => !x.IsDir))
            m.Items.Add(Item("Edit with Notepad++", "", "", () =>
                Process.Start(npp, string.Join(" ", sel.Where(x => !x.IsDir).Select(x => $"\"{x.Path}\"")))));
        m.Items.Add(Item("Quick View", "", "Space", QuickView));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Cut", "", "Ctrl+X", () => ClipboardPut(cut: true)));
        m.Items.Add(Item("Copy", "", "Ctrl+C", () => ClipboardPut(cut: false)));
        // on a folder, or a shortcut to one, Paste goes into it (Explorer's rule); on a file, into the folder shown
        var into = one ? first.FolderTarget : null;
        m.Items.Add(into != null ? Item("Paste into folder", "", "", () => ClipboardPaste(into), Clipboard.ContainsFileDropList())
                                 : Item("Paste", "", "Ctrl+V", () => ClipboardPaste(), Clipboard.ContainsFileDropList()));
        m.Items.Add(Item("Copy to other pane", "", "Ctrl+F5", () => Transfer(false)));
        m.Items.Add(Item("Move to other pane", "", "F6", () => Transfer(true)));
        m.Items.Add(SendTo("Copy to", "\uE8C8", p, sel, move: false));
        m.Items.Add(SendTo("Move to", "\uE8DE", p, sel, move: true));
        m.Items.Add(Item("Group into new folder…", "\uE8F4", "", () => GroupIntoFolder(p, sel)));
        m.Items.Add(Item("Add to Drop bin", "\uE7B8", "Ctrl+B", () => AddToBin(sel.Select(x => x.Path))));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Copy path", "", "Ctrl+Shift+C", () => Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Path)))));
        m.Items.Add(Item("Copy name", "", "", () => Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Name)))));
        m.Items.Add(one ? Item("Rename", "", "F2", Rename) : Item("Rename all…", "", "F2", RenameMany));
        m.Items.Add(Item("Delete", "", "Del", () => Delete()));
        m.Items.Add(Item("Delete permanently", "", "Shift+Del", () => Delete(permanent: true)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Compress to ZIP", "", "", Compress));
        if (one && (ZipExt.Contains(ext) || Archive.IsArchiveName(first.Path)))
        {
            m.Items.Add(Item("Extract here", "", "", () => Extract(first, toFolder: false)));
            m.Items.Add(Item($"Extract to {Path.GetFileNameWithoutExtension(first.Name)}\\", "", "", () => Extract(first, toFolder: true)));
        }
        if (one && DiskImageExt.Contains(ext)) m.Items.Add(Item("Mount", "", "", () => MountImage(first.Path)));
        AddShell(m, shell, "7-zip");
        AddShell(m, shell, "winrar");
        if (one && !dir) m.Items.Add(Item("Checksum (SHA-256)", "", "", () => Checksum(first)));
        m.Items.Add(Item("Create shortcut", "", "", CreateShortcuts));
        m.Items.Add(new Separator());
        if (sel.Any(x => x.IsDir)) m.Items.Add(Item("Pin to Quick access", "", "Ctrl+D", PinSelected));
        if (LumeItem(sel.Select(x => x.Path).ToList()) is { } lume) m.Items.Add(lume);
        m.Items.Add(Item("Show in File Explorer", "", "", () => OpenInExplorer($"/select,\"{first.Path}\"")));
        AddShell(m, shell, "drive");
        if (Offer("spacescan")) m.Items.Add(Item("Scan with SpaceScan", "", "", () => ScanWithSpaceScan(first.IsDir ? first.Path : Path.GetDirectoryName(first.Path)!)));
        if (Offer("shelf") && sel.All(x => x.IsDir || Path.GetExtension(x.Name).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            m.Items.Add(Item("Add to Shelf", "\uE7FC", "", () => HandTo("shelf", ShelfPage, sel.Select(x => x.Path))));
        if (Offer("reel") && sel.All(x => x.IsDir || ReelVideo.Contains(Path.GetExtension(x.Name))))
            m.Items.Add(Item("Add to Reel", "\uE8B2", "", () => HandTo("reel", ReelPage, sel.Select(x => x.Path))));
        if (Offer("backuplabs")) m.Items.Add(Item("Back up with Backup Labs", "\uE81C", "", () => HandTo("backuplabs", BackupPage, sel.Select(x => x.Path))));
        m.Items.Add(Item("Windows menu…", "", "", () => { if (ShellMenu.Show(this, p.Dir, sel.Select(x => x.Path).ToList()) == "rename") Rename(); }));
        m.Items.Add(Item("Properties", "", "Alt+Enter", () => Properties(sel.Select(x => x.Path).ToList())));
        return m;
    }

    /// Adds what a shell extension offered for the selection (ShellMenu.Extensions); false when it offered nothing.
    static bool AddShell(ItemsControl menu, ShellMenu.Extensions shell, string name)
    {
        if (!shell.Found.TryGetValue(name, out var nodes)) return false;
        foreach (var n in nodes) menu.Items.Add(ShellItem(n));
        return true;
    }

    static object ShellItem(ShellMenu.Node n)
    {
        if (n.Text == null) return new Separator();
        var mi = new MenuItem { Header = n.Text, InputGestureText = n.Gesture ?? "" };
        if (n.Icon != null) mi.Icon = new Image { Source = n.Icon, Width = 16, Height = 16 };
        if (n.Children != null) foreach (var c in n.Children) mi.Items.Add(ShellItem(c));
        else mi.Click += (_, _) => n.Run();
        return mi;
    }

    ContextMenu FolderMenu(PaneView p)
    {
        var m = new ContextMenu();
        if (undo.Count > 0) m.Items.Add(Item($"Undo {undo[^1].What}", "", "Ctrl+Z", Undo));
        m.Items.Add(Item("Paste", "", "Ctrl+V", () => ClipboardPaste(), Clipboard.ContainsFileDropList()));
        m.Items.Add(new Separator());
        m.Items.Add(Item("New folder", "", "Ctrl+Shift+N", MkDir));
        m.Items.Add(Item("New file…", "", "Ctrl+N", NewFile));
        m.Items.Add(new Separator());
        // View, as in Explorer's own menu for a folder (the Windows menu gets the same from ShellMenu.Show)
        var view = new MenuItem { Header = $"View  ·  {p.ViewModeName}", Icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 } };
        foreach (var item in p.ViewMenuItems()) view.Items.Add(item);
        m.Items.Add(view);
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
        m.Items.Add(Item("Find duplicates…", "", "", () => FindDuplicates(p.Dir)));
        // the other pane's folder too, when both are on screen and show different places
        var other = p == Left ? Right : Left;
        if (!Settings.SinglePane && other.Dir is { Length: > 0 } od && od != PaneView.ThisPC && !od.Equals(p.Dir, StringComparison.OrdinalIgnoreCase))
            m.Items.Add(Item("Find duplicates here and in the other pane…", "", "", () => FindDuplicates(p.Dir, od)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", "", "F5 / Ctrl+R", () => { SizeCache.Clear(); p.Refresh(force: true); }));
        m.Items.Add(Item(Settings.ShowHidden ? "Hide hidden files" : "Show hidden files", "", "Ctrl+H", ToggleHidden));
        m.Items.Add(Item("Pin to Quick access", "", "Ctrl+D", PinSelected));
        if (LumeItem(new[] { p.Dir }) is { } lume) m.Items.Add(lume);
        m.Items.Add(Item("Open terminal here", "", "Ctrl+`", OpenTerminal));
        m.Items.Add(Item("Open in File Explorer", "", "", () => OpenInExplorer($"\"{p.Dir}\"")));
        if (Offer("spacescan")) m.Items.Add(Item("Scan with SpaceScan", "", "", () => ScanWithSpaceScan(p.Dir)));
        if (Offer("backuplabs")) m.Items.Add(Item("Back up with Backup Labs", "\uE81C", "", () => HandTo("backuplabs", BackupPage, new[] { p.Dir })));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Windows menu…", "", "", () => ShellMenu.Show(this, p.Dir, Array.Empty<string>())));
        m.Items.Add(Item("Properties", "", "", () => Properties(p.Dir)));
        return m;
    }

    /// Copy to / Move to: the folders copies and moves went to lately, then any folder. The folder shown is left
    /// out (copying there duplicates, moving there does nothing).
    MenuItem SendTo(string header, string glyph, PaneView p, List<Entry> sel, bool move)
    {
        var sub = Item(header, glyph, "", () => { });
        var paths = sel.Select(x => x.Path).ToList();
        foreach (var dest in Settings.RecentDestinations.Where(d => !d.Equals(p.Dir, StringComparison.OrdinalIgnoreCase)
                                                                   && (PaneView.IsRemote(d) || Directory.Exists(d))))
        {
            var target = dest;
            var mi = Item(Path.GetFileName(dest.TrimEnd('\\')) is { Length: > 0 } n ? n : dest, "", "", () => Submit(paths, target, move));
            mi.InputGestureText = ParentForMenu(dest); // where it is, muted at the right
            mi.ToolTip = dest;
            mi.Icon = ShellIcon(dest, "\uE8B7", 16, new Thickness(0));
            sub.Items.Add(mi);
        }
        if (sub.Items.Count > 0) sub.Items.Add(new Separator());
        sub.Items.Add(Item("Choose a folder…", "\uE838", "", () =>
        {
            var pick = new Microsoft.Win32.OpenFolderDialog { Title = $"{header} which folder?", InitialDirectory = Other.Dir };
            if (pick.ShowDialog(this) == true) Submit(paths, pick.FolderName, move);
        }));
        return sub;
    }

    /// Makes a folder beside the selection and moves the selection into it (one Ferry move, undoable with Ctrl+Z);
    /// the new folder is selected afterwards. An existing name is refused rather than merged into.
    void GroupIntoFolder(PaneView p, List<Entry> sel)
    {
        var name = PromptDialog.Ask(this, "Group into new folder", $"Move the {(sel.Count == 1 ? "selected item" : $"{sel.Count} selected items")} into a new folder named", "New folder", "Group");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MessageBox.Show(this, $"A folder name can't contain {string.Join(" ", Path.GetInvalidFileNameChars().Where(c => c >= ' '))}", "Group into new folder"); return; }
        var folder = Path.Combine(p.Dir, name);
        if (Directory.Exists(folder) || File.Exists(folder)) { MessageBox.Show(this, $"“{name}” already exists here.", "Group into new folder"); return; }
        if (!Run(() => Directory.CreateDirectory(folder))) return;
        Submit(sel.Select(x => x.Path).ToList(), folder, move: true);
        p.Navigate(p.Dir, name, record: false);
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
    [DllImport("mpr.dll")] static extern int WNetConnectionDialog(IntPtr hwnd, int type);
    [DllImport("mpr.dll")] static extern int WNetDisconnectDialog(IntPtr hwnd, int type);
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
