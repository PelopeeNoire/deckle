namespace WhispUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private AnchorWindow? _anchor;
    private LogWindow? _logWindow;
    private HudWindow? _hudWindow;
    private TrayIconManager? _tray;
    private WhispEngine? _engine;

    public App()
    {
        InitializeComponent();

        // Filet de diagnostic — sans ça un crash dans un event WhispEngine
        // disparaît silencieusement.
        this.UnhandledException += (_, e) =>
        {
            DebugLog.Write("CRASH", $"{e.Exception.GetType().Name}: {e.Exception.Message}");
            DebugLog.Write("CRASH", e.Exception.StackTrace ?? "(no stack)");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            DebugLog.Write("CRASH-AD", $"{ex?.GetType().Name}: {ex?.Message}");
            DebugLog.Write("CRASH-AD", ex?.StackTrace ?? "(no stack)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DebugLog.Write("CRASH-TS", $"{e.Exception.GetType().Name}: {e.Exception.Message}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DebugLog.Write("APP", "OnLaunched");

        _engine = new WhispEngine();

        // LogWindow créée une fois, jamais détruite.
        _logWindow = new LogWindow();

        // HudWindow créée une fois, jamais détruite. Pré-initialisée hors écran
        // via Show(false) : l'arbre XAML se construit sans que la fenêtre soit
        // visible (le Show réel passera par ShowNoActivate après positionnement).
        _hudWindow = new HudWindow();
        _hudWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 320, 64));
        _hudWindow.AppWindow.Show(false);

        _tray = new TrayIconManager
        {
            OnShowLogs = () => _logWindow.ShowAndActivate(),
            OnQuit     = () => QuitApp(),
        };

        // Events moteur → UI. LogWindow.Log/LogError et TrayIconManager.UpdateStatus
        // sont appelés depuis des threads de fond ; LogWindow marshale en interne
        // via DispatcherQueue, UpdateStatus n'appelle que Shell_NotifyIcon (thread-safe).
        _engine.StatusChanged += status =>
        {
            _tray.UpdateStatus(status);
            _logWindow.Log($"[STATUS] {status}");

            // HUD : piloté par la transition de statut. Thread de fond → HudWindow
            // marshale en interne via DispatcherQueue.
            if (status == "Enregistrement...")
                _hudWindow.ShowRecording();
            else if (status == "Transcription en cours...")
                _hudWindow.SwitchToTranscribing();
        };
        _engine.LogLine              += msg => _logWindow.Log(msg);
        _engine.LogErrorLine         += msg => _logWindow.LogError(msg);
        _engine.TranscriptionFinished += () => _hudWindow.Hide();

        _anchor = new AnchorWindow(_tray, OnHotkey);

        _anchor.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 100, 100));
        _anchor.AppWindow.Show(false);
    }

    // ── Shutdown propre depuis tray > Quitter ────────────────────────────────
    //
    // Application.Current.Exit() ne suffit pas sur WinUI 3 unpackaged quand des
    // hooks natifs (SetWindowSubclass, RegisterHotKey, waveIn) sont actifs :
    // le process survit et l'icône tray reste fantôme faute de NIM_DELETE.
    //
    // Séquence : (1) Dispose tray → envoie NIM_DELETE + RemoveWindowSubclass.
    //            (2) Dispose engine → libère le ctx whisper et le pipeline.
    //            (3) Environment.Exit(0) → sortie brutale garantie. Les threads
    //                Record/Transcribe étant IsBackground=true, ils meurent avec.
    private void QuitApp()
    {
        DebugLog.Write("APP", "Shutdown requested");
        try { _tray?.Dispose();   } catch (Exception ex) { DebugLog.Write("APP", "tray dispose: " + ex.Message); }
        try { _engine?.Dispose(); } catch (Exception ex) { DebugLog.Write("APP", "engine dispose: " + ex.Message); }
        Environment.Exit(0);
    }

    private void OnHotkey(int hotkeyId)
    {
        if (_engine is null) return;

        bool useLlm = hotkeyId == NativeMethods.HOTKEY_ID_REWRITE;

        if (!_engine.IsRecording)
        {
            IntPtr target = NativeMethods.GetForegroundWindow();
            DebugLog.Write("HOTKEY", $"start id={hotkeyId} target={target}");
            _engine.StartRecording(useLlm: useLlm, shouldPaste: true, pasteTarget: target);
        }
        else
        {
            DebugLog.Write("HOTKEY", $"stop id={hotkeyId}");
            _engine.StopRecording();
        }
    }
}
