using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using WhispUI.Controls;
using WhispUI.Interop;
using WhispUI.Localization;
using WhispUI.Logging;
using WhispUI.Shell;

namespace WhispUI;

// ─── HUD bottom-center ───────────────────────────────────────────────────────
//
// WinUI 3 transient window, never destroyed. Fixed 272x78 in dips for every
// state. Hosts two UserControls swapped via Visibility:
//   - HudChrono   — Charging / Recording / Transcribing / Rewriting
//   - HudMessage  — Pasted / Copied / Error / UserFeedback
//
// This window owns only the technical shell: HWND, layered alpha,
// proximity fade subclass, lifecycle. All visual logic lives in the
// controls.
//
// The HUD is fully opaque — no transparency, no SystemBackdrop. The card
// visual is the hosted UserControl's Border (fill + corner radius from
// theme resources). The HWND itself is rounded by DWM via
// DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND so the sliver between the
// card's rounded corners and the HWND rectangle is never visible — DWM
// clips the compositor output to a rounded shape. DWMWA_BORDER_COLOR is
// set to DWMWA_COLOR_NONE to suppress the 1-dip DWM accent stroke.
//
// No classical Win32 frame is painted because WM_NCCALCSIZE is intercepted
// in the subclass and returns a zero non-client area. WinUI 3 reapplies
// WS_DLGFRAME / WS_EX_WINDOWEDGE on the top-level HWND even after we strip
// them (diagnosed 2026-04-17), so stripping bits is a losing game — we
// leave them on and simply deny Windows any NC area to paint into.
//
// Mouse proximity:
//   - WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA) gives a global
//     alpha covering the whole window content. Updated on every WM_INPUT
//     through a smoothstep — no polling.
//   - Raw Input (RIDEV_INPUTSINK) ensures WM_INPUT arrives even though the
//     HUD never owns focus.
//   - Subclass delegate kept in an instance field to survive GC.
public sealed partial class HudWindow : Window
{
    private const int HUD_WIDTH         = 272;
    private const int HUD_HEIGHT        =  78;
    private const int HUD_BOTTOM_MARGIN =  96;

    // Fade continu : alpha mappé sur la distance curseur/HUD via smoothstep.
    //   distance >= FAR_RADIUS → alpha MAX_ALPHA (HUD pleine)
    //   distance <= NEAR_RADIUS → alpha MIN_ALPHA (HUD estompée)
    //   entre les deux → smoothstep (t²(3-2t)).
    // Pas d'animation : chaque WM_INPUT recalcule et applique l'alpha cible.
    private const double NEAR_RADIUS_DIP = 10;
    private const double FAR_RADIUS_DIP  = 128;
    private const byte   MAX_ALPHA       = 255;
    private const byte   MIN_ALPHA       = 40;

    private readonly IntPtr _hwnd;

    private byte _currentAlpha = MAX_ALPHA;
    private bool _proximityActive;
    private bool _rawInputRegistered;

    private NativeMethods.SubclassProc? _subclassDelegate;
    private static readonly UIntPtr SubclassId = new(0x48554450); // "HUDP"

    private HudState _state = HudState.Hidden;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _messageHideTimer;

    // Raised when the HUD transitions between visible and hidden. Used by
    // HudOverlayManager to slide cards into / out of the main HUD's slot
    // (slot 0 drops onto the HUD's position while the HUD is hidden).
    public event EventHandler<bool>? MainHudVisibilityChanged;

    public IntPtr Hwnd             => _hwnd;
    public bool   IsMainHudShown   => _state != HudState.Hidden;

    public HudWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Explicit null defeats any auto-applied Mica / Acrylic backdrop on
        // recent WindowsAppSDK versions. Paired with DWMWA_SYSTEMBACKDROP_TYPE
        // = DWMSBT_NONE below as belt-and-suspenders — one is the WinUI API
        // surface, the other is the DWM Win32 guarantee.
        SystemBackdrop = null;

