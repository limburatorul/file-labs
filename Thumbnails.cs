using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileExplorer;

// Explorer's own thumbnails/icons via IShellItemImageFactory: photos, videos, PDFs, and the
// proper icon for everything else — one call, same cache Explorer uses.
public static class Thumbnails
{
    // Asking the shell costs milliseconds per file, and folders get revisited constantly (tabs, back,
    // the sidebar), so every answer is kept. The key carries the file's timestamp: an edited photo
    // gets a new thumbnail instead of the old one.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BitmapSource Image, long UsedAt)> cache = new(StringComparer.OrdinalIgnoreCase);
    const int MaxCached = 4000;

    /// Returns a frozen image (safe to hand to the UI thread) or null. Call from an STA thread.
    public static BitmapSource Get(string path, int size, bool iconOnly = false, long stamp = 0)
    {
        var key = $"{path}|{size}|{iconOnly}|{stamp}";
        if (cache.TryGetValue(key, out var hit)) { cache[key] = (hit.Image, DateTime.UtcNow.Ticks); return hit.Image; }
        var image = Fetch(path, size, iconOnly);
        if (image == null) return null;
        cache[key] = (image, DateTime.UtcNow.Ticks);
        if (cache.Count > MaxCached)
            foreach (var old in cache.OrderBy(kv => kv.Value.UsedAt).Take(cache.Count / 4).ToList()) cache.TryRemove(old.Key, out _);
        return image;
    }

    static BitmapSource Fetch(string path, int size, bool iconOnly)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0) return null;
        var factory = (IShellItemImageFactory)obj;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, iconOnly ? SIIGBF_ICONONLY : SIIGBF_RESIZETOFIT, out var hbmp) != 0) return null;
            try { return ToBitmapSource(hbmp); }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    // CreateBitmapSourceFromHBitmap drops the alpha channel (icons get black corners),
    // so copy the DIB bits directly.
    static BitmapSource ToBitmapSource(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight, stride = w * 4;
        // Ask GDI for the pixels as a top-down 32-bit DIB (negative height): it converts whatever row
        // order the shell used — thumbnails and icons differ, and the header doesn't reliably say which.
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var flipped = new byte[stride * h];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbmp, 0, (uint)h, flipped, ref bmi, 0) != h) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // The shell hands back three kinds of 32-bit bitmaps: premultiplied alpha (most thumbnails),
        // straight alpha (many icons — read as premultiplied, their transparent parts turn white),
        // and no alpha at all (every alpha byte 0 — would be invisible). Tell them apart per image.
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < flipped.Length; i += 4)
        {
            byte a = flipped[i + 3];
            if (a != 0) anyAlpha = true;
            if (flipped[i] > a || flipped[i + 1] > a || flipped[i + 2] > a) straight = true; // impossible if premultiplied
        }
        if (!anyAlpha) for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        var format = !anyAlpha || !straight ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
        var src = BitmapSource.Create(w, h, 96, 96, format, null, flipped, stride);
        src.Freeze();
        return src;
    }

    // ---- icon overlays: the sync badges of Google Drive, Nextcloud, Dropbox etc., as in Explorer ----
    // Those apps register shell icon overlay handlers; the shell asks each one whether a file is
    // theirs and reports the winning overlay's number. The badge picture lives in the system image list.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, BitmapSource> overlays = new();
    // Which badge a file carries, remembered per path: asking costs ~4 ms because every sync app's
    // handler is consulted. Dropped again when the shell says that item changed.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> badgeOf = new(StringComparer.OrdinalIgnoreCase);

    public static void ForgetOverlay(string path) => badgeOf.TryRemove(path, out _);

    /// The overlay badge Explorer would draw on this item (a full icon-sized image, badge in the
    /// corner), or null. Call from an STA thread: the shell runs the apps' handlers on it.
    public static BitmapSource Overlay(string path)
    {
        if (badgeOf.TryGetValue(path, out var known)) return known;
        var badge = ReadOverlay(path);
        if (badgeOf.Count > MaxCached) badgeOf.Clear(); // they are cheap to build again
        badgeOf[path] = badge;
        return badge;
    }

    static BitmapSource ReadOverlay(string path)
    {
        var fi = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_OVERLAYINDEX) == IntPtr.Zero) return null;
        if (fi.hIcon != IntPtr.Zero) DestroyIcon(fi.hIcon);
        int overlay = (fi.iIcon >> 24) & 0xFF;
        if (overlay == 0) return null;
        return overlays.GetOrAdd(overlay, n =>
        {
            var iid = typeof(IImageList).GUID;
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) != 0 || list.GetOverlayImage(n, out int image) != 0
                || list.GetIcon(image, ILD_TRANSPARENT, out var icon) != 0) return null;
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DestroyIcon(icon); }
        });
    }

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_OVERLAYINDEX = 0x40;
    const int SHIL_EXTRALARGE = 2, ILD_TRANSPARENT = 1; // 48 px: scaled down for rows, crisp enough on tiles

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // Only GetIcon and GetOverlayImage are called; the rest hold their places in the vtable.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        void Add(); void ReplaceIcon(); void SetOverlayImage(); void Replace(); void AddMasked(); void Draw(); void Remove();
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr icon);
        void GetImageInfo(); void Copy(); void Merge(); void Clone(); void GetImageRect(); void GetIconSize(); void SetIconSize();
        void GetImageCount(); void SetImageCount(); void SetBkColor(); void GetBkColor(); void BeginDrag(); void EndDrag();
        void DragEnter(); void DragLeave(); void DragMove(); void SetDragCursorImage(); void DragShowNolock(); void GetDragImage(); void GetItemFlags();
        [PreserveSig] int GetOverlayImage(int overlay, out int index);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetImageList(int list, ref Guid iid, out IImageList imageList);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    const int SIIGBF_RESIZETOFIT = 0, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbmp); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}
