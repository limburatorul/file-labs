using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileExplorer;

// The real Windows context menu (Explorer's "Show more options" menu, with shell extensions:
// 7-Zip, Send to, Open with, ...). IShellFolder.GetUIObjectOf → IContextMenu → TrackPopupMenuEx.
// Owner-drawn submenus (Send to, Open with) only fill in if their messages are forwarded to
// IContextMenu2/3, hence the window hook while the menu is open.
public static class ShellMenu
{
    /// Shows the menu for items (all in one folder) or, with none, the folder's background menu.
    /// Returns the verb the user picked when File Labs should handle it itself (e.g. "rename"), else null.
    /// `view`: File Labs' View modes, as a View submenu at the top of the folder's menu, where Explorer has its own
    /// (the folder's menu from the shell has none: Explorer adds it from its window).
    /// `rename`: false where File Labs has no in-place rename to offer (the sidebar's drives and folders), so Windows
    /// leaves Rename out instead of showing an item that does nothing.
    /// `top`: File Labs' own commands above Windows' (a Quick access folder's Move up, Move down and Remove).
    public static string Show(Window owner, string folder, IReadOnlyList<string> items, IReadOnlyList<(string Text, bool On, Action Run)> view = null, bool rename = true,
        IReadOnlyList<(string Text, Action Run)> top = null)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidls = new List<IntPtr>();
        IntPtr menu = IntPtr.Zero;
        IContextMenu ctx = null;
        HwndSource source = null;
        HwndSourceHook hook = null;
        try
        {
            ctx = items.Count > 0 ? ItemsMenu(hwnd, items, pidls) : BackgroundMenu(hwnd, folder, pidls);
            if (ctx == null) return null;

            menu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | (rename ? CMF_CANRENAME : 0);
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) flags |= CMF_EXTENDEDVERBS;
            ctx.QueryContextMenu(menu, 0, FirstId, LastId, flags);
            var own = new List<Action>(); // File Labs' own items: ids OwnFirst, OwnFirst + 1, ... past the shell's range
            if (view != null)
            {
                var sub = CreatePopupMenu(); // destroyed with the menu
                foreach (var (text, on, run) in view) { AppendMenu(sub, on ? MF_CHECKED : 0, (UIntPtr)(OwnFirst + own.Count), text); own.Add(run); }
                InsertMenu(menu, 0, MF_BYPOSITION | MF_POPUP, (UIntPtr)(ulong)sub, "&View");
                InsertMenu(menu, 1, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
            }
            if (top != null)
            {
                for (int i = 0; i < top.Count; i++) { InsertMenu(menu, i, MF_BYPOSITION, (UIntPtr)(OwnFirst + own.Count), top[i].Text); own.Add(top[i].Run); }
                InsertMenu(menu, top.Count, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
            }

            var ctx2 = ctx as IContextMenu2;
            var ctx3 = ctx as IContextMenu3;
            source = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (ctx3 != null && ctx3.HandleMenuMsg2((uint)msg, w, l, out var result) == 0) { handled = true; return result; }
                    if (ctx2 != null && ctx2.HandleMenuMsg((uint)msg, w, l) == 0) { handled = true; return IntPtr.Zero; }
                }
                return IntPtr.Zero;
            };
            source.AddHook(hook);

            GetCursorPos(out var pt);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (cmd >= OwnFirst && cmd < OwnFirst + own.Count) { own[cmd - (int)OwnFirst](); return null; }
            if (cmd < FirstId) return null; // dismissed