        // Title + icon kept consistent with the other windows. Title is not
        // visible (no title bar) but surfaces in alt-tab / Task View / debug.
        Title = Loc.Get("Hud_WindowTitle");
        IconAssets.ApplyToWindow(AppWindow);

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsResizable   = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        // Round the HWND at the DWM level, request the system default accent
        // border stroke, and force DWMSBT_NONE on the backdrop so DWM paints
        // nothing behind our opaque content. DWMWCP_ROUND matches Windows 11's
        // standard radius; DWMWA_COLOR_DEFAULT on DWMWA_BORDER_COLOR tells DWM
        // to paint the 1-dip system-native frame stroke around the rounded
        // HWND silhouette (tracks theme/accent) — this is the "Windows default
        // frame" visible on every first-party Win11 app. DWMSBT_NONE
        // explicitly disables Mica / Acrylic (belt-and-suspenders with
        // SystemBackdrop = null above).
        uint cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPref, sizeof(uint));
        uint borderColor = NativeMethods.DWMWA_COLOR_DEFAULT;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR,
            ref borderColor, sizeof(uint));
        uint backdropType = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdropType, sizeof(uint));

        // WS_EX_LAYERED is the precondition for SetLayeredWindowAttributes.
        // WS_EX_TOOLWINDOW keeps the HUD out of alt-tab. WS_EX_TRANSPARENT
        // forwards mouse hits beneath the window so it never steals focus.
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
        NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, MAX_ALPHA, NativeMethods.LWA_ALPHA);

        // Subclass MUST be installed before SWP_FRAMECHANGED — Windows sends
        // WM_NCCALCSIZE in response to FRAMECHANGED, and the subclass's
        // WM_NCCALCSIZE handler is what erases the non-client area. Installed
        // after the layered ex-style so LAYERED is in place when NC calc runs.
        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

        // SWP_FRAMECHANGED triggers WM_NCCALCSIZE, which now routes through
        // the subclass and returns a zero NC area. Net effect: the remaining
        // WS_DLGFRAME / WS_EX_WINDOWEDGE bits on the top-level HWND (which
        // WinUI 3 reapplies behind our back whenever we try to strip them)
        // have no NC area to paint into — they become inert.
        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        RegisterMouseRawInput();

        // Never destroyed — only path out is the tray Quit menu.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
    }

    private void RegisterMouseRawInput()
    {
        var rid = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage     = 0x02,
                dwFlags     = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget  = _hwnd,
            }
        };
        _rawInputRegistered = NativeMethods.RegisterRawInputDevices(
            rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    // ── Public API (thread-safe) ──────────────────────────────────────────────

    public void ShowPreparing()        => EnqueueUI(() => SetState(HudState.Charging));
    public void ShowRecording()        => EnqueueUI(() => SetState(HudState.Recording));
    public void SwitchToTranscribing() => EnqueueUI(() => SetState(HudState.Transcribing));
    public void SwitchToRewriting()    => EnqueueUI(() => SetState(HudState.Rewriting));

    // Durations are severity-driven (see UserFeedbackDurations). Success and
    // Informational clear fast, warnings and errors linger so the user has
    // time to read the actionable body.

    public void ShowError(string title, string body) =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Critical, title, body,
                UserFeedbackDurations.For(UserFeedbackSeverity.Error))));

    public void ShowPasted() =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Success, Loc.Get("Hud_Pasted_Title"), string.Empty,
                UserFeedbackDurations.Success)));

    // "Copied to clipboard" is a *success* outcome — the transcription
    // landed on the clipboard, which is the default flow when
    // AutoPasteEnabled is off (ship default). The green checkmark matches
    // the user's model: the operation succeeded. "Ctrl+V where you want it"
    // is a next-step hint, not a failure notice.
    public void ShowCopied() =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Success,
                Loc.Get("Hud_Copied_Title"), Loc.Get("Hud_Copied_Hint"),
                UserFeedbackDurations.Success)));

    public void ShowUserFeedback(UserFeedback fb)
    {
        MessageKind kind = fb.Severity switch
        {
            UserFeedbackSeverity.Info     => MessageKind.Informational,
            UserFeedbackSeverity.Warning  => MessageKind.Warning,
            _                              => MessageKind.Critical,
        };
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(kind, fb.Title, fb.Body,
                UserFeedbackDurations.For(fb.Severity))));
    }

    public void Hide() => EnqueueUI(() => SetState(HudState.Hidden));

    // Forward mic RMS samples (20 Hz, engine recording thread) to the chrono
    // control without marshalling. HudChrono.UpdateAudioLevel writes to a
    // CompositionPropertySet scalar, which is thread-safe by Composition's
    // contract — going through the dispatcher would add latency for no gain.
    // Safe to call at any state: UpdateAudioLevel is a no-op when the
    // recording outline isn't attached.
    public void OnAudioLevel(float rms) => Chrono.UpdateAudioLevel(rms);

    // Blocking variant: explicit rendezvous between the transcribe thread
    // and the UI thread. Called just before PasteFromClipboard so SW_HIDE
    // is effective before SendInput queues the Ctrl+V — otherwise the
    // hide can redistribute activation while the keystrokes are in flight
    // and the paste lands in the wrong target.
    public void HideSync()
    {
        if (DispatcherQueue.HasThreadAccess) { SetState(HudState.Hidden); return; }
        var done = new ManualResetEventSlim();
        bool enqueued = DispatcherQueue.TryEnqueueOrLog(() =>
        {
            try { SetState(HudState.Hidden); } finally { done.Set(); }
        }, LogSource.Hud, "HideSync");

        // Si l'enqueue a échoué (queue fermée pendant teardown), on évite
        // le Wait infini en libérant immédiatement. Le HUD ne sera pas
        // caché — mais le caller (transcribe thread) doit continuer pour
        // que le paste suive son cours.
        if (!enqueued) { done.Set(); return; }

        // Timeout défensif : SetState est microseconds en temps normal,
        // mais si le UI thread est bloqué (composition glitch, deadlock
        // externe), on libère le caller plutôt que de hang la pipeline.
        // Le paste sera émis sans le rendezvous Hide → risque de race
        // documenté dans docs/reference--paste-behavior--0.1.md, accepté en cas pathologique.
        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            LogService.Instance.Warning(LogSource.Hud, "HideSync timeout — UI thread didn't process within 5s, paste proceeding without Hide rendezvous");
        }
    }

    // ── State dispatcher ──────────────────────────────────────────────────────
    //
    // Single entry point for all UI transitions. Marshals control visibility,
    // forwards to the control's ApplyState / Show, shows the (fixed-size)
    // window, and arms the auto-hide timer for messages.

    private void SetState(HudState next, MessagePayload? msg = null)
    {
        // Overlay disabled in Settings → no-op for any *visible* state. Hidden
        // still runs so an in-flight HUD gets cleared if the user toggles.
        if (next != HudState.Hidden &&
            !Settings.SettingsService.Instance.Current.Overlay.Enabled)
        {
            return;
        }

        bool wasShown = _state != HudState.Hidden;
        _state = next;
        bool isShown = _state != HudState.Hidden;

        _messageHideTimer?.Stop();

        if (wasShown != isShown)
            MainHudVisibilityChanged?.Invoke(this, isShown);

        switch (next)
        {
            case HudState.Hidden:
                Chrono.ApplyState(HudState.Hidden);
                Chrono.Visibility  = Visibility.Visible;
                Message.Visibility = Visibility.Collapsed;
                _proximityActive   = false;
                SetAlphaImmediate(MAX_ALPHA);
                IconAssets.ApplyToWindow(AppWindow, recording: false);
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
                return;

            case HudState.Message:
                if (msg is null)
                    throw new ArgumentNullException(nameof(msg), "Message state requires a payload");
                Chrono.ApplyState(HudState.Hidden);
                Chrono.Visibility  = Visibility.Collapsed;
                Message.Visibility = Visibility.Visible;
                Message.Show(msg);
                IconAssets.ApplyToWindow(AppWindow, recording: false);
                ShowNoActivate();
                SetAlphaImmediate(MAX_ALPHA);
                _proximityActive = Settings.SettingsService.Instance.Current.Overlay.FadeOnProximity;
                if (_proximityActive) UpdateProximity();
                ArmMessageHideTimer(msg.Duration);
                return;

            case HudState.Charging:
            case HudState.Recording:
            case HudState.Transcribing:
            case HudState.Rewriting:
                Chrono.ApplyState(next);
                Message.Visibility = Visibility.Collapsed;
                Chrono.Visibility  = Visibility.Visible;
                IconAssets.ApplyToWindow(AppWindow, recording: next == HudState.Recording);
                ShowNoActivate();
                SetAlphaImmediate(MAX_ALPHA);
                _proximityActive = Settings.SettingsService.Instance.Current.Overlay.FadeOnProximity;
                if (_proximityActive) UpdateProximity();
                return;
        }
    }

    private void ArmMessageHideTimer(TimeSpan duration)
    {
        _messageHideTimer ??= DispatcherQueue.CreateTimer();
        _messageHideTimer.Stop();
        _messageHideTimer.Interval = duration;
        _messageHideTimer.IsRepeating = false;
        _messageHideTimer.Tick -= OnMessageHideTick;
        _messageHideTimer.Tick += OnMessageHideTick;
        _messageHideTimer.Start();
    }

    private void OnMessageHideTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        SetState(HudState.Hidden);
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private void EnqueueUI(Action a)
    {
        if (DispatcherQueue.HasThreadAccess) a();
        else DispatcherQueue.TryEnqueueOrLog(() => a(), LogSource.Hud, "ui action");
    }

    // Pixel rect the HUD would occupy at the current DPI + work area +
    // Overlay.Position setting, regardless of visibility. HudOverlayManager
    // reads this to lay out the stack even when the HUD itself is hidden.
    public Windows.Graphics.RectInt32 GetRectPx()
    {
        var wa = DisplayArea.Primary.WorkArea;

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;

        int w = (int)Math.Round(HUD_WIDTH  * scale);
        int h = (int)Math.Round(HUD_HEIGHT * scale);
        int margin = (int)Math.Round(HUD_BOTTOM_MARGIN * scale);

        // HUD centered horizontally by design (mirrors native Win11 HUDs —
        // volume, brightness, screen capture). Only vertical anchor is user-
        // configurable. StartsWith covers legacy corner values from older
        // settings.json files.
        string position = Settings.SettingsService.Instance.Current.Overlay.Position ?? "";
        int x = wa.X + (wa.Width - w) / 2;
        int y = position.StartsWith("Top")
            ? wa.Y + margin
            : wa.Y + wa.Height - h - margin;

        return new Windows.Graphics.RectInt32(x, y, w, h);
    }

    private void ShowNoActivate()
    {
        // Recomputed on every show: a Windows DPI scale change between two
        // dictations (125% → 150%) is reflected immediately.
        var rect = GetRectPx();
        AppWindow.MoveAndResize(rect);

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── Subclass: WM_NCCALCSIZE (no-frame) + WM_INPUT (proximity) ─────────────

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        // Erase the non-client area. With wParam=TRUE, lParam points to a
        // NCCALCSIZE_PARAMS whose rgrc[0] holds the proposed window rect;
        // returning 0 and leaving that rect unchanged tells Windows "the
        // client area covers the full window". No caption, no frame, no
        // 3D edge. wParam=FALSE hits here with a plain RECT — same contract,
        // we leave it as-is. Neutralizes WS_DLGFRAME + WS_EX_WINDOWEDGE
        // that WinUI 3 reapplies to the top-level behind our back.
        //
        // Verified 2026-04-17: removing this handler (even with
        // ExtendsContentIntoTitleBar already off) brings the rectangular
        // outline back immediately. The handler is load-bearing.
        if (uMsg == NativeMethods.WM_NCCALCSIZE)
        {
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_INPUT && _proximityActive)
        {
            UpdateProximity();
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Proximity: distance → alpha via smoothstep ────────────────────────────

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
        if (alpha != _currentAlpha) SetAlphaImmediate(alpha);
    }

    private void SetAlphaImmediate(byte alpha)
    {
        _currentAlpha = alpha;
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }
}
