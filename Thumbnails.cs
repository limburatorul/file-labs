using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileExplorer;

// Explorer's own thumbnails/icons via IShellItemImageFactory: photos, videos, PDFs, and the
// proper icon for everything else — one call, same cache Explorer uses.
public static class Thumbnails
{
    /// Returns a frozen image (safe to hand to the UI thread) or null. Call from an STA thread.
    public static BitmapSource Get(string path, int size, bool iconOnly = false)
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
