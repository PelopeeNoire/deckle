using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using WhispUI.Controls;
using WhispUI.Interop;
using WhispUI.Logging;

namespace WhispUI;

// ─── HUD bottom-center ───────────────────────────────────────────────────────
//
// WinUI 3 transient window, never destroyed. Hosts two UserControls:
//   - HudChrono  (314x78) for Charging / Recording / Transcribing / Rewriting
//   - HudMessage (272x78 card centered in a 400x160 window) for Pasted /
//                Copied / Error / UserFeedback feedback.
//
// This window owns only the technical shell: HWND, layered alpha, proximity
// fade subclass, lifecycle. All visual logic lives in the controls — the
// only state machine is the SetState dispatcher below.
//
// Lifecycle:
//   StatusChanged "Recording"          → ShowRecording()    → Recording
//   StatusChanged "Transcribing"       → SwitchToTranscribing() → Transcribing
//   StatusChanged "Rewriting (...)"    → SwitchToRewriting()    → Rewriting
//   TranscriptionFinished              → ShowPasted/Copied/Error → Message
//
// Mouse proximity:
//   - WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA) gives a global
//     alpha covering Acrylic + content. Updated on every WM_INPUT through a
//     smoothstep — no polling.
//   - Raw Input (RIDEV_INPUTSINK) ensures WM_INPUT arrives even though the
//     HUD never owns focus.
//   - Subclass delegate kept in an instance field to survive GC.
public sealed partial class HudWindow : Window
{
    // Chrono states (Charging / Recording / Transcribing / Rewriting): 314x78.
    private const int HUD_WIDTH         = 314;
    private const int HUD_HEIGHT        =  78;
    // Message state: window is grown to 400x160 so the 272x78 card sits
    // centered inside a transparent margin where the composite halo+drop
    // shadow can spill (cf. plan §E hybrid bleed strategy).
    private const int HUD_WIDTH_MESSAGE  = 400;
    private const int HUD_HEIGHT_MESSAGE = 160;
    // After the 650 ms Saturation animation ends, the shadow is in its
    // attenuated form. We let it dwell ~150 ms at full size, then retract
    // the window down to the bare card. The shadow stays attached but is
    // clipped by the smaller window — visually the halo "settles" while
    // the card snaps back to its standard size ("plaf puis ça revient").
    private const int HUD_WIDTH_MESSAGE_RETRACTED  = 272;
    private const int HUD_HEIGHT_MESSAGE_RETRACTED =  78;
    private static readonly TimeSpan MessageRetractDelay = TimeSpan.FromMilliseconds(800);
    // Below this threshold the message hides before the retract would happen
    // (e.g. Pasted at 500 ms) — skip the retract entirely so we don't waste a
    // resize on a window about to disappear.
    private static readonly TimeSpan MessageRetractMinDuration = TimeSpan.FromMilliseconds(1000);
    private const int HUD_BOTTOM_MARGIN  =  96;

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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _messageRetractTimer;
    private bool _messageRetracted;

    public HudWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Title + icon kept consistent with the other windows. Title is not
        // visible (no title bar) but surfaces in alt-tab / Task View / debug.
        Title = "WhispUI Recording";
        IconAssets.ApplyToWindow(AppWindow);

        ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsResizable   = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor         = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;

