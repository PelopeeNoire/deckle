using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Deckle.Controls;
using Deckle.Interop;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Shell;

namespace Deckle;

// Overlay card Window used by HudOverlayManager. Each enqueued overlay creates
// one HudOverlayWindow; the manager owns its position and life timer, the
// window owns its own alpha (fade-in / fade-out / proximity modulation).
//
// Same technical shell as HudWindow — fixed HUD_WIDTH x HUD_HEIGHT in dips,
// opaque LayerFillColorDefaultBrush, DWM round + default accent stroke,
// WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT, WM_NCCALCSIZE erased
// via subclass. Also mirrors HudWindow's proximity-fade behaviour: when the
// cursor approaches, alpha smooth-ramps from MAX down toward MIN. Implemented
// via a DispatcherQueueTimer + GetCursorPos poll rather than Raw Input — a
// RIDEV_INPUTSINK registration is per-process-per-usage and would clobber the
// single registration HudWindow owns. Overlays are transient (2-8 s) so
// polling 60 Hz costs ~120 GetCursorPos calls per card, which is negligible.
//
// Unlike HudWindow, no Closing intercept: the manager calls FadeOut, which
// ends with ForceClose → Window.Close() after the alpha animator reaches 0.
public sealed partial class HudOverlayWindow : Window
{
    public const int HUD_WIDTH  = 272;
    public const int HUD_HEIGHT = 78;

    // Proximity smoothstep constants. NEAR / MIN / MAX match HudWindow so
    // the endpoints behave the same, but FAR_RADIUS is deliberately wider
    // than HudWindow's 128 dip — overlays sit above the main HUD so any
    // cursor approach toward the main HUD naturally passes through the
    // overlay region too, and the user wants those cards to start clearing
    // earlier to stay out of the way.
    private const double NEAR_RADIUS_DIP = 10;
    private const double FAR_RADIUS_DIP  = 256;
    private const byte   MAX_ALPHA       = 255;
    private const byte   MIN_ALPHA       = 40;

    private readonly IntPtr _hwnd;
    private LayeredAlphaAnimator? _fade;

    private NativeMethods.SubclassProc? _subclassDelegate;
    private static readonly UIntPtr SubclassId = new(0x48554F56); // "HUOV"

    private DispatcherQueueTimer? _proximityTimer;
    private bool _proximityActive;
    private byte _proximityAlpha = MAX_ALPHA;

