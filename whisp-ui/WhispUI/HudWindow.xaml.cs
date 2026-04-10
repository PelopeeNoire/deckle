using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace WhispUI;

// ─── HUD bas-centre ──────────────────────────────────────────────────────────
//
// Window WinUI 3 ~320×64, affichée bas-centre de l'écran principal.
// Ne vole JAMAIS le focus : Show via SW_SHOWNOACTIVATE + SetWindowPos NOACTIVATE.
// Créée une fois dans App.OnLaunched, jamais détruite (Closing→Cancel).
//
// Cycle de vie :
//   StatusChanged "Enregistrement..."       → ShowRecording()
//   StatusChanged "Transcription en cours..." → SwitchToTranscribing()
//   TranscriptionFinished                   → Hide()
//
// Tous les appels publics sont thread-safe (marshal via DispatcherQueue).
//
// Proximité souris :
//   - WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA) → alpha global
//     qui couvre Mica + content. Anim manuelle via DispatcherTimer (~16 ms).
//   - Source d'événements : Raw Input (WM_INPUT) avec RIDEV_INPUTSINK.
//     Pas de polling — on ne consomme rien quand la souris est immobile.
//   - Subclass sur le HWND pour intercepter WM_INPUT (pattern identique à
//     TrayIconManager : délégué en champ d'instance pour éviter le GC).

public sealed partial class HudWindow : Window
{
    // Dimensions Figma (node 64:1699) : 314 × 78.
    private const int HUD_WIDTH  = 314;
    private const int HUD_HEIGHT = 78;
    private const int HUD_BOTTOM_MARGIN = 96; // au-dessus de la taskbar

    // ── Proximité souris ──────────────────────────────────────────────────────
    // Fade continu : alpha mappé sur la distance curseur/HUD via smoothstep.
    //   distance >= FAR_RADIUS → alpha MAX_ALPHA (plafond, HUD "pleine")
    //   distance <= NEAR_RADIUS → alpha MIN_ALPHA (plancher, HUD estompée)
    //   entre les deux → smoothstep (t²(3-2t)), courbe douce sans cassure aux bords.
    // Pas d'hystérésis : la transition est continue, donc pas de risque de flicker.
    // Pas d'animation : chaque WM_INPUT (souris à ~125 Hz) recalcule et applique
    // directement l'alpha cible. La fluidité vient de la fréquence des events.
    private const double NEAR_RADIUS_DIP = 10;   // quasi-touchant → alpha MIN
    private const double FAR_RADIUS_DIP  = 128;  // ≥ 128 DIPs → alpha MAX
    private const byte   MAX_ALPHA       = 255;  // HUD pleinement visible (loin)
    private const byte   MIN_ALPHA       = 40;   // HUD estompée mais pas invisible (proche)

    private readonly IntPtr _hwnd;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private bool _clockRenderingHooked;

    // Cache des derniers chiffres affichés : évite de réécrire un Run.Text
    // identique à chaque tick (les centièmes changent à 30 Hz, mais minutes
    // et secondes ne changent qu'occasionnellement).
    private int _lastMin = -1;
    private int _lastSec = -1;
    private int _lastCs  = -1;

    private byte _currentAlpha = MAX_ALPHA;

    // Subclass : délégué en champ d'instance (jamais lambda locale, GC sinon).
    private NativeMethods.SubclassProc? _subclassDelegate;
    private static readonly UIntPtr SubclassId = new(0x48554450); // "HUDP"

    private bool _proximityActive; // true entre Show et Hide ; gate du subclass proc
    private bool _rawInputRegistered;

    // Brushes créés sur le thread UI (constructeur). Piège connu : instancier
    // un SolidColorBrush depuis un thread de fond lève RPC_E_WRONG_THREAD.
    private readonly SolidColorBrush _recordingBrush;
    private readonly SolidColorBrush _transcribingBrush;

    // Chiffres déjà passés au moins une fois : peints avec le rouge "erreur"
    // Windows (SystemFillColorCriticalBrush), qui s'adapte light/dark via
    // theme resources. Re-résolu sur RootGrid.ActualThemeChanged pour suivre
    // un changement de thème runtime (les Run.Foreground assignés en code
    // ne réagissent pas aux ThemeResource binding).
    private Brush _digitAccentBrush = null!;
    private bool _tMin1, _tMin2, _tSec1, _tSec2, _tCs1, _tCs2;

    public HudWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Titre + icône cohérents avec les autres fenêtres de l'app.
        // Le titre n'est pas visible (pas de title bar) mais sert dans
        // alt-tab / Task View / outils de debug Windows.
        Title = "WhispUI Recording";
        IconAssets.ApplyToWindow(AppWindow);

