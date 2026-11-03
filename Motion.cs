using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FileExplorer;

// How things appear, in one place: a short fade with a slight grow or rise, eased out, and a window fades out as it
// closes. Nothing moves when Windows' "Animation effects" is off or Settings → Animations is.
// Menus and tooltips aren't here: WPF already fades their popups by Windows' own menu and tooltip settings.
static class Motion
{
    public static bool On => Settings.Animations && SystemParameters.ClientAreaAnimation;

    static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// Fades `e` in from `from`, grown from `scale` (about its RenderTransformOrigin) and risen `rise` px. `window`: e is
    /// that window's root, and the count starts at its first frame, after which Settings and Help fill themselves in.
    public static void Enter(UIElement e, double scale = 1, double rise = 0, int ms = 180, double from = 0, Window window = null)
    {
        if (!On) return;
        ScaleTransform s = null; TranslateTransform t = null;
        // ours, or none yet: an element's own transform is left alone and it only fades
        if (scale != 1 || rise != 0)
        {
            if (e.RenderTransform is TransformGroup { Children: [ScaleTransform s0, TranslateTransform t0] }) { s = s0; t = t0; }
            else if (e.RenderTransform == Transform.Identity)
                e.RenderTransform = new TransformGroup { Children = { (s = new ScaleTransform()), (t = new TranslateTransform()) } };
        }
        void Run(bool go) // false: held where it starts
        {
            var d = new Duration(TimeSpan.FromMilliseconds(go ? ms : 0));
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, go ? 1 : from, d) { EasingFunction = Out });
            if (s == null) return;
            var grow = new DoubleAnimation(scale, go ? 1 : scale, d) { EasingFunction = Out };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, go ? 0 : rise, d) { EasingFunction = Out });
        }
        // Held at the start, and begun two frames later: the frame that lays the element out can be long (a window's
        // first, 50-200 ms), and an animation begun before it was most of the way through when first drawn.
        Run(false);
        int frames = 0;
        TimeSpan last = TimeSpan.Zero;
        EventHandler start = null;
        start = (_, a) =>
        {
            var now = ((RenderingEventArgs)a).RenderingTime;
            if (now == last || ++frames < 2) { last = now; return; } // raised twice for one frame at times
            CompositionTarget.Rendering -= start;
            Run(true);
        };
        if (window == null) CompositionTarget.Rendering += start;
        else window.ContentRendered += (_, _) => CompositionTarget.Rendering += start;
    }

    /// Called once, before any window: every window but the main one grows in when it first appears, and every window
    /// fades out when it closes.
    /// On its first layout, not Loaded: WPF skips Loaded for a window with no Loaded handler in it yet (Quick View, whose
    /// content comes later), and class handlers don't count.
    public static void Hook() => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(WindowLaidOut));
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> seen = new();

    // the window's template root: it stays when the content is replaced (Quick View swaps it per file)
    static UIElement Root(Window w) => VisualTreeHelper.GetChildrenCount(w) > 0 ? VisualTreeHelper.GetChild(w, 0) as UIElement : null;

    static void WindowLaidOut(object s, SizeChangedEventArgs e)
    {
        // WebView2 draws in its own HWND, which neither fades nor scales; the editor also asks about unsaved tabs on close
        if (s is not Window w || w is EditorWindow || Root(w) is not { } root || !seen.TryAdd(w, null)) return;
        // Not the main window: drawn through an opacity layer, its first frame came 170 ms later (median of 6 starts), and
        // Windows already animates it opening. It still fades out on close.
        if (w is not MainWindow)
        {
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            Enter(root, scale: 0.97, window: w);
        }
        w.Closing += FadeOut; // after the window's own handlers, so one that cancels (close to tray) is seen
    }

    /// The pizza button's nudge: 3 s after `w` first shows, then every 10 s while it is on screen, a 700 ms double pulse
    /// (grow to 1.06, back, to 1.03, back) with the template's "Glow" brightening it at each peak. Not while the pointer
    /// is on it; the timer stops while the window is minimized or hidden to the tray.
    public static void Nudge(Button b, Window w)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = new ScaleTransform();
        b.RenderTransform = s;
        timer.Tick += (_, _) =>
        {
            timer.Interval = TimeSpan.FromSeconds(10);
            if (!On || b.IsMouseOver || !b.IsVisible || b.Template.FindName("Glow", b) is not UIElement glow) return;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, 1.06, 1, 1.03, 1));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, 1.06, 1, 1.03, 1));
            glow.BeginAnimation(UIElement.OpacityProperty, Pulse(0, 1, 0, 1, 0));
        };
        void Follow() => timer.IsEnabled = w.IsVisible && w.WindowState != WindowState.Minimized;
        w.ContentRendered += (_, _) =>
        {
            Follow();
            w.StateChanged += (_, _) => Follow();
            w.IsVisibleChanged += (_, _) => Follow();
        };
    }

    // values 175 ms apart, each step eased out
    static DoubleAnimationUsingKeyFrames Pulse(params double[] v)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        for (int i = 0; i < v.Length; i++) a.KeyFrames.Add(new EasingDoubleKeyFrame(v[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 175)), Out));
        return a;
    }

    static readonly HashSet<Window> leaving = new();

    /// The first popup the app shows (a menu, a tooltip) held its first frame back by about half a second; once any popup
    /// has been on screen, a menu draws in under 90 ms. So one, empty and click-through, is shown for a moment at idle
    /// after the window's first frame.
    public static void WarmPopups()
    {
        var p = new Popup { AllowsTransparency = true, Placement = PlacementMode.Absolute, Child = new Border { Width = 1, Height = 1 } };
        p.Opened += (_, _) => p.Dispatcher.BeginInvoke(() => p.IsOpen = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        p.IsOpen = true;
    }

    static void FadeOut(object s, CancelEventArgs e)
    {
        var w = (Window)s;
        if (leaving.Remove(w) || e.Cancel || !On || Root(w) is not { } root) return; // a second Close is the real one
        e.Cancel = true;
        leaving.Add(w);
        bool? result = w.DialogResult; // cancelling the close forgets it
        root.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        // closed once the last, empty frame is drawn: taking a window down costs 30-50 ms (Quick View), which otherwise
        // froze the fade's last visible frame for that long
        fade.Completed += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            if (!leaving.Contains(w)) return; // closed meanwhile (a second Close, Application.Shutdown)
            if (result != null && w.DialogResult != result) w.DialogResult = result; else w.Close();
            if (PresentationSource.FromVisual(w) == null) return;
            // someone kept it open after all: it must not stay invisible
            leaving.Remove(w);
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.IsHitTestVisible = true;
        }, System.Windows.Threading.DispatcherPriority.Background);
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