    public HudOverlayWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = null;
        Title = Loc.Get("HudOverlay_WindowTitle");
        IconAssets.ApplyToWindow(AppWindow);

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsResizable   = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        // Same DWM triptych as HudWindow — DWMWCP_ROUND for the rounded HWND
        // silhouette, DWMWA_COLOR_DEFAULT for the 1-dip system accent stroke
        // that frames the card (without it the card reads as a naked colored
        // rectangle on the desktop), DWMSBT_NONE to keep Mica/Acrylic off.
        //
        // Additional tweak on overlay cards vs the main HUD:
        // DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED. The main HUD's Win11
        // Shell dropshadow is fine against the desktop, but when an overlay
        // card sits 12 dip above the HUD that same shadow lands on the HUD's
        // top edge as a visible halo. Disabling NC rendering kills the
        // shadow on overlays; the stroke + corner-preference compositor clip
        // are independent paths, they keep doing their job.
        uint cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPref, sizeof(uint));
        uint borderColor = NativeMethods.DWMWA_COLOR_DEFAULT;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR,
            ref borderColor, sizeof(uint));
        uint ncPolicy = NativeMethods.DWMNCRP_DISABLED;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_NCRENDERING_POLICY,
            ref ncPolicy, sizeof(uint));
        uint backdropType = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdropType, sizeof(uint));

        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
        // Start fully transparent — LayeredAlphaAnimator ramps up to 255 when
        // the manager invokes FadeTo after ShowAt.
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, NativeMethods.LWA_ALPHA);

        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Fade animator owns the layered alpha from here on. Manager calls
        // FadeIn / FadeOut; proximity polling mixes in at runtime via instant
        // FadeTo updates (no animation overhead — we're already driving frames
        // at 60 Hz from the proximity timer).
        _fade = new LayeredAlphaAnimator(_hwnd, DispatcherQueue, initialAlpha: 0);
    }

    public IntPtr Hwnd => _hwnd;

    // Pushes the feedback payload into the embedded HudMessage control. The
    // Duration field of MessagePayload is unused here (HudMessage ignores it);
    // the manager owns the per-card timer.
    public void ApplyPayload(UserFeedback fb)
    {
        MessageKind kind = fb.Severity switch
        {
            UserFeedbackSeverity.Info    => MessageKind.Informational,
            UserFeedbackSeverity.Warning => MessageKind.Warning,
            _                             => MessageKind.Critical,
        };
        Message.Show(new MessagePayload(kind, fb.Title, fb.Body, TimeSpan.Zero));
    }

    // Card pixel size for the current DPI — manager uses this to compute slot
    // positions (stride = gap + card height).
    public (int Width, int Height) GetSizePx()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;
        return (
            (int)Math.Round(HUD_WIDTH  * scale),
            (int)Math.Round(HUD_HEIGHT * scale));
    }

    // Initial placement: size the window at current DPI, move to target pixel
    // position, then show without activation and pin to HWND_TOP so the newest
    // overlay sits in front of older sibling overlays.
    public void ShowAt(int xPx, int yPx)
    {
        var (w, h) = GetSizePx();
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(xPx, yPx, w, h));
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    // Used for instant repositioning (reduced-motion path, or to bypass the
    // animator if the manager needs to snap). Under normal conditions,
    // WindowSlideAnimator.SlideTo owns the live position of this HWND.
    public void SetPositionPx(int xPx, int yPx)
    {
        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero,
            xPx, yPx, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // ── Fade: public entry points driven by HudOverlayManager ────────────────

    // Ramps alpha from current (0 on first call after ShowAt) up to MAX_ALPHA
    // over 150 ms, then arms the proximity polling loop. Callback fires once
    // both the fade and the proximity arming are complete.
    public void FadeIn(Action? onComplete = null)
    {
        _fade?.FadeTo(MAX_ALPHA, onComplete: () =>
        {
            _proximityAlpha = MAX_ALPHA;
            BeginProximityMode();
            onComplete?.Invoke();
        });
    }

    // Disarms the proximity loop immediately (so it can't fight the fade),
    // then ramps alpha down to 0 over 150 ms. Manager uses the completion
    // callback to schedule ForceClose.
    public void FadeOut(Action? onComplete = null)
    {
        EndProximityMode();
        _fade?.FadeTo(0, onComplete: onComplete);
    }

    // ── Proximity: cursor-distance → alpha smoothstep ────────────────────────
    //
    // Same profile as HudWindow.UpdateProximity, driven by a 60 Hz dispatcher
    // timer instead of RIDEV_INPUTSINK. The proximity-requested alpha only
    // modulates downward from MAX_ALPHA to MIN_ALPHA — fade-in / fade-out own
    // the 0..MAX_ALPHA range.

    private void BeginProximityMode()
    {
        if (_proximityActive) return;
        if (!Settings.SettingsService.Instance.Current.Overlay.FadeOnProximity) return;

        _proximityActive = true;
        _proximityTimer ??= DispatcherQueue.CreateTimer();
        _proximityTimer.Interval = TimeSpan.FromMilliseconds(16);
        _proximityTimer.IsRepeating = true;
        _proximityTimer.Tick -= OnProximityTick;
        _proximityTimer.Tick += OnProximityTick;
        _proximityTimer.Start();

        // Seed with the current cursor distance so the first frame after
        // fade-in already reflects reality rather than snapping one tick later.
        UpdateProximity();
    }

    private void EndProximityMode()
    {
        if (_proximityTimer is not null)
        {
            _proximityTimer.Stop();
            _proximityTimer.Tick -= OnProximityTick;
        }
        _proximityActive = false;
    }

    private void OnProximityTick(DispatcherQueueTimer sender, object args)
        => UpdateProximity();

    private void UpdateProximity()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        var pos  = AppWindow.Position;
        var size = AppWindow.Size;
        int left   = pos.X;
        int top    = pos.Y;
        int right  = pos.X + size.Width;
        int bottom = pos.Y + size.Height;

        int dx = cursor.X < left ? left - cursor.X : (cursor.X > right  ? cursor.X - right  : 0);
        int dy = cursor.Y < top  ? top  - cursor.Y : (cursor.Y > bottom ? cursor.Y - bottom : 0);
        double distancePx = Math.Sqrt(dx * dx + dy * dy);

        double scale  = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        double nearPx = NEAR_RADIUS_DIP * scale;
        double farPx  = FAR_RADIUS_DIP  * scale;

        double t = (distancePx - nearPx) / (farPx - nearPx);
        if (t < 0.0) t = 0.0;
        if (t > 1.0) t = 1.0;

        double eased = t * t * (3.0 - 2.0 * t);

        byte alpha = (byte)Math.Round(MIN_ALPHA + eased * (MAX_ALPHA - MIN_ALPHA));
        if (alpha == _proximityAlpha) return;
        _proximityAlpha = alpha;
        _fade?.FadeTo(alpha, instant: true);
    }

    // Actually destroys the window (no Closing intercept on this transient).
    // The manager calls this after the fade-out animator completes.
    public void ForceClose()
    {
        EndProximityMode();
        _fade?.Cancel();

        if (_subclassDelegate is not null)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
            _subclassDelegate = null;
        }
        Close();
    }

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        // Same zero-NC-area trick as HudWindow. WinUI 3 reapplies
        // WS_DLGFRAME / WS_EX_WINDOWEDGE on the top-level; we leave those bits
        // on and deny Windows any non-client area to paint into.
        if (uMsg == NativeMethods.WM_NCCALCSIZE)
        {
            return IntPtr.Zero;
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