            var verb = Verb(ctx, (uint)(cmd - FirstId));
            if (verb is "rename") return verb; // needs an in-place editor, which is ours
            Invoke(owner, ctx, cmd - (int)FirstId, folder, items, pt);
            return null;
        }
        catch (COMException) { return null; } // a broken shell extension shouldn't take the app down
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (ctx != null) Marshal.ReleaseComObject(ctx);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    /// Runs the command `offset` of a menu the shell (or one extension) filled, for these items, as Explorer does.
    static void Invoke(Window owner, IContextMenu ctx, int offset, string folder, IReadOnlyList<string> items, POINT pt)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
            hwnd = new WindowInteropHelper(owner).Handle,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            lpDirectoryW = folder,
            nShow = 1, // SW_SHOWNORMAL
            ptInvoke = pt,
        };
        // The app a command starts (Open with → Notepad) has to take the foreground, which Windows grants only
        // when the foreground process allows it. A packaged app (Notepad on Windows 11) is started, or an open one
        // given the file, by other processes on our behalf; a report had Notepad opening behind File Labs. The
        // user just picked the command, so the right to come forward is passed on to whoever needs it.
        AllowSetForegroundWindow(ASFW_ANY);
        var before = items.Count > 0 ? Titles() : null;
        ctx.InvokeCommand(ref info);
        if (before != null) BringForward(owner, items, before);
    }

    static IContextMenu ItemsMenu(IntPtr hwnd, IReadOnlyList<string> items, List<IntPtr> pidls) =>
        (IContextMenu)ItemsObject(hwnd, items, pidls, typeof(IContextMenu).GUID);

    /// The shell's object `iid` (a menu, a data object) for items that are all in one folder.
    static object ItemsObject(IntPtr hwnd, IReadOnlyList<string> items, List<IntPtr> pidls, Guid iid)
    {
        IShellFolder parent = null;
        var children = new IntPtr[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (SHParseDisplayName(items[i], IntPtr.Zero, out var pidl, 0, out _) != 0) return null;
            pidls.Add(pidl);
            if (SHBindToParent(pidl, typeof(IShellFolder).GUID, out var p, out children[i]) != 0) return null;
            if (parent == null) parent = (IShellFolder)p; else Marshal.ReleaseComObject(p);
        }
        parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iid, IntPtr.Zero, out var obj);
        Marshal.ReleaseComObject(parent);
        return obj;
    }

    // ---- some extensions' own commands, in File Labs' menu (1.0.50) ----
    // Explorer's whole menu takes 0.5–1.4 s to build here (every extension on the PC loads), so File Labs asks only the
    // ones it shows, each on its own, the way Explorer itself calls an extension: IShellExtInit.Initialize with the
    // selection, then QueryContextMenu into a menu of its own, so everything in that menu is that extension's. Picking
    // one runs the extension's own InvokeCommand, through the same path as the Windows menu, so it behaves exactly as
    // in Explorer and follows the program's own settings. The extensions are found where their installers register them.

    /// A command of an extension: Text null is a separator; Run null with Children is a submenu.
    public sealed record Node(string Text, string Gesture, ImageSource Icon, IReadOnlyList<Node> Children, Action Run);

    /// What each extension added for the selection, by name ("openwith", "7-zip", "winrar", "drive"); an extension that is
    /// not installed, failed, or had nothing for these items is absent. Holds the extensions until disposed, which the menu
    /// does when it closes (a command still running is let finish first).
    public sealed class Extensions : IDisposable
    {
        public readonly Dictionary<string, IReadOnlyList<Node>> Found = new();
        internal readonly List<object> Held = new();
        internal readonly List<IntPtr> Pidls = new(), Menus = new();
        int busy;
        bool disposed;

        internal void Run(Action command)
        {
            if (disposed) return;
            busy++;
            try { command(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { } // a broken extension shouldn't take the app down
            finally { if (--busy == 0 && disposed) Free(); }
        }

        public void Dispose() { if (disposed) return; disposed = true; if (busy == 0) Free(); }

        void Free()
        {
            foreach (var m in Menus) DestroyMenu(m);
            foreach (var o in Held) Marshal.ReleaseComObject(o);
            foreach (var p in Pidls) Marshal.FreeCoTaskMem(p);
            Menus.Clear(); Held.Clear(); Pidls.Clear();
        }
    }

    // name → the key its installer writes under <class>\shellex\ContextMenuHandlers (Google Drive's carries its version:
    // "DriveFS 28 or later"), so a prefix
    static readonly (string Name, string Key)[] Wanted = { ("openwith", "Open With"), ("7-zip", "7-Zip"), ("winrar", "WinRAR"), ("drive", "DriveFS") };
    // WinRAR 7 on Windows 11 by default adds itself only to the new menu, through its sparse package
    // (WinRAR.ShellExtension: an IExplorerCommand with this CLSID, RarExt.dll); the classic handler is there only when
    // its "Legacy context menu" option is on.
    static readonly Guid WinRarCommand = new("B41DB860-64E4-11D2-9906-E49FADC173CA");

    public static Extensions LoadExtensions(Window owner, IReadOnlyList<string> items)
    {
        var found = new Extensions();
        // One parent for all (search results from several folders get none, like the Windows menu). Not on a network
        // drive: the extensions read the files on the UI thread, and a share that is asleep would freeze the window
        // (1.0.49 fixed that for backups); the Windows menu… item still has them there.
        // ponytail: network items get none; asking on an STA thread of their own would bring them back if missed
        // nor for a big selection: every item is parsed and bound here, on the UI thread, before the menu shows
        // ponytail: over 50 items get none (hundreds froze the right-click); the Windows menu… item still has them
        if (items.Count > 50) return found;
        if (items.Count == 0 || items.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 || PaneView.IsRemote(items[0])) return found;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var folder = Path.GetDirectoryName(items[0]);
        bool files = items.Any(File.Exists), dirs = items.Any(Directory.Exists);
        System.Runtime.InteropServices.ComTypes.IDataObject data = null;
        foreach (var (name, key) in Wanted)
        {
            // registered for every kind in the selection: files under *, folders under Directory
            var clsid = Handler(files ? "*" : "Directory", key);
            if (files && dirs && Handler("Directory", key) != clsid) clsid = null;
            try
            {
                if (clsid == null)
                {
                    if (name == "winrar" && Explorer(owner, items, WinRarCommand, found) is { Count: > 0 } rar) found.Found[name] = rar;
                    continue;
                }
                if (data == null)
                {
                    data = (System.Runtime.InteropServices.ComTypes.IDataObject)ItemsObject(hwnd, items, found.Pidls, typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID);
                    if (data == null) return found;
                    found.Held.Add(data);
                }
                var clsidValue = clsid.Value;
                var iid = typeof(IContextMenu).GUID;
                if (CoCreateInstance(ref clsidValue, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj) != 0) continue;
                found.Held.Add(obj);
                // no folder: that is how a drop target is told, and 7-Zip then leaves out its email commands
                if (obj is not IShellExtInit init || init.Initialize(IntPtr.Zero, data, IntPtr.Zero) != 0) continue;
                var ctx = (IContextMenu)obj;
                var menu = CreatePopupMenu();
                found.Menus.Add(menu);
                if (ctx.QueryContextMenu(menu, 0, FirstId, LastId, CMF_NORMAL | CMF_EXPLORE) < 0) continue;
                if (Nodes(owner, ctx, menu, folder, items, found) is { Count: > 0 } nodes) found.Found[name] = nodes;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { } // one broken extension leaves the others
        }
        return found;
    }

    /// The CLSID of the context-menu extension registered as `key` (or a key starting with it) for `cls`, or null.
    static Guid? Handler(string cls, string key)
    {
        using var handlers = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(cls + @"\shellex\ContextMenuHandlers");
        if (handlers == null) return null;
        foreach (var sub in handlers.GetSubKeyNames().Where(n => n.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
        {
            using var k = handlers.OpenSubKey(sub);
            if (Guid.TryParse(k?.GetValue("") as string, out var g)) return g;
        }
        return null;
    }

    /// A menu an extension filled, as nodes; its submenus are filled first (Open with fills its list only then).
    static List<Node> Nodes(Window owner, IContextMenu ctx, IntPtr menu, string folder, IReadOnlyList<string> items, Extensions found)
    {
        var nodes = new List<Node>();
        int count = GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            var text = Marshal.AllocHGlobal(1024);
            try
            {
                var info = new MENUITEMINFO
                {
                    cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_CHECKMARKS | MIIM_STRING | MIIM_FTYPE | MIIM_BITMAP,
                    dwTypeData = text, cch = 511,
                };
                if (!GetMenuItemInfo(menu, (uint)i, true, ref info)) continue;
                if ((info.fType & MFT_SEPARATOR) != 0) { nodes.Add(new Node(null, null, null, null, null)); continue; }
                if ((info.fState & MFS_DISABLED) != 0 || info.cch == 0) continue; // nothing to run, or owner-drawn without text
                var (label, gesture) = Label(Marshal.PtrToStringUni(text));
                var icon = MenuIcon(menu, i);
                if (info.hSubMenu != IntPtr.Zero)
                {
                    if (ctx is IContextMenu3 c3) c3.HandleMenuMsg2((uint)WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i, out _);
                    else if (ctx is IContextMenu2 c2) c2.HandleMenuMsg((uint)WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i);
                    if (Nodes(owner, ctx, info.hSubMenu, folder, items, found) is { Count: > 0 } sub) nodes.Add(new Node(label, gesture, icon, sub, null));
                    continue;
                }
                if (info.wID < FirstId || info.wID > LastId) continue;
                int offset = (int)(info.wID - FirstId);
                nodes.Add(new Node(label, gesture, icon, null, () => found.Run(() => { GetCursorPos(out var pt); Invoke(owner, ctx, offset, folder, items, pt); })));
            }
            finally { Marshal.FreeHGlobal(text); }
        }
        return Tidy(nodes);
    }

    /// The picture beside a menu item (its bitmap, else its unchecked mark), or null.
    static ImageSource MenuIcon(IntPtr menu, int position)
    {
        var info = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_BITMAP | MIIM_CHECKMARKS };
        if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) return null;
        var bmp = Bitmap(info.hbmpItem) ? info.hbmpItem : info.hbmpUnchecked;
        return Bitmap(bmp) ? Thumbnails.ToBitmapSource(bmp) : null;
    }

    // -1 and 1..11 are HBMMENU_ placeholders; a real handle may come back sign-extended, so negative
    static bool Bitmap(IntPtr h) => (long)h is not (0 or -1 or (>= 1 and <= 11));

    /// An IExplorerCommand (Windows 11's menu) and its subcommands as nodes, or null when it can't be created.
    static List<Node> Explorer(Window owner, IReadOnlyList<string> items, Guid clsid, Extensions found)
    {
        using (var registered = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InProcServer32"))
            if (registered == null) return null;
        var iid = typeof(IExplorerCommand).GUID;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj) != 0) return null;
        found.Held.Add(obj);
        var pidls = new IntPtr[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (SHParseDisplayName(items[i], IntPtr.Zero, out pidls[i], 0, out _) != 0) return null;
            found.Pidls.Add(pidls[i]);
        }
        if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0) return null;
        found.Held.Add(array);
        return Command(owner, (IExplorerCommand)obj, array, items, found) is { Text: not null } node ? new List<Node> { node } : null;
    }

    static Node Command(Window owner, IExplorerCommand cmd, IShellItemArray array, IReadOnlyList<string> items, Extensions found)
    {
        if (cmd.GetFlags(out var flags) != 0) flags = 0;
        if ((flags & ECF_ISSEPARATOR) != 0) return new Node(null, null, null, null, null);
        if (cmd.GetState(array, true, out var state) != 0 || (state & (ECS_HIDDEN | ECS_DISABLED)) != 0) return null;
        if (cmd.GetTitle(array, out var title) != 0 || string.IsNullOrEmpty(title)) return null;
        var (label, gesture) = Label(title);
        var icon = cmd.GetIcon(array, out var location) == 0 ? IconAt(location) : null;
        if ((flags & ECF_HASSUBCOMMANDS) != 0)
        {
            var sub = new List<Node>();
            if (cmd.EnumSubCommands(out var e) == 0)
            {
                found.Held.Add(e);
                while (e.Next(1, out var child, out var n) == 0 && n == 1)
                {
                    found.Held.Add(child);
                    if (Command(owner, child, array, items, found) is { } node) sub.Add(node);
                }
            }
            return Tidy(sub) is { Count: > 0 } kids ? new Node(label, gesture, icon, kids, null) : null;
        }
        return new Node(label, gesture, icon, null, () => found.Run(() =>
        {
            AllowSetForegroundWindow(ASFW_ANY); // as Invoke: the program it starts may come forward
            var before = Titles();
            cmd.Invoke(array, IntPtr.Zero);
            BringForward(owner, items, before);
        }));
    }

    /// "C:\x\RarExt.dll,0" (or ",-101", a resource id) as a picture.
    static ImageSource IconAt(string location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        int comma = location.LastIndexOf(',');
        var file = comma > 0 ? location[..comma] : location;
        int index = comma > 0 && int.TryParse(location[(comma + 1)..], out var n) ? n : 0;
        if (ExtractIconEx(Environment.ExpandEnvironmentVariables(file.Trim('"')), index, out var large, out var small, 1) == 0) return null;
        try
        {
            var handle = large != IntPtr.Zero ? large : small;
            if (handle == IntPtr.Zero) return null;
            var img = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
    }

    /// A menu's "&Add to archive...\tCtrl+A" as WPF's "_Add to archive..." and its gesture text.
    internal static (string Label, string Gesture) Label(string text)
    {
        var parts = text.Split('\t', 2);
        var label = parts[0].Replace("_", "__").Replace("&&", "\0").Replace("&", "_").Replace("\0", "&");
        return (label, parts.Length > 1 ? parts[1] : null);
    }

    /// Without separators at either end or two in a row (hidden commands leave them behind).
    internal static List<Node> Tidy(List<Node> nodes)
    {
        var tidy = new List<Node>();
        foreach (var n in nodes)
            if (n.Text != null || (tidy.Count > 0 && tidy[^1].Text != null)) tidy.Add(n);
        if (tidy.Count > 0 && tidy[^1].Text == null) tidy.RemoveAt(tidy.Count - 1);
        return tidy;
    }

    static IContextMenu BackgroundMenu(IntPtr hwnd, string folder, List<IntPtr> pidls)
    {
        if (SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) != 0) return null;
        pidls.Add(pidl);
        var iidFolder = typeof(IShellFolder).GUID;
        IShellFolder shellFolder;
        if (SHBindToParent(pidl, iidFolder, out var parentObj, out var child) != 0) return null;
        var parent = (IShellFolder)parentObj; // for a drive root this is "This PC", child is the drive
        if (parent.BindToObject(child, IntPtr.Zero, ref iidFolder, out var f) != 0) { Marshal.ReleaseComObject(parent); return null; }
        Marshal.ReleaseComObject(parent);
        shellFolder = (IShellFolder)f;
        var iid = typeof(IContextMenu).GUID;
        shellFolder.CreateViewObject(hwnd, ref iid, out var obj);
        Marshal.ReleaseComObject(shellFolder);
        return (IContextMenu)obj;
    }

    /// Still not enough for "Open with -> Notepad" when Notepad was already open, minimized or behind File Labs (1.0.39
    /// report; "Open" at the top was fine): the handler gives it the file after the menu has returned, Notepad asks to
    /// come forward, Windows refuses and flashes its taskbar button instead (1.0.48 report: "highlighted but not brought
    /// to the front"). So for 3 s, as long as the window that was in front when the command ran still is (the user
    /// hasn't moved on), File Labs does it for the app: a window of another process that flashes, or whose title has
    /// just changed to name one of these files, is restored and brought forward. File Labs, in front, is allowed to.
    /// 1.0.42 only watched titles, and a Notepad that already showed the file, or named it a moment later, slipped by.
    static void BringForward(Window owner, IReadOnlyList<string> items, Dictionary<IntPtr, string> before)
    {
        var names = items.Select(Path.GetFileName).Where(n => n != "").ToList();
        long until = Environment.TickCount64 + 3000;
        var front = GetForegroundWindow();
        var hwnd = new WindowInteropHelper(owner).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        uint shellMsg = RegisterWindowMessage("SHELLHOOK");
        var poll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        HwndSourceHook flash = null;
        bool done = false;
        void Stop()
        {
            if (done) return;
            done = true;
            poll.Stop();
            DeregisterShellHookWindow(hwnd);
            source?.RemoveHook(flash);
        }
        void Bring(IntPtr h)
        {
            Stop();
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }
        // Windows tells shell hook windows (the taskbar, and this one for these 3 s) which window it flashes
        flash = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == shellMsg && (int)w == HSHELL_FLASH && GetForegroundWindow() == front
                && GetWindowThreadProcessId(l, out uint pid) != 0 && pid != Environment.ProcessId)
                Bring(l);
            return IntPtr.Zero;
        };
        source?.AddHook(flash);
        RegisterShellHookWindow(hwnd);
        poll.Tick += (_, _) =>
        {
            if (Environment.TickCount64 > until || GetForegroundWindow() != front) { Stop(); return; } // it came forward, or the user went on
            foreach (var (h, title) in Titles())
                if ((!before.TryGetValue(h, out var was) || was != title) && ShowsFile(title, names)) { Bring(h); return; }
        };
        poll.Start();
    }

    internal static bool ShowsFile(string title, IEnumerable<string> names) => names.Any(n => title.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// The titles of the visible top-level windows of other processes.
    static Dictionary<IntPtr, string> Titles()
    {
        var titles = new Dictionary<IntPtr, string>();
        var text = new System.Text.StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out uint pid) != 0 && pid != Environment.ProcessId && GetWindowText(h, text, text.Capacity) > 0)
                titles[h] = text.ToString();
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    /// Lets this menu follow Windows' light or dark mode, as Frostpane does. It is a classic Win32 menu, drawn by
    /// uxtheme, and an app gets dark ones only by asking, through SetPreferredAppMode (ordinal 135; 1 = follow the
    /// system), as Explorer and Notepad do. Undocumented, but the same since Windows 10 1903; where it is missing
    /// the menu simply stays light.
    public static void FollowSystemTheme()
    {
        try { SetPreferredAppMode(1); FlushMenuThemes(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    static string Verb(IContextMenu ctx, uint id)
    {
        var buf = new System.Text.StringBuilder(256);
        return ctx.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, buf, (uint)buf.Capacity) == 0 ? buf.ToString() : null;
    }

    // ---- interop ----
    const uint FirstId = 1, LastId = 0x7FFF, OwnFirst = 0x8000;
    const uint MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_BYPOSITION = 0x400, MF_SEPARATOR = 0x800;
    const int SW_RESTORE = 9, HSHELL_FLASH = 0x8006; // HSHELL_REDRAW | HSHELL_HIGHBIT
    const int ASFW_ANY = -1;
    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    const uint GCS_VERBW = 4, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_CHECKMARKS = 0x8, MIIM_STRING = 0x40, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
    const uint MFT_SEPARATOR = 0x800, MFS_DISABLED = 0x3, CLSCTX_INPROC_SERVER = 0x1;
    const uint ECF_HASSUBCOMMANDS = 0x1, ECF_ISSEPARATOR = 0x8, ECS_DISABLED = 0x1, ECS_HIDDEN = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, System.Runtime.InteropServices.ComTypes.IDataObject data, IntPtr hkeyProgId);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray { }

    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
        [PreserveSig] int GetIcon(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
        [PreserveSig] int GetToolTip(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string tip);
        [PreserveSig] int GetCanonicalName(out Guid guid);
        [PreserveSig] int GetState(IShellItemArray items, [MarshalAs(UnmanagedType.Bool)] bool slowOk, out uint state);
        [PreserveSig] int Invoke(IShellItemArray items, IntPtr bindCtx);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand e);
    }

    [ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumExplorerCommand
    {
        [PreserveSig] int Next(uint count, out IExplorerCommand command, out uint fetched);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize, fMask;
        public IntPtr hwnd, lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters, lpDirectory;
        public int nShow, dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        public string lpParametersW, lpDirectoryW, lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attrs);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attrs);
        void GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr w, IntPtr l, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr pidlLast);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    [DllImport("shell32.dll")] static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IShellItemArray array);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool InsertMenu(IntPtr menu, int position, uint flags, UIntPtr id, string text);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int max);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}