        _recordingBrush    = new SolidColorBrush(Colors.IndianRed);
        _transcribingBrush = new SolidColorBrush(Colors.Gray);

        _digitAccentBrush = ResolveCriticalBrush();
        // Re-résoudre sur theme switch + ré-appliquer aux chiffres déjà allumés.
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            _digitAccentBrush = ResolveCriticalBrush();
            if (_tMin1) Min1.Foreground = _digitAccentBrush;
            if (_tMin2) Min2.Foreground = _digitAccentBrush;
            if (_tSec1) Sec1.Foreground = _digitAccentBrush;
            if (_tSec2) Sec2.Foreground = _digitAccentBrush;
            if (_tCs1)  Cs1.Foreground  = _digitAccentBrush;
            if (_tCs2)  Cs2.Foreground  = _digitAccentBrush;
        };

        // Présentation : pas de barre titre, pas de resize/min/max.
        ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsResizable   = false;
        presenter.IsAlwaysOnTop = true; // topmost permanent, ne vole pas le focus
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor         = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;

        // DesktopAcrylicBackdrop : matériau canonique des fenêtres transient Windows 11
        // (flyouts, menus contextuels, dialogs, notifications). DWM appaire ce backdrop
        // au rendu système popup : Acrylic + ombre portée Shell + rayon + stroke, tout
        // natif, tout automatique, suit light/dark. À préférer à MicaBackdrop pour toute
        // surface éphémère comme une HUD — Mica est réservé aux fenêtres app longue vie.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // Signal DWM explicite : DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW.
        // C'est la bonne intention cote DWM pour une fenetre transient popup, mais
        // EN PRATIQUE ca ne donne pas l'ombre "Shell Shadows" riche qu'on voit sur
        // les menus contextuels de l'Explorer : ces menus sont des HWND de classe
        // popup systeme dessines par le shell via un chemin DWM prive, auquel une
        // WinUI 3 Window unpackaged n'a pas acces. Validation runtime 2026-04-09 :
        // l'appel ci-dessous n'ameliore pas visiblement l'ombre mais reste en place
        // parce qu'il exprime la bonne intention cote doc officielle (DWMSBT_*).
        int backdropType = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd,
            NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdropType,
            sizeof(int));

        // Marquer la fenêtre comme layered et set alpha initial à 255 (opaque).
        // Sans WS_EX_LAYERED, SetLayeredWindowAttributes est sans effet.
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
        NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, MAX_ALPHA, NativeMethods.LWA_ALPHA);

        // Subclass HWND pour intercepter WM_INPUT (proximité souris event-driven).
        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

        // Enregistrer le device souris en RIDEV_INPUTSINK : reçoit les WM_INPUT
        // même quand le HUD n'a pas le focus (et il ne l'a jamais, par design).
        RegisterMouseRawInput();

        // Jamais détruite — sortie unique via tray Quitter.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };

        // Chrono : pas de DispatcherTimer (jitter visible quand le thread UI est
        // chargé par les WM_INPUT/SetLayeredWindowAttributes). On utilise
        // CompositionTarget.Rendering qui fire à chaque frame du compositor
        // (vsync), donc cadence parfaitement régulière. Branché/débranché dans
        // ShowRecording / Hide via _clockRenderingHooked.
    }

    // Résout SystemFillColorCriticalBrush via Application.Resources. Cette
    // ressource pointe sur la variante (light/dark) active au moment de l'appel.
    // → on appelle aussi sur ActualThemeChanged pour rester aligné.
    private Brush ResolveCriticalBrush() =>
        (Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush)
        ?? new SolidColorBrush(Microsoft.UI.Colors.IndianRed);

    private void RegisterMouseRawInput()
    {
        var rid = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // Generic Desktop Controls
                usUsage     = 0x02, // Mouse
                dwFlags     = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget  = _hwnd,
            }
        };
        _rawInputRegistered = NativeMethods.RegisterRawInputDevices(
            rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    // ── API publique (thread-safe) ────────────────────────────────────────────

    public void ShowRecording()
    {
        // Overlay désactivé dans les Settings → ne pas afficher le HUD.
        if (!Settings.SettingsService.Instance.Current.Overlay.Enabled)
            return;

        EnqueueUI(() =>
        {
            StatusDot.Fill = _recordingBrush;
            TranscribeRing.IsActive  = false;
            TranscribeRing.Visibility = Visibility.Collapsed;

            _stopwatch.Restart();
            _lastMin = _lastSec = _lastCs = -1; // force repaint complet au show
            // Reset coloration : on efface le Foreground local des Run pour
            // qu'ils héritent de ClockText.Foreground (lié à ThemeResource
            // TextFillColorPrimaryBrush, donc auto-réactif au thème). Tout
            // chiffre qui changera ensuite passera en accent rouge et y restera.
            _tMin1 = _tMin2 = _tSec1 = _tSec2 = _tCs1 = _tCs2 = false;
            // Reset .Text à "0" sinon UpdateClock voit RunX.Text != "0" (résiduel
            // de la session précédente) et lève le flag rouge alors que la valeur
            // courante est 0.
            Min1.Text = Min2.Text = "0";
            Sec1.Text = Sec2.Text = "0";
            Cs1.Text  = Cs2.Text  = "0";
            Min1.ClearValue(TextElement.ForegroundProperty);
            Min2.ClearValue(TextElement.ForegroundProperty);
            Sec1.ClearValue(TextElement.ForegroundProperty);
            Sec2.ClearValue(TextElement.ForegroundProperty);
            Cs1.ClearValue(TextElement.ForegroundProperty);
            Cs2.ClearValue(TextElement.ForegroundProperty);
            UpdateClock();
            if (!_clockRenderingHooked)
            {
                CompositionTarget.Rendering += OnClockRendering;
                _clockRenderingHooked = true;
            }

            IconAssets.ApplyToWindow(AppWindow, recording: true);
            ShowNoActivate();

            // Reset alpha à 255 et arme la proximité (si fade activé dans les Settings).
            SetAlphaImmediate(MAX_ALPHA);
            _proximityActive = Settings.SettingsService.Instance.Current.Overlay.FadeOnProximity;

            if (_proximityActive)
                UpdateProximity();
        });
    }

    public void SwitchToTranscribing()
    {
        EnqueueUI(() =>
        {
            StatusDot.Fill = _transcribingBrush;
            TranscribeRing.Visibility = Visibility.Visible;
            TranscribeRing.IsActive   = true;
            IconAssets.ApplyToWindow(AppWindow, recording: false);

            // Fige le chrono à la fin de l'enregistrement : la valeur affichée
            // reste visible pendant la transcription, mais ne tourne plus.
            // On stoppe aussi le rendering hook — inutile de tourner à 60 Hz
            // pour réafficher la même valeur.
            _stopwatch.Stop();
            if (_clockRenderingHooked)
            {
                CompositionTarget.Rendering -= OnClockRendering;
                _clockRenderingHooked = false;
            }
            UpdateClock(); // dernière passe pour caler les Runs sur le temps figé
        });
    }

    public void Hide() => EnqueueUI(HideCore);

    // Variante bloquante : rendez-vous explicite entre un thread de fond
    // (transcribe) et le thread UI. Utilisée juste avant PasteFromClipboard
    // pour garantir que le HUD est entièrement caché AVANT que SendInput
    // n'enfile le Ctrl+V — sinon SW_HIDE peut redistribuer l'activation
    // pendant que les frappes sont en vol et le coller atterrit ailleurs.
    public void HideSync()
    {
        if (DispatcherQueue.HasThreadAccess) { HideCore(); return; }
        var done = new ManualResetEventSlim();
        DispatcherQueue.TryEnqueue(() =>
        {
            try { HideCore(); } finally { done.Set(); }
        });
        done.Wait();
    }

    private void HideCore()
    {
        _proximityActive = false;
        SetAlphaImmediate(MAX_ALPHA); // reset pour la prochaine session

        if (_clockRenderingHooked)
        {
            CompositionTarget.Rendering -= OnClockRendering;
            _clockRenderingHooked = false;
        }
        _stopwatch.Stop();
        TranscribeRing.IsActive   = false;
        TranscribeRing.Visibility = Visibility.Collapsed;
        IconAssets.ApplyToWindow(AppWindow, recording: false);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    // ── Implémentation ────────────────────────────────────────────────────────

    private void EnqueueUI(Action a)
    {
        if (DispatcherQueue.HasThreadAccess) a();
        else DispatcherQueue.TryEnqueue(() => a());
    }

    private void ShowNoActivate()
    {
        // Recalculé à chaque show : si l'utilisateur change l'échelle Windows
        // (125% → 150%) entre deux dictées, le HUD s'adapte automatiquement.
        var wa = DisplayArea.Primary.WorkArea;

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;
        int w = (int)Math.Round(HUD_WIDTH  * scale);
        int h = (int)Math.Round(HUD_HEIGHT * scale);
        int margin = (int)Math.Round(HUD_BOTTOM_MARGIN * scale);

        string position = Settings.SettingsService.Instance.Current.Overlay.Position;
        int x, y;
        switch (position)
        {
            case "BottomRight":
                x = wa.X + wa.Width - w - margin;
                y = wa.Y + wa.Height - h - margin;
                break;
            case "TopCenter":
                x = wa.X + (wa.Width - w) / 2;
                y = wa.Y + margin;
                break;
            default: // BottomCenter
                x = wa.X + (wa.Width - w) / 2;
                y = wa.Y + wa.Height - h - margin;
                break;
        }
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnClockRendering(object? sender, object e) => UpdateClock();

    private void UpdateClock()
    {
        // Format MM.SS.cc — minutes modulo 100 (rolling silencieux à 100 min,
        // largement au-delà de toute dictée réaliste).
        var elapsed = _stopwatch.Elapsed;
        int totalMin = (int)elapsed.TotalMinutes;
        int min = totalMin % 100;
        int sec = elapsed.Seconds;
        int cs  = elapsed.Milliseconds / 10; // 0..99

        // Coloration : un chiffre qui change passe en accent et y reste pour
        // toute la session. Le 0 initial reste en blanc tant qu'il n'a pas bougé.
        // Les points (Runs sans nom) ne sont jamais touchés → blanc.
        if (min != _lastMin)
        {
            int d1 = min / 10, d2 = min % 10;
            if (Min1.Text != d1.ToString()) { Min1.Text = d1.ToString(); if (!_tMin1) { _tMin1 = true; Min1.Foreground = _digitAccentBrush; } }
            if (Min2.Text != d2.ToString()) { Min2.Text = d2.ToString(); if (!_tMin2) { _tMin2 = true; Min2.Foreground = _digitAccentBrush; } }
            _lastMin = min;
        }
        if (sec != _lastSec)
        {
            int d1 = sec / 10, d2 = sec % 10;
            if (Sec1.Text != d1.ToString()) { Sec1.Text = d1.ToString(); if (!_tSec1) { _tSec1 = true; Sec1.Foreground = _digitAccentBrush; } }
            if (Sec2.Text != d2.ToString()) { Sec2.Text = d2.ToString(); if (!_tSec2) { _tSec2 = true; Sec2.Foreground = _digitAccentBrush; } }
            _lastSec = sec;
        }
        if (cs != _lastCs)
        {
            int d1 = cs / 10, d2 = cs % 10;
            if (Cs1.Text != d1.ToString()) { Cs1.Text = d1.ToString(); if (!_tCs1) { _tCs1 = true; Cs1.Foreground = _digitAccentBrush; } }
            if (Cs2.Text != d2.ToString()) { Cs2.Text = d2.ToString(); if (!_tCs2) { _tCs2 = true; Cs2.Foreground = _digitAccentBrush; } }
            _lastCs = cs;
        }
    }

    // ── Subclass : interception WM_INPUT ──────────────────────────────────────

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_INPUT && _proximityActive)
        {
            // Pas besoin de parser le RAWINPUT : on veut juste la position absolue
            // courante du curseur (les RAWMOUSE deltas seraient inutiles ici).
            UpdateProximity();
        }
        else if (uMsg == NativeMethods.WM_NCACTIVATE)
        {
            // Force DWM a peindre la HUD comme active en permanence : ombre portee
            // "Shell Shadows / Active Window" riche (Win11) au lieu de l'ombre
            // inactive aplatie. Sans ca, la HUD est cote DWM perpetuellement
            // inactive (SW_SHOWNOACTIVATE + WS_EX_NOACTIVATE + jamais de focus
            // clavier) et herite des ombres "non active window". On reecrit
            // wParam=TRUE avant DefSubclassProc, lParam reste tel quel.
            return NativeMethods.DefSubclassProc(hWnd, uMsg, new IntPtr(1), lParam);
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Proximité : distance → alpha via smoothstep ───────────────────────────

    private void UpdateProximity()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        var pos  = AppWindow.Position; // pixels physiques
        var size = AppWindow.Size;
        int left   = pos.X;
        int top    = pos.Y;
        int right  = pos.X + size.Width;
        int bottom = pos.Y + size.Height;

        // Distance euclidienne curseur → rectangle (0 si dedans).
        int dx = cursor.X < left ? left - cursor.X : (cursor.X > right  ? cursor.X - right  : 0);
        int dy = cursor.Y < top  ? top  - cursor.Y : (cursor.Y > bottom ? cursor.Y - bottom : 0);
        double distancePx = Math.Sqrt(dx * dx + dy * dy);

        // Conversion seuils DIPs → pixels via le DPI du HUD.
        double scale   = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        double nearPx  = NEAR_RADIUS_DIP * scale;
        double farPx   = FAR_RADIUS_DIP  * scale;

        // Normalisation distance → t ∈ [0, 1] entre near et far.
        double t = (distancePx - nearPx) / (farPx - nearPx);
        if (t < 0.0) t = 0.0;
        if (t > 1.0) t = 1.0;

        // Smoothstep : t²(3-2t). Pente nulle aux bords, accélération au milieu.
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