        // DesktopAcrylicBackdrop — canonical material for transient Win11
        // surfaces (flyouts, menus, dialogs, notifications). DWM matches
        // this backdrop with the system popup rendering pipeline.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW. The right
        // intent for a popup-class HWND. In practice it does not unlock
        // the rich Shell-shadow seen on Explorer context menus (those go
        // through a private DWM path inaccessible to unpackaged WinUI 3),
        // but the call stays as documented intent.
        int backdropType = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdropType,
            sizeof(int));

        // WS_EX_LAYERED is the precondition for SetLayeredWindowAttributes.
        // WS_EX_TOOLWINDOW keeps the HUD out of alt-tab. WS_EX_TRANSPARENT
        // forwards mouse hits beneath the window so it never steals focus.
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
        NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, MAX_ALPHA, NativeMethods.LWA_ALPHA);

        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

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

    public void ShowError(string title, string body) =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Critical, title, body, TimeSpan.FromSeconds(5))));

    public void ShowPasted() =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Success, "Pasted", string.Empty, TimeSpan.FromMilliseconds(500))));

    public void ShowCopied() =>
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(MessageKind.Informational,
                "Copied to clipboard", "Ctrl+V where you want it.",
                TimeSpan.FromSeconds(3))));

    public void ShowUserFeedback(UserFeedback fb)
    {
        (MessageKind kind, TimeSpan duration) = fb.Severity switch
        {
            UserFeedbackSeverity.Info     => (MessageKind.Informational, TimeSpan.FromSeconds(4)),
            UserFeedbackSeverity.Warning  => (MessageKind.Warning,       TimeSpan.FromSeconds(5)),
            _                              => (MessageKind.Critical,      TimeSpan.FromSeconds(5)),
        };
        EnqueueUI(() => SetState(HudState.Message,
            new MessagePayload(kind, fb.Title, fb.Body, duration)));
    }

    public void Hide() => EnqueueUI(() => SetState(HudState.Hidden));

    // Blocking variant: explicit rendezvous between the transcribe thread
    // and the UI thread. Called just before PasteFromClipboard so SW_HIDE
    // is effective before SendInput queues the Ctrl+V — otherwise the
    // hide can redistribute activation while the keystrokes are in flight
    // and the paste lands in the wrong target.
    public void HideSync()
    {
        if (DispatcherQueue.HasThreadAccess) { SetState(HudState.Hidden); return; }
        var done = new ManualResetEventSlim();
        DispatcherQueue.TryEnqueue(() =>
        {
            try { SetState(HudState.Hidden); } finally { done.Set(); }
        });
        done.Wait();
    }

    // ── State dispatcher ──────────────────────────────────────────────────────
    //
    // Single entry point for all UI transitions. Marshals the right control
    // visibility, forwards to the control's ApplyState / Show, drives the
    // window resize (chrono dims vs message dims), and arms the auto-hide
    // timer for messages.

    private void SetState(HudState next, MessagePayload? msg = null)
    {
        // Overlay disabled in Settings → no-op for any *visible* state. Hidden
        // still runs so an in-flight HUD gets cleared if the user toggles.
        if (next != HudState.Hidden &&
            !Settings.SettingsService.Instance.Current.Overlay.Enabled)
        {
            return;
        }

        _state = next;

        // Cancel any pending auto-hide / retract whenever we transition — a
        // fresh message rearms below; any other state cancels outright.
        _messageHideTimer?.Stop();
        _messageRetractTimer?.Stop();

        switch (next)
        {
            case HudState.Hidden:
                Chrono.ApplyState(HudState.Hidden);
                Chrono.Visibility  = Visibility.Visible;
                Message.Visibility = Visibility.Collapsed;
                _messageRetracted  = false;
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
                // Always start at full size — even if the previous message had
                // already retracted, we want the new one to "boom" again.
                _messageRetracted = false;
                Message.Show(msg);
                IconAssets.ApplyToWindow(AppWindow, recording: false);
                _proximityActive = false;
                ShowNoActivate();
                SetAlphaImmediate(MAX_ALPHA);
                ArmMessageHideTimer(msg.Duration);
                ArmMessageRetractTimer(msg.Duration);
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

    private void ArmMessageRetractTimer(TimeSpan totalDuration)
    {
        // Skip the retract for ultra-short messages (Pasted at 500 ms) — the
        // window would still be in its full-size phase when it hides anyway.
        if (totalDuration < MessageRetractMinDuration) return;

        _messageRetractTimer ??= DispatcherQueue.CreateTimer();
        _messageRetractTimer.Stop();
        _messageRetractTimer.Interval = MessageRetractDelay;
        _messageRetractTimer.IsRepeating = false;
        _messageRetractTimer.Tick -= OnMessageRetractTick;
        _messageRetractTimer.Tick += OnMessageRetractTick;
        _messageRetractTimer.Start();
    }

    private void OnMessageRetractTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        // Guard against state transitions that happened between Tick scheduling
        // and now (a new ShowError, a Hide…). Only retract if we're still
        // showing the same message.
        if (_state != HudState.Message) return;
        _messageRetracted = true;
        ShowNoActivate();
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private void EnqueueUI(Action a)
    {
        if (DispatcherQueue.HasThreadAccess) a();
        else DispatcherQueue.TryEnqueue(() => a());
    }

    private void ShowNoActivate()
    {
        // Recomputed on every show: a Windows DPI scale change between two
        // dictations (125% → 150%) is reflected immediately.
        var wa = DisplayArea.Primary.WorkArea;

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;

        bool isMessage = _state == HudState.Message;
        int widthDips, heightDips;
        if (isMessage && !_messageRetracted)
        {
            // Full phase — large window so the composite shadow can bleed
            // into the transparent margin around the 272x78 card.
            widthDips  = HUD_WIDTH_MESSAGE;
            heightDips = HUD_HEIGHT_MESSAGE;
        }
        else if (isMessage)
        {
            // Retracted phase — window snaps back to card dimensions. The
            // attenuated shadow keeps animating but is now clipped by the
            // window edges, which reads as the halo settling down.
            widthDips  = HUD_WIDTH_MESSAGE_RETRACTED;
            heightDips = HUD_HEIGHT_MESSAGE_RETRACTED;
        }
        else
        {
            widthDips  = HUD_WIDTH;
            heightDips = HUD_HEIGHT;
        }

        int w = (int)Math.Round(widthDips  * scale);
        int h = (int)Math.Round(heightDips * scale);
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
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── Subclass: WM_INPUT (proximity) + WM_NCACTIVATE (active shadow) ────────

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_INPUT && _proximityActive)
        {
            UpdateProximity();
        }
        else if (uMsg == NativeMethods.WM_NCACTIVATE)
        {
            // Force DWM to paint the HUD as active permanently — richer
            // "Active Window" shadow instead of the flattened inactive one.
            // The HUD never receives focus by design (SW_SHOWNOACTIVATE +
            // WS_EX_NOACTIVATE), so DWM otherwise treats it as inactive
            // forever. Override wParam=TRUE before delegating.
            return NativeMethods.DefSubclassProc(hWnd, uMsg, new IntPtr(1), lParam);
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
