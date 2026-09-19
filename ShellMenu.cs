using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileExplorer;

// The real Windows context menu (Explorer's "Show more options" menu, with shell extensions:
// 7-Zip, Send to, Open with, ...). IShellFolder.GetUIObjectOf → IContextMenu → TrackPopupMenuEx.
// Owner-drawn submenus (Send to, Open with) only fill in if their messages are forwarded to
// IContextMenu2/3, hence the window hook while the menu is open.
public static class ShellMenu
{
    /// Shows the menu for items (all in one folder) or, with none, the folder's background menu.
    /// Returns the verb the user picked when File Labs should handle it itself (e.g. "rename"), else null.
    public static string Show(Window owner, string folder, IReadOnlyList<string> items)
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
            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) flags |= CMF_EXTENDEDVERBS;
            ctx.QueryContextMenu(menu, 0, FirstId, LastId, flags);

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
            if (cmd < FirstId) return null; // dismissed

            var verb = Verb(ctx, (uint)(cmd - FirstId));
            if (verb is "rename") return verb; // needs an in-place editor, which is ours

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - FirstId),
                lpVerbW = (IntPtr)(cmd - FirstId),
                lpDirectoryW = folder,
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = pt,
            };
            ctx.InvokeCommand(ref info);
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

    static IContextMenu ItemsMenu(IntPtr hwnd, IReadOnlyList<string> items, List<IntPtr> pidls)
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
        var iid = typeof(IContextMenu).GUID;
        parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iid, IntPtr.Zero, out var obj);
        Marshal.ReleaseComObject(parent);
        return (IContextMenu)obj;
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

    static string Verb(IContextMenu ctx, uint id)
    {
        var buf = new System.Text.StringBuilder(256);
        return ctx.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, buf, (uint)buf.Capacity) == 0 ? buf.ToString() : null;
    }

    // ---- interop ----
    const uint FirstId = 1, LastId = 0x7FFF;
    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    const uint GCS_VERBW = 4, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

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
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
}
