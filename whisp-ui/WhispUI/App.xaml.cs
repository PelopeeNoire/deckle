namespace WhispUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private AnchorWindow? _anchor;
    private LogWindow? _logWindow;

    // Accesseur statique pour que les autres Windows/Pages puissent logger
    // directement dans la LogWindow UI (pas seulement dans %TEMP%\whisp-debug.log
    // via DebugLog). Utilisé par SettingsWindow et WhisperPage pour instrumenter
    // les actions utilisateur. Peut être null pendant le tout début du boot ;
    // chaque appel doit donc utiliser le null-conditional `App.Log?.`.
    public static LogWindow? Log => (Current as App)?._logWindow;
    internal static SettingsWindow? SettingsWin => (Current as App)?._settingsWindow;
    private SettingsWindow? _settingsWindow;
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

        // SettingsWindow créée une fois, jamais détruite. Pas de Show initial :
        // ouverte uniquement à la demande via tray. L'item footer "Logs" de la
        // NavigationView ouvre la LogWindow partagée via ce callback.
        _settingsWindow = new SettingsWindow
        {
            OnShowLogsRequested = () => _logWindow.ShowAndActivate(),
        };

        // HudWindow créée une fois, jamais détruite. Pas de Show initial : le
        // constructeur capture l'HWND et pose subclass / raw input / styles
        // étendus directement sur le handle natif — aucun besoin d'afficher la
        // fenêtre pour que ça marche. Le premier ShowRecording déclenchera
        // ShowNoActivate, qui positionne bas-centre et appelle SW_SHOWNOACTIVATE.
        _hudWindow = new HudWindow();

        _tray = new TrayIconManager
        {
            OnShowLogs        = () => _logWindow.ShowAndActivate(),
            OnShowSettings    = () => _settingsWindow.ShowAndActivate(),
            // Clic gauche tray = toggle transcription via le même chemin que la
            // hotkey standard. Permet de lancer à la souris avec une seule main.
            OnToggleRecording = () => OnHotkey(NativeMethods.HOTKEY_ID_TRANSCRIBE),
            OnQuit            = () => QuitApp(),
        };

        // Events moteur → UI. LogWindow.Log/LogError et TrayIconManager.UpdateStatus
        // sont appelés depuis des threads de fond ; LogWindow marshale en interne
        // via DispatcherQueue, UpdateStatus n'appelle que Shell_NotifyIcon (thread-safe).
        _engine.StatusChanged += status =>
        {
            _tray.UpdateStatus(status);
            _logWindow.Log($"[STATUS] {status}");
            // Beacon "icône d'app" du LogWindow : rouge enregistrement, gris idle.
            _logWindow.SetRecordingState(status == "Enregistrement...");

            // HUD : piloté par la transition de statut. Thread de fond → HudWindow
            // marshale en interne via DispatcherQueue.
            if (status == "Enregistrement...")
                _hudWindow.ShowRecording();
            else if (status == "Transcription en cours...")
                _hudWindow.SwitchToTranscribing();
        };
        _engine.LogVerboseLine       += msg => _logWindow.LogVerbose(msg);
        _engine.LogLine              += msg => _logWindow.Log(msg);
        _engine.LogStepLine          += msg => _logWindow.LogStep(msg);
        _engine.LogWarningLine       += msg => _logWindow.LogWarning(msg);
        _engine.LogErrorLine         += msg => _logWindow.LogError(msg);
        _engine.TranscriptionFinished += () => _hudWindow.Hide();
        // Rendez-vous synchrone juste avant le paste : on cache le HUD et on
        // attend que SW_HIDE soit effectif côté thread UI avant que le moteur
        // n'envoie le SendInput. Évite la race où Hide() (déclenché en async
        // après le paste) redistribue l'activation pendant que le Ctrl+V est
        // encore dans la queue d'input du thread cible.
        _engine.OnReadyToPaste = () => _hudWindow.HideSync();

        _anchor = new AnchorWindow(_tray, OnHotkey);

        _anchor.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 100, 100));
        _anchor.AppWindow.Show(false);

        // Appliquer le thème sauvegardé (System/Light/Dark).
        ApplyTheme(Settings.SettingsService.Instance.Current.Appearance.Theme);

        // Si lancé avec --settings (restart depuis les Settings), rouvrir
        // automatiquement la fenêtre Settings sur la bonne page.
        var cliArgs = Environment.GetCommandLineArgs();
        int settingsIdx = Array.IndexOf(cliArgs, "--settings");
        if (settingsIdx >= 0)
        {
            string? pageTag = settingsIdx + 1 < cliArgs.Length
                ? cliArgs[settingsIdx + 1]
                : null;
            DebugLog.Write("APP", $"--settings flag detected, page={pageTag ?? "(default)"}");
            _settingsWindow?.ShowAndActivate(pageTag);
        }
    }

    // ── Theme ────────────────────────────────────────────────────────────────
    //
    // Pose RequestedTheme sur le Content (FrameworkElement racine) de chaque
    // fenêtre connue. ElementTheme.Default = suivre le système.
    // Appelé au boot (OnLaunched) et quand l'utilisateur change le thème
    // dans GeneralPage.

    public static void ApplyTheme(string themeName)
    {
        var theme = themeName switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "Dark"  => Microsoft.UI.Xaml.ElementTheme.Dark,
            _       => Microsoft.UI.Xaml.ElementTheme.Default,
        };

        if (Current is not App app) return;

        foreach (var window in new Microsoft.UI.Xaml.Window?[]
                     { app._settingsWindow, app._logWindow, app._hudWindow, app._anchor })
        {
            if (window?.Content is Microsoft.UI.Xaml.FrameworkElement fe)
                fe.RequestedTheme = theme;
        }
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

    // ── Restart depuis les Settings ─────────────────────────────────────────
    //
    // Lance un nouveau process WhispUI avec --settings pour que les Settings
    // se rouvrent automatiquement au boot, puis shutdown propre du process
    // courant via QuitApp().
    public static void RestartApp(string? pageTag = null)
    {
        DebugLog.Write("APP", "Restart requested");
        var exePath = Environment.ProcessPath;
        if (exePath is not null)
        {
            var args = pageTag is not null
                ? $"--settings {pageTag}"
                : "--settings";
            DebugLog.Write("APP", $"Starting new process: {exePath} {args}");
            System.Diagnostics.Process.Start(exePath, args);
        }

        if (Current is App app)
            app.QuitApp();
    }

    private void OnHotkey(int hotkeyId)
    {
        if (_engine is null) return;

        bool useLlm = hotkeyId == NativeMethods.HOTKEY_ID_REWRITE;

        if (!_engine.IsRecording)
        {
            IntPtr target = NativeMethods.GetForegroundWindow();
            DebugLog.Write("HOTKEY", $"start id={hotkeyId} target={target}");
            string desc = Win32Util.DescribeHwnd(target);
            _logWindow?.LogStep($"Hotkey start (id={hotkeyId}{(useLlm ? ", LLM" : "")}) → {desc}");
            if (Win32Util.GetFocusedClass(target) is null)
                _logWindow?.LogWarning("Cible sans focus clavier — le paste risque d'échouer");
            _engine.StartRecording(useLlm: useLlm, shouldPaste: true, pasteTarget: target);
        }
        else
        {
            DebugLog.Write("HOTKEY", $"stop id={hotkeyId}");
            _logWindow?.LogStep("Hotkey stop");
            _engine.StopRecording();
        }
    }
}
