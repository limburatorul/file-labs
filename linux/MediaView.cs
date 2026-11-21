using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FileExplorer;

/// Video and music in Quick View and the preview pane, as on Windows: play, pause, a seek bar and the time. Played by
/// GStreamer, the desktop's own media library (GNOME's and Cinnamon's players use it), so a file plays here when the
/// desktop has its codec; without GStreamer, or without the codec, the file gets its summary and "Open with its app".
/// Quick View plays at once; the preview pane shows the first frame and waits for a click, or every video passed on the
/// way would start.
sealed class MediaView : DockPanel
{
    const int W = 960, H = 540; // frames come letterboxed into this box
    const int Null = 1, Paused = 3, Playing = 4, TimeFormat = 3, FlushAccurate = 1 | 2, ErrorMessage = 2;

    readonly IntPtr sink, bus;
    IntPtr pipeline;
    readonly WriteableBitmap frame;
    readonly Image image;
    readonly Slider slider = new() { Minimum = 0, Maximum = 1, Margin = new(12, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock time = new() { Foreground = Fmt.Brush("#B3FFFFFF"), VerticalAlignment = VerticalAlignment.Center, FontFeatures = FontFeatureCollection.Parse("tnum") };
    readonly TextBlock playGlyph = new() { Classes = { "icon" }, FontSize = 14 };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly byte[] pixels = new byte[W * H * 4];
    readonly Func<string, Control> failed;
    volatile bool running = true, wantFrame = true;
    int framePosted;
    bool paused, sliding;
    Thread pump;

    /// null when GStreamer can't be loaded or won't take the file; the caller shows the summary then.
    public static MediaView TryCreate(string path, bool video, bool play, Func<string, Control> failed)
    {
        if (!Gst.Ready()) return null;
        var view = new MediaView(path, video, play, failed);
        return view.pipeline == IntPtr.Zero ? null : view;
    }

    MediaView(string path, bool video, bool play, Func<string, Control> failed)
    {
        this.failed = failed;
        var uri = new Uri(path).AbsoluteUri; // quotes and spaces come out percent-encoded
        pipeline = Gst.gst_parse_launch($"playbin uri=\"{uri}\"" + (video ? "" : " video-sink=fakesink"), out var err);
        if (err != IntPtr.Zero) Gst.g_error_free(err);
        if (pipeline == IntPtr.Zero) return;
        bus = Gst.gst_element_get_bus(pipeline);
        if (video)
        {
            // the frames' branch is made apart and handed to playbin, whose sinks only exist once it plays
            var vbin = Gst.gst_parse_bin_from_description($"videoconvert ! videoscale ! video/x-raw,format=BGRA,width={W},height={H},pixel-aspect-ratio=1/1 ! appsink name=fl-frames max-buffers=2 drop=true", true, out err);
            if (err != IntPtr.Zero) Gst.g_error_free(err);
            if (vbin == IntPtr.Zero) { Gst.gst_object_unref(pipeline); pipeline = IntPtr.Zero; return; }
            sink = Gst.gst_bin_get_by_name(vbin, "fl-frames");
            var value = new byte[24]; // a GValue holding the bin
            var h = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                Gst.g_value_init(h.AddrOfPinnedObject(), Gst.gst_element_get_type());
                Gst.g_value_set_object(h.AddrOfPinnedObject(), vbin);
                Gst.g_object_set_property(pipeline, "video-sink", h.AddrOfPinnedObject()); // playbin takes its own reference
                Gst.g_value_unset(h.AddrOfPinnedObject());
            }
            finally { h.Free(); }
        }

        var button = new Button { Classes = { "ghost" }, Content = playGlyph, Padding = new(8, 4) };
        button.Click += (_, _) => Toggle();
        var bar = new DockPanel { Margin = new(12, 8, 12, 10) };
        DockPanel.SetDock(button, Dock.Left); DockPanel.SetDock(time, Dock.Right);
        bar.Children.Add(button); bar.Children.Add(time); bar.Children.Add(slider);
        DockPanel.SetDock(bar, Dock.Bottom);
        Children.Add(bar);
        if (video)
        {
            frame = new WriteableBitmap(new PixelSize(W, H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            image = new Image { Source = frame, Stretch = Stretch.Uniform };
            var screen = new Border { Background = Brushes.Black, Child = image, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            screen.PointerReleased += (_, _) => Toggle();
            Children.Add(screen);
        }
        else Children.Add(failed(null)); // music: the summary, with the bar under it

        slider.AddHandler(PointerPressedEvent, (_, _) => sliding = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.AddHandler(PointerReleasedEvent, (_, _) => { sliding = false; Seek(slider.Value); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        timer.Tick += (_, _) => Tick();
        DetachedFromVisualTree += (_, _) => Stop();

        paused = !play;
        playGlyph.Text = paused ? Glyph.Play : Glyph.Pause;
        Gst.gst_element_set_state(pipeline, play ? Playing : Paused);
        timer.Start();
        if (video) { pump = new Thread(Pump) { IsBackground = true, Name = "media frames" }; pump.Start(); }
    }

    void Toggle()
    {
        if (!running) return;
        paused = !paused;
        playGlyph.Text = paused ? Glyph.Play : Glyph.Pause;
        Gst.gst_element_set_state(pipeline, paused ? Paused : Playing);
    }

    void Seek(double seconds)
    {
        if (!running) return;
        Gst.gst_element_seek_simple(pipeline, TimeFormat, FlushAccurate, (long)(seconds * 1e9));
        wantFrame = true; // paused: show where it landed
    }

    void Tick()
    {
        // an error on the bus: a missing codec, a broken file
        var msg = Gst.gst_bus_pop_filtered(bus, ErrorMessage);
        if (msg != IntPtr.Zero)
        {
            Gst.gst_message_parse_error(msg, out var gerr, out var debug);
            var text = gerr != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerr, 8)) : "unknown error";
            if (gerr != IntPtr.Zero) Gst.g_error_free(gerr);
            Gst.g_free(debug);
            Gst.gst_mini_object_unref(msg);
            Stop();
            if (Parent is ContentControl host) host.Content = failed("Can't play this file (missing codec?): " + text);
            return;
        }
        if (Gst.gst_element_query_duration(pipeline, TimeFormat, out var dur) && dur > 0) slider.Maximum = dur / 1e9;
        if (Gst.gst_element_query_position(pipeline, TimeFormat, out var pos) && pos >= 0)
        {
            if (!sliding) slider.Value = Math.Min(pos / 1e9, slider.Maximum);
            time.Text = $"{Clock(pos / 1e9)} / {Clock(slider.Maximum)}";
        }
        if (sink != IntPtr.Zero && Gst.gst_app_sink_is_eos(sink) && !paused) Toggle(); // at the end: paused, Play starts over below
        if (paused && pos >= dur && dur > 0) { Seek(0); }
    }

    static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // Frames come in on this thread: the newest is copied out and handed to the UI, which draws it once it is free
    void Pump()
    {
        while (running)
        {
            var sample = paused ? (wantFrame ? Gst.gst_app_sink_try_pull_preroll(sink, 100_000_000) : IntPtr.Zero) : Gst.gst_app_sink_try_pull_sample(sink, 100_000_000);
            if (sample == IntPtr.Zero) { if (paused && !wantFrame) Thread.Sleep(50); continue; }
            wantFrame = false;
            var buffer = Gst.gst_sample_get_buffer(sample);
            if (buffer != IntPtr.Zero && Gst.gst_buffer_map(buffer, out var map, 1 /* read */))
            {
                if ((long)map.size >= pixels.Length)
                    lock (pixels) Marshal.Copy(map.data, pixels, 0, pixels.Length);
                Gst.gst_buffer_unmap(buffer, ref map);
            }
            Gst.gst_mini_object_unref(sample);
            if (Interlocked.Exchange(ref framePosted, 1) == 0) Dispatcher.UIThread.Post(Draw, DispatcherPriority.Render);
        }
    }

    void Draw()
    {
        Interlocked.Exchange(ref framePosted, 0);
        if (!running) return;
        using (var fb = frame.Lock())
            lock (pixels) Marshal.Copy(pixels, 0, fb.Address, pixels.Length); // BGRA at this width has no row padding
        image.InvalidateVisual();
    }

    void Stop()
    {
        if (!running) return;
        running = false;
        timer.Stop();
        pump?.Join(500);
        Gst.gst_element_set_state(pipeline, Null);
        if (sink != IntPtr.Zero) Gst.gst_object_unref(sink);
        if (bus != IntPtr.Zero) Gst.gst_object_unref(bus);
        Gst.gst_object_unref(pipeline);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MapInfo
    {
        public IntPtr memory; public int flags; public IntPtr data; public UIntPtr size, maxsize;
        public IntPtr user0, user1, user2, user3, reserved0, reserved1, reserved2, reserved3;
    }

    static class Gst
    {
        const string Core = "libgstreamer-1.0.so.0", App = "libgstapp-1.0.so.0", GLib = "libglib-2.0.so.0", GObject = "libgobject-2.0.so.0";
        static int state; // 0 not tried, 1 ready, -1 missing

        public static bool Ready()
        {
            if (state == 0)
                try { gst_init(IntPtr.Zero, IntPtr.Zero); gst_app_sink_get_type(); state = 1; }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { state = -1; }
            return state == 1;
        }

        [DllImport(Core)] public static extern void gst_init(IntPtr argc, IntPtr argv);
        [DllImport(Core)] public static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
        [DllImport(Core)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Core)] public static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(Core)] public static extern bool gst_element_query_position(IntPtr element, int format, out long cur);
        [DllImport(Core)] public static extern bool gst_element_query_duration(IntPtr element, int format, out long duration);
        [DllImport(Core)] public static extern bool gst_element_seek_simple(IntPtr element, int format, int flags, long position);
        [DllImport(Core)] public static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(Core)] public static extern IntPtr gst_bus_pop_filtered(IntPtr bus, int types);
        [DllImport(Core)] public static extern void gst_message_parse_error(IntPtr message, out IntPtr gerror, out IntPtr debug);
        [DllImport(Core)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(Core)] public static extern bool gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);
        [DllImport(Core)] public static extern void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);
        [DllImport(Core)] public static extern void gst_mini_object_unref(IntPtr obj);
        [DllImport(Core)] public static extern void gst_object_unref(IntPtr obj);
        [DllImport(App)] public static extern IntPtr gst_app_sink_get_type();
        [DllImport(App)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
        [DllImport(App)] public static extern IntPtr gst_app_sink_try_pull_preroll(IntPtr sink, ulong timeout);
        [DllImport(App)] public static extern bool gst_app_sink_is_eos(IntPtr sink);
        [DllImport(Core)] public static extern IntPtr gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, bool ghostUnlinkedPads, out IntPtr error);
        [DllImport(Core)] public static extern IntPtr gst_element_get_type();
        [DllImport(GObject)] public static extern IntPtr g_value_init(IntPtr value, IntPtr type);
        [DllImport(GObject)] public static extern void g_value_set_object(IntPtr value, IntPtr obj);
        [DllImport(GObject)] public static extern void g_value_unset(IntPtr value);
        [DllImport(GObject)] public static extern void g_object_set_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value);
        [DllImport(GLib)] public static extern void g_error_free(IntPtr error);
        [DllImport(GLib)] public static extern void g_free(IntPtr mem);
    }
}
