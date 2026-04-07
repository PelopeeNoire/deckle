using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

public sealed partial class HudWindow : Window
{
    // Dimensions Figma (node 64:1699) : 314 × 78.
    private const int HUD_WIDTH  = 314;
    private const int HUD_HEIGHT = 78;
    private const int HUD_BOTTOM_MARGIN = 64; // légèrement au-dessus de la taskbar

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _clockTimer;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    // Brushes créés sur le thread UI (constructeur). Piège connu : instancier
    // un SolidColorBrush depuis un thread de fond lève RPC_E_WRONG_THREAD.
    private readonly SolidColorBrush _recordingBrush;
    private readonly SolidColorBrush _transcribingBrush;

    public HudWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        _recordingBrush    = new SolidColorBrush(Colors.IndianRed);
        _transcribingBrush = new SolidColorBrush(Colors.Gray);

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

        // Mica : fond translucide natif Windows 11. Le Border interne (CornerRadius=7)
        // donne la forme arrondie ; le Mica le traverse via la racine transparente.
        SystemBackdrop = new MicaBackdrop();

        // Jamais détruite — sortie unique via tray Quitter.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };

        // Chrono 100 ms (non exact, peu importe : affichage).
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    // ── API publique (thread-safe) ────────────────────────────────────────────

    public void ShowRecording()
    {
        EnqueueUI(() =>
        {
            StatusDot.Fill = _recordingBrush;
            TranscribeRing.IsActive  = false;
            TranscribeRing.Visibility = Visibility.Collapsed;

            _stopwatch.Restart();
            UpdateClock();
            _clockTimer.Start();

            ShowNoActivate();
        });
    }

    public void SwitchToTranscribing()
    {
        EnqueueUI(() =>
        {
            StatusDot.Fill = _transcribingBrush;
            TranscribeRing.Visibility = Visibility.Visible;
            TranscribeRing.IsActive   = true;
            // Le chrono continue — il reflète le temps total de la session.
        });
    }

    public void Hide()
    {
        EnqueueUI(() =>
        {
            _clockTimer.Stop();
            _stopwatch.Stop();
            TranscribeRing.IsActive   = false;
            TranscribeRing.Visibility = Visibility.Collapsed;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        });
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
        {
            var wa = DisplayArea.Primary.WorkArea;

            // AppWindow.MoveAndResize attend des pixels physiques. HUD_WIDTH/HEIGHT
            // sont en unités logiques (Figma 100% = 96 DPI). On multiplie par le
            // facteur d'échelle du moniteur courant pour rester visuellement
            // identique à 100%, 125%, 150%…
            uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
            double scale = dpi / 96.0;
            int w = (int)Math.Round(HUD_WIDTH  * scale);
            int h = (int)Math.Round(HUD_HEIGHT * scale);
            int margin = (int)Math.Round(HUD_BOTTOM_MARGIN * scale);

            int x = wa.X + (wa.Width  - w) / 2;
            int y = wa.Y +  wa.Height - h - margin;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
    }

    private void UpdateClock()
    {
        // Tâche 2 : layout statique, chrono figé à 00.00.00 (Runs en XAML).
        // La logique runtime (timer 33 ms, coloration des chiffres actifs) arrive à la Tâche 3.
    }
}
