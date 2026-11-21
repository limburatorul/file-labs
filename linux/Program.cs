using System.Runtime.InteropServices;
using Avalonia;

namespace FileExplorer;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        DesktopScale();
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia on X11 scales by Xft.dpi only. Cinnamon, MATE, Xfce and GNOME on X11 keep that at 96 when the display is
    // set to 200% and publish the factor in XSETTINGS (Gdk/WindowScalingFactor), which GTK apps read; without this File
    // Labs stayed at 100% there. An explicit AVALONIA_*SCALE* variable still wins.
    static void DesktopScale()
    {
        if (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS") is { Length: > 0 }) return;
        try
        {
            if (XSettings.WindowScalingFactor() is int f and > 1 and <= 4)
                Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", f.ToString());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } // no X11: nothing to read
    }
}

/// Reads one integer from the X settings daemon (the freedesktop XSETTINGS spec): the _XSETTINGS_S<screen> selection's
/// owner carries a _XSETTINGS_SETTINGS property with every setting.
static class XSettings
{
    const string X = "libX11.so.6";

    public static int? WindowScalingFactor()
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return null;
        var d = XOpenDisplay(IntPtr.Zero);
        if (d == IntPtr.Zero) return null;
        try
        {
            var owner = XGetSelectionOwner(d, XInternAtom(d, $"_XSETTINGS_S{XDefaultScreen(d)}", false));
            if (owner == 0) return null;
            var prop = XInternAtom(d, "_XSETTINGS_SETTINGS", false);
            if (XGetWindowProperty(d, owner, prop, 0, 1 << 16, false, prop, out _, out int format, out var n, out _, out var data) != 0 || data == IntPtr.Zero) return null;
            if (format != 8) return null;
            try { var bytes = new byte[(int)n]; Marshal.Copy(data, bytes, 0, bytes.Length); return Find(bytes, "Gdk/WindowScalingFactor"); }
            finally { XFree(data); }
        }
        finally { XCloseDisplay(d); }
    }

    /// The integer setting called name in an XSETTINGS blob, or null.
    public static int? Find(byte[] b, string name)
    {
        if (b.Length < 12) return null;
        bool big = b[0] == 1;
        uint U32(int at) => big ? (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]) : BitConverter.ToUInt32(b, at);
        int U16(int at) => big ? b[at] << 8 | b[at + 1] : BitConverter.ToUInt16(b, at);
        static int Pad(int n) => (n + 3) & ~3;
        int at = 12;
        for (uint i = 0, count = U32(8); i < count && at + 8 <= b.Length; i++)
        {
            int type = b[at], len = U16(at + 2);
            if (at + 4 + Pad(len) + 4 > b.Length) return null;
            var key = System.Text.Encoding.ASCII.GetString(b, at + 4, len);
            at += 4 + Pad(len) + 4; // name, then the serial of its last change
            switch (type)
            {
                case 0: if (at + 4 > b.Length) return null; if (key == name) return (int)U32(at); at += 4; break;   // integer
                case 1: if (at + 4 > b.Length) return null; at += 4 + Pad((int)U32(at)); break;                       // string
                case 2: at += 8; break;                                                                                // colour
                default: return null;
            }
        }
        return null;
    }

    [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(X)] static extern int XDefaultScreen(IntPtr display);
    [DllImport(X)] static extern nuint XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(X)] static extern nuint XGetSelectionOwner(IntPtr display, nuint selection);
    [DllImport(X)] static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, bool delete, nuint type,
                                                        out nuint actualType, out int actualFormat, out nuint items, out nuint bytesAfter, out IntPtr data);
    [DllImport(X)] static extern int XFree(IntPtr data);
}
