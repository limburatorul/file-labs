using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileExplorer;

// Explorer's preview pane, hosted in Quick View: the preview handler registered for a file type
// (Office documents, HTML, email, and whatever else installed apps registered) draws into a child
// window of ours. The same handlers Explorer uses, so nothing extra has to be installed.
public class PreviewHost : HwndHost
{
    readonly string path;
    readonly Guid clsid;
    object handler;
    IntPtr window, parentWindow;

    PreviewHost(string path, Guid clsid) { this.path = path; this.clsid = clsid; }

    /// A host for this file, or null when no preview handler is registered for its type.
    public static PreviewHost For(string path) => HandlerFor(System.IO.Path.GetExtension(path)) is { } id ? new PreviewHost(path, id) : null;

    static Guid? HandlerFor(string ext)
    {
        if (ext == "") return null;
        var buf = new StringBuilder(64);
        uint len = (uint)buf.Capacity;
        return AssocQueryString(0, 16 /* ASSOCSTR_SHELLEXTENSION */, ext, "{8895b1c6-b41f-4c1c-a562-0d564250836f}", buf, ref len) == 0
               && Guid.TryParse(buf.ToString(), out var g) ? g : null;
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        parentWindow = parent.Handle;
        window = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(Start, System.Windows.Threading.DispatcherPriority.Loaded); // once the window has a size
        return new HandleRef(this, window);
    }

    /// Raised with a message when the handler can't show the file, so Quick View can fall back.
    public event Action<string> Failed;

    void Start()
    {
        try
        {
            // Most handlers run out of process (prevhost.exe), which keeps a crashing one away from us.
            var iid = typeof(IPreviewHandler).GUID;
            var clsidCopy = clsid;
            if (CoCreateInstance(ref clsidCopy, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out handler) != 0
                && CoCreateInstance(ref clsidCopy, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out handler) != 0)
                throw new COMException("The preview handler for this type could not be started");

            if (handler is IInitializeWithFile withFile) withFile.Initialize(path, STGM_READ);
            else if (handler is IInitializeWithStream withStream)
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, null, out var stream));
                withStream.Initialize(stream, STGM_READ);
            }
            else if (handler is IInitializeWithItem withItem)
            {
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item));
                withItem.Initialize((IShellItem)item, STGM_READ);
            }
            else throw new COMException("The preview handler for this type can't be given a file");

            if (handler is IPreviewHandlerVisuals visuals) // handlers that draw their own text follow the dark theme
            {
                visuals.SetBackgroundColor(0x00130D0A);
                visuals.SetTextColor(0x00F6EEE8);
            }
            var r = DeviceRect();
            ((IPreviewHandler)handler).SetWindow(window, ref r);
            ((IPreviewHandler)handler).DoPreview();
            SetFocus(parentWindow); // handlers like to take the keyboard; Quick View needs it for Space/Esc/arrows
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or System.IO.IOException)
        {
            Unload();
            Failed?.Invoke(ex.Message);
        }
    }

    RECT DeviceRect()
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice ?? Matrix.Identity;
        return new RECT { Right = (int)(ActualWidth * m.M11), Bottom = (int)(ActualHeight * m.M22) };
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (handler is IPreviewHandler h) { var r = DeviceRect(); h.SetRect(ref r); }
    }

    void Unload()
    {
        if (handler == null) return;
        try { ((IPreviewHandler)handler).Unload(); } catch (COMException) { } // already gone (out-of-process crash)
        Marshal.FinalReleaseComObject(handler);
        handler = null;
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
    }

    // ---- interop ----
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    const uint CLSCTX_INPROC_SERVER = 1, CLSCTX_LOCAL_SERVER = 4, STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
    }

    [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandlerVisuals
    {
        void SetBackgroundColor(uint color);
        void SetFont(IntPtr logfont);
        void SetTextColor(uint color);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithItem { void Initialize(IShellItem item, uint mode); }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int AssocQueryString(uint flags, int str, string assoc, string extra, StringBuilder outBuf, ref uint len);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IStream template, out IStream stream);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
}
