using Deckle.Interop;
using Deckle.Logging;
using Deckle.Logging.Sinks;
using Deckle.Shell;
using Deckle.Whisp;
using Deckle.Whisp.Setup;

namespace Deckle;

public partial class App : Microsoft.UI.Xaml.Application
{
    private static readonly LogService _log = LogService.Instance;

    private MessageOnlyHost? _messageHost;
    private HotkeyManager? _hotkeyManager;
    private LogWindow? _logWindow;

    internal static SettingsWindow? SettingsWin => (Current as App)?._settingsWindow;
    private SettingsWindow? _settingsWindow;
    private PlaygroundWindow? _playgroundWindow;
    private HudWindow? _hudWindow;
    private HudOverlayManager? _overlayManager;
    private TrayIconManager? _tray;
    private WhispEngine? _engine;

    // Last engine recording state, captured on every StatusChanged. Used
    // to seed PlaygroundWindow's beacon on lazy creation: when the user
    // opens Playground for the first time mid-recording, the beacon
    // reflects the current state without waiting for the next status
    // transition.
    private bool _lastRecordingState;

    // Current theme + caption button theme, kept in sync by ApplyTheme.
    // Lazy windows (LogWindow / SettingsWindow / PlaygroundWindow) read
    // these to apply the right palette at creation, so they appear with
    // the user's chosen theme on first open even though they missed the
    // boot ApplyTheme broadcast.
    private static Microsoft.UI.Xaml.ElementTheme _currentTheme =
        Microsoft.UI.Xaml.ElementTheme.Default;
    private static Microsoft.UI.Windowing.TitleBarTheme _currentTitleBarTheme =
        Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;

    public App()
    {
        InitializeComponent();

        // Diagnostic safety net — without this, a crash in a WhispEngine
        // event disappears silently. Any sink registered later in OnLaunched
        // picks these up (the JsonlFileSink writes them to app.jsonl). Events
        // raised before OnLaunched have no sinks yet and are dropped — there
        // are none of those in practice.
        this.UnhandledException += (_, e) =>
        {
            _log.Error(LogSource.Crash, $"{e.Exception.GetType().Name}: {e.Exception.Message}");
            _log.Error(LogSource.Crash, e.Exception.StackTrace ?? "(no stack)");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            _log.Error(LogSource.Crash, $"[AppDomain] {ex?.GetType().Name}: {ex?.Message}");
            _log.Error(LogSource.Crash, ex?.StackTrace ?? "(no stack)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Error(LogSource.Crash, $"[TaskScheduler] {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.SetObserved();
        };

        // Trace explicit du process-exit. Le sink JsonlFileSink flush à
        // chaque écriture (using StreamWriter), donc les events précédents
        // sont déjà sur disque — mais distinguer un shutdown propre d'un
        // crash brut dans les logs aide le post-mortem. Pas un dump, juste
        // un marqueur "on est sorti par cette voie".
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _log.Info(LogSource.App, "ProcessExit triggered");
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Cold-start instrumentation. Milestones accumulate into a local
        // list during construction and get flushed as a single aggregate
        // line through LogService at the end of OnLaunched — LogWindow
        // receives it under [APP]. A naive "one _log.Info per milestone"
        // approach would lose the earliest ones (LogService has no sink
        // before _logWindow is constructed and AddSink is called).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var milestones = new List<string>();
        void Milestone(string name) => milestones.Add($"{name} +{sw.ElapsedMilliseconds}ms");

        // Wire Deckle.Logging's gates to the host's TelemetrySettings BEFORE
        // attaching JsonlFileSink: the sink's first emit reads
        // TelemetryGates.Current to decide whether to land on disk, and an
        // unconfigured Logging defaults to the closed posture (every toggle
        // false, no override path). Without Configure here, the very first
        // log lines flushed below ("Paths initialized") would silently skip
        // the JSONL even when the user has the app log enabled.
        TelemetryGates.Configure(new AppTelemetryGates());

        // File sink first — captures every event from boot, including the
        // startup milestones flushed at the end of OnLaunched. Writes under
        // the telemetry storage directory (benchmark/ in dev layout, or
        // LocalState/telemetry/ in packaged mode — see AppPaths).
        TelemetryService.Instance.AddSink(new JsonlFileSink());
        Milestone("filesink");

        // Resolved paths logged once at boot — useful for support: tells us
        // where the app is looking for settings, models, native DLLs, and
        // telemetry. Touching any AppPaths member triggers the static ctor
        // that resolves <UserDataRoot> and creates the writable directories.
        //
        // Doctrine logging : Info = jalon en phrase Capital courte ;
        // détails techniques (chemins résolus) en Verbose miroir, lisible
        // en filtre All sans polluer Activity.
        _log.Info(LogSource.App, "Paths initialized");
        _log.Verbose(LogSource.App,
            $"paths | root={AppPaths.UserDataRoot}" +
            $" | settings={AppPaths.SettingsFilePath}" +
            $" | telemetry={AppPaths.TelemetryDirectory}" +
            $" | models={AppPaths.ModelsDirectory}" +
            $" | native={AppPaths.NativeDirectory}");

        // First-run gate — the engine ctor below loads the model immediately
        // and would throw DllNotFoundException without the native runtime
        // (libwhisper + ggml backends). There's no graceful degradation:
        // either the dependencies are in place, or we open the wizard, or
        // the user quits. The wizard provisions the natives via auto-download
        // (Deckle GitHub Release, NativeRuntime.CurrentBundle.Url) with a
        // local Browse... fallback, plus the chosen speech model from
        // HuggingFace.
        if (!NativeRuntime.IsInstalled() ||
            !SpeechModels.IsDefaultInstalled())
        {
            _log.Info(LogSource.Setup,
                $"first-run gate | natives_installed={NativeRuntime.IsInstalled()}" +
                $" | default_model_installed={SpeechModels.IsDefaultInstalled()}");
            var setup = new Shell.Setup.SetupWindow();
            setup.Body.Navigate(typeof(Shell.Setup.ChoicesPage), setup);
            setup.Activate();
            bool success = await setup.Completion;
            if (!success)
            {
                _log.Info(LogSource.Setup, "wizard cancelled — exiting");
                Environment.Exit(0);
                return;
            }
            Milestone("wizard");
        }

        _engine = new WhispEngine(new AppWhispEngineHost());
        Milestone("engine");

        // LogWindow lazy : instanciée à la première ouverture via
        // ShowLogWindowLazy(). Le sink est inscrit à ce moment-là, et
        // TelemetryService.Replay() rejoue l'historique du buffer
        // central pour que le viewer soit complet dès l'ouverture.
        // Évite de payer un swap chain DComp + visual tree DWM au boot
        // pour une fenêtre dont l'utilisateur n'a pas systématiquement
        // besoin. Les events boot sont préservés dans app.jsonl
        // (JsonlFileSink reste inscrit dès le boot).

        // SettingsWindow lazy : instanciée à la première ouverture via
        // ShowSettingsWindowLazy(). La branche --settings du boot
        // (restart depuis Settings) crée + show direct par le même
        // chemin lazy, donc le restart sur la bonne page reste fonctionnel.

        // PlaygroundWindow lazy: dev tool, instancied on first tray
        // open via ShowPlaygroundLazy(). Avoids paying a DComp swap
        // chain + DWM visual tree at boot for a window rarely used.
        // Same Closing→Hide contract once created.

        // Wire the recording cap into the chrono lib. Deckle.Chrono.Hud is
        // a Settings-agnostic module ; the App is the one that reads
        // Settings on every vsync to honour live edits to MaxRecordingDurationSeconds
        // (Capture page slider). Provider is invoked from UpdateClock at vsync.
        Deckle.Chrono.Hud.HudChrono.MaxRecordingDurationSecondsProvider =
            () => Settings.SettingsService.Instance.Current.Capture.MaxRecordingDurationSeconds;

        // HudWindow created once, never destroyed. No initial Show: the
        // constructor captures the HWND and sets up subclass / raw input /
        // extended styles directly on the native handle — no need to show the
        // window for this to work. The first ShowRecording triggers
        // ShowNoActivate, which positions bottom-center and calls SW_SHOWNOACTIVATE.
        _hudWindow = new HudWindow();

        // Manager for the transient overlay card stack (independent HWNDs
        // stacked 24 dip away from the main HUD). Owns per-card timers and
        // positions; reacts to main HUD show/hide via MainHudVisibilityChanged.
        _overlayManager = new HudOverlayManager(_hudWindow, _hudWindow.DispatcherQueue);

        // HUD feedback sink: picks up log entries that carry a UserFeedback
        // payload and surfaces them on the HUD. Events without feedback flow
        // only through the file sink and LogWindow. Added after HudWindow and
        // HudOverlayManager are constructed so the closures capture non-null
        // references. Routing rule: Replacement → main HUD slot (chrono
        // swapped out); Overlay → stacked card via HudOverlayManager.
        TelemetryService.Instance.AddSink(new HudFeedbackSink(
            onReplacement: fb => _hudWindow.ShowUserFeedback(fb),
            onOverlay:     fb => _overlayManager.Enqueue(fb)));

        // Warm pass: brief Show + Hide of the HUD at its real position so the
        // first composition (swap chain DComp + visual tree + Bitcount font
        // shaping) happens at boot rather than at first hotkey. The flash is
        // visible during boot — accepted, because the user isn't watching the
        // HUD position yet. First hotkey afterwards is cold-path-free.
        _hudWindow.PrimeAndHide();
        Milestone("hudwindow");

        _tray = new TrayIconManager
        {
            OnShowLogs        = () => ShowLogWindowLazy(),
            OnShowSettings    = () => ShowSettingsWindowLazy(),
            OnShowPlayground  = () => ShowPlaygroundLazy(),
            // Left-click tray = toggle transcription via the same path as the
            // standard hotkey. Allows starting with the mouse one-handed.
            OnToggleRecording = () => OnHotkey(NativeMethods.HOTKEY_ID_TRANSCRIBE),
            OnRestart         = () => RestartAppFromTray(),
            OnQuit            = () => QuitApp(),
        };
        Milestone("tray");

        // Engine events → UI. StatusChanged, TranscriptionFinished, etc. are
        // called from background threads; LogWindow and HudWindow marshal
        // internally via DispatcherQueue, UpdateStatus only calls
        // Shell_NotifyIcon (thread-safe). Logging events are gone — the engine
        // now logs directly via LogService.
        _engine.StatusChanged += status =>
        {
            _tray.UpdateStatus(status);
            _log.Info(LogSource.Status, status);
            // Beacon app icon in LogWindow + PlaygroundWindow: red =
            // recording, grey = idle. Single source of truth driven
            // from the engine status transition. StartsWith covers the
            // "Recording…" ellipsis variant emitted by RaiseStatus to
            // signal a transient state visually in the tray tooltip.
            bool isRecording = status.StartsWith("Recording");
            _lastRecordingState = isRecording;
            // Both nullable now: LogWindow and PlaygroundWindow are lazy-
            // created on first user open, so they're absent until then.
            _logWindow?.SetRecordingState(isRecording);
            _playgroundWindow?.SetRecordingState(isRecording);

            // HUD: driven by status transition. Background thread → HudWindow
            // marshals internally via DispatcherQueue. StartsWith on every
            // branch so transient ellipsis variants ("Transcribing…",
            // "Rewriting (cleanup)…") all route correctly.
            if (status.StartsWith("Recording"))
                _hudWindow.ShowRecording();
            else if (status.StartsWith("Transcribing"))
                _hudWindow.SwitchToTranscribing();
            else if (status.StartsWith("Rewriting"))
                _hudWindow.SwitchToRewriting();
        };
        _engine.TranscriptionFinished += outcome =>
        {
            switch (outcome)
            {
                case TranscriptionOutcome.Pasted:
                    // UIA confirmed the focused element accepts text and the
                    // Ctrl+V was sent. Brief success flash, then auto-hide.
                    _hudWindow.ShowPasted();
                    break;
                case TranscriptionOutcome.ClipboardOnly:
                    // Paste skipped (UIA unsure, foreground = Deckle, no focus,
                    // SendInput partial…) — tell the user the text is on the
                    // clipboard and keep the HUD up long enough to read.
                    _hudWindow.ShowCopied();
                    break;
                default:
                    _hudWindow.Hide();
                    break;
            }
        };
        // Synchronous rendezvous just before paste: hide the HUD and wait for
        // SW_HIDE to be effective on the UI thread before the engine sends
        // SendInput. Avoids the race where Hide() (triggered async after paste)
        // redistributes activation while Ctrl+V is still in the target's input queue.
        _engine.OnReadyToPaste = () => _hudWindow.HideSync();

        // Mic RMS → HUD recording outline. Fires ~20 Hz from the recording
        // audio thread; OnAudioLevel pushes into a CompositionPropertySet,
        // thread-safe per Composition's contract — no dispatcher needed.
        // Method group so it can be unsubscribed symmetrically later if
        // needed; no-op when the outline isn't attached (any non-Recording
        // state), so permanent subscription is fine.
        _engine.AudioLevel += _hudWindow.OnAudioLevel;

        // Initial status — model loads on-demand at first hotkey, not at startup.
        _tray.UpdateStatus("Ready");
        _log.Info(LogSource.Status, "Ready");

        // Message-only Win32 host — invisible by construction (HWND_MESSAGE
        // parent). Hosts the tray callback and global hotkeys without any
        // XAML window or off-screen trick.
        _messageHost = new MessageOnlyHost();
        _tray.Register(_messageHost.Hwnd);
        _hotkeyManager = new HotkeyManager(_messageHost.Hwnd, OnHotkey);
        // Try/catch obligatoire : RegisterHotKey peut échouer avec err 1409
        // (ERROR_HOTKEY_ALREADY_REGISTERED) quand un autre process possède
        // déjà la combinaison — typiquement WhispInteropTest qui tourne
        // encore via la tâche planifiée Whisp, ou PowerToys / une app
        // tierce qui a pris Win+1. Sans ce filet, le throw d'HotkeyManager
        // remonte à OnLaunched et l'app refuse de démarrer.
        //
        // Compromis : l'app continue de démarrer mais sans hotkeys. Le
        // tray reste opérationnel (Settings, Quit, toggle recording via
        // clic), donc l'utilisateur n'est pas verrouillé. UserFeedback
        // Overlay informe visuellement via le HUD au boot.
        try
        {
            _hotkeyManager.Register();
        }
        catch (Exception ex)
        {
            _log.Error(
                LogSource.Hotkey,
                $"Hotkey registration failed: {ex.Message}",
                new UserFeedback(
                    "Hotkeys unavailable",
                    "Another app owns the chord (often WhispInteropTest still running). Use the tray icon to record.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Overlay));
        }
        Milestone("hotkeys");

        // Silent warmup — runs a dummy transcription on a zero-filled buffer
        // so the first real hotkey doesn't pay the cold model load + first
        // inference cost. Fire-and-forget on a background thread; the engine
        // gates HUD/TranscriptionFinished events internally during the
        // warmup Transcribe() pass so nothing surfaces to the user.
        if (Settings.SettingsService.Instance.Current.Startup.WarmupOnLaunch)
            _engine.Warmup();
        Milestone("warmup");

        // Apply saved theme (System/Light/Dark).
        ApplyTheme(Settings.SettingsService.Instance.Current.Appearance.Theme);

        // Apply persisted level window (MinDbfs / MaxDbfs / DbfsCurveExponent)
        // into HudChrono statics so the first Recording reflects the user's
        // calibration without a restart-from-defaults round-trip.
        ApplyLevelWindow(Settings.SettingsService.Instance.Current.Capture.LevelWindow);

        // If launched with --settings (restart from Settings), automatically
        // reopen the Settings window on the right page.
        var cliArgs = Environment.GetCommandLineArgs();
        int settingsIdx = Array.IndexOf(cliArgs, "--settings");
        if (settingsIdx >= 0)
        {
            string? pageTag = settingsIdx + 1 < cliArgs.Length
                ? cliArgs[settingsIdx + 1]
                : null;
            _log.Verbose(LogSource.App, $"--settings flag detected | page={pageTag ?? "(default)"}");
            // Voie lazy : crée la fenêtre + l'affiche sur la page demandée.
            // Indistinct du chemin tray quand l'utilisateur ouvre Settings
            // pour la première fois.
            ShowSettingsWindowLazy(pageTag);
        }

        sw.Stop();
        milestones.Add($"total {sw.ElapsedMilliseconds}ms");
        _log.Verbose(LogSource.App, "startup milestones | " + string.Join(" | ", milestones));
    }

    // ── Level window ─────────────────────────────────────────────────────────
    //
    // Pushes the persisted dBFS calibration window from settings into the
    // HudChrono statics that the per-frame RMS mapper reads. Called at boot
    // (OnLaunched) and on every ViewModel change so live edits in
    // GeneralPage propagate without restart. Idempotent — safe to call
    // multiple times.

    public static void ApplyLevelWindow(Capture.LevelWindowSettings cfg)
    {
        if (cfg is null) return;
        Capture.AudioLevelMapper.MinDbfs           = cfg.MinDbfs;
        Capture.AudioLevelMapper.MaxDbfs           = cfg.MaxDbfs;
        Capture.AudioLevelMapper.DbfsCurveExponent = cfg.DbfsCurveExponent;
    }

    // ── Theme ────────────────────────────────────────────────────────────────
    //
    // Sets RequestedTheme on the Content (root FrameworkElement) of each known
    // window. ElementTheme.Default = follow the system.
    // Called at boot (OnLaunched) and when the user changes the theme
    // in GeneralPage.

    public static void ApplyTheme(string themeName)
    {
        var theme = themeName switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "Dark"  => Microsoft.UI.Xaml.ElementTheme.Dark,
            _       => Microsoft.UI.Xaml.ElementTheme.Default,
        };

        // Caption buttons are drawn by DWM via AppWindow.TitleBar, not by the
        // XAML tree — RequestedTheme on Content does not reach them, which is
        // what causes the dark/light switch latency on min/max/close. The fix
        // is AppWindow.TitleBar.PreferredTheme (WindowsAppSDK 1.7+), which
        // tells DWM which caption-button palette to use. "Default" lets the
        // system follow the app theme; explicit Light/Dark overrides it.
        var titleBarTheme = theme switch
        {
            Microsoft.UI.Xaml.ElementTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
            Microsoft.UI.Xaml.ElementTheme.Dark  => Microsoft.UI.Windowing.TitleBarTheme.Dark,
            _                                     => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode,
        };

        // Stocker pour que les fenêtres créées lazy après ce broadcast
        // récupèrent la bonne palette à leur instanciation via
        // ApplyThemeToSingle(window).
        _currentTheme = theme;
        _currentTitleBarTheme = titleBarTheme;

        if (Current is not App app) return;

        foreach (var window in new Microsoft.UI.Xaml.Window?[]
                     { app._settingsWindow, app._playgroundWindow, app._logWindow, app._hudWindow })
        {
            ApplyThemeToSingle(window);
        }
    }

    // Applique le theme courant à une fenêtre unique. Utilisé par la
    // boucle de ApplyTheme et par les ShowXxxLazy qui créent des fenêtres
    // après le broadcast initial — la fenêtre nouvellement créée doit
    // refléter la palette actuelle dès son premier render.
    private static void ApplyThemeToSingle(Microsoft.UI.Xaml.Window? window)
    {
        if (window is null) return;
        if (window.Content is Microsoft.UI.Xaml.FrameworkElement fe)
            fe.RequestedTheme = _currentTheme;
        if (window.AppWindow?.TitleBar is { } tb)
            tb.PreferredTheme = _currentTitleBarTheme;
    }

    // ── LogWindow lazy creation ──────────────────────────────────────────────
    //
    // Created on first tray open (or via Settings → "Logs" footer). Inscrit
    // comme sink à ce moment, et TelemetryService.Replay() rejoue le buffer
    // central pour que le viewer affiche tout depuis le boot. Beacon seedé
    // avec _lastRecordingState et theme appliqué pour que la fenêtre ait le
    // bon look dès son premier render.
    private void ShowLogWindowLazy()
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow();
            TelemetryService.Instance.AddSink(_logWindow);
            TelemetryService.Instance.Replay(_logWindow);
            _logWindow.SetRecordingState(_lastRecordingState);
            ApplyThemeToSingle(_logWindow);
        }
        _logWindow.ShowAndActivate();
    }

    // ── SettingsWindow lazy creation ─────────────────────────────────────────
    //
    // Created on first tray open or au boot si --settings est passé en CLI
    // (restart depuis Settings). Le callback OnShowLogsRequested capturé
    // ici pointe vers la voie lazy LogWindow — pas de référence directe à
    // _logWindow qui peut être null à ce moment.
    private void ShowSettingsWindowLazy(string? pageTag = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow
            {
                OnShowLogsRequested = () => ShowLogWindowLazy(),
            };
            ApplyThemeToSingle(_settingsWindow);
        }
        _settingsWindow.ShowAndActivate(pageTag);
    }

    // ── Playground lazy creation ─────────────────────────────────────────────
    //
    // Dev tool, not in user hot path. Created on first tray open instead
    // of at boot, to avoid paying a persistent DComp swap chain + DWM
    // visual tree for a window rarely used. Beacon seeded with the last
    // captured recording state so it's correct even if Playground opens
    // mid-recording.
    private void ShowPlaygroundLazy()
    {
        if (_playgroundWindow is null)
        {
            _playgroundWindow = new PlaygroundWindow();
            _playgroundWindow.SetRecordingState(_lastRecordingState);
            ApplyThemeToSingle(_playgroundWindow);
        }
        _playgroundWindow.ShowAndActivate();
    }

    // ── Clean shutdown from tray > Quit ──────────────────────────────────────
    //
    // Application.Current.Exit() is not enough on WinUI 3 unpackaged when
    // native hooks (SetWindowSubclass, RegisterHotKey, waveIn) are active:
    // the process survives and the tray icon remains ghost without NIM_DELETE.
    //
    // Sequence: (1) Dispose tray → sends NIM_DELETE + RemoveWindowSubclass.
    //           (2) Dispose engine → frees the whisper ctx and pipeline.
    //           (3) Environment.Exit(0) → guaranteed hard exit. Record/Transcribe
    //               threads are IsBackground=true, they die with the process.
    private void QuitApp()
    {
        _log.Info(LogSource.App, "Shutdown requested");
        try { Settings.SettingsService.Instance.Flush(); } catch (Exception ex) { _log.Warning(LogSource.App, "settings flush: " + ex.Message); }
        try { _hotkeyManager?.Dispose();   } catch (Exception ex) { _log.Warning(LogSource.App, "hotkeys dispose: " + ex.Message); }
        try { _tray?.Dispose();            } catch (Exception ex) { _log.Warning(LogSource.App, "tray dispose: " + ex.Message); }
        try { _messageHost?.Dispose();     } catch (Exception ex) { _log.Warning(LogSource.App, "message host dispose: " + ex.Message); }
        try { _overlayManager?.Dispose();  } catch (Exception ex) { _log.Warning(LogSource.App, "overlay manager dispose: " + ex.Message); }
        try { _engine?.Dispose();          } catch (Exception ex) { _log.Warning(LogSource.App, "engine dispose: " + ex.Message); }
        Environment.Exit(0);
    }

    // ── Restart from Settings ───────────────────────────────────────────────
    //
    // Launches a new Deckle process with --settings so Settings reopen
    // automatically at boot, then clean shutdown of the current process
    // via QuitApp().
    public static void RestartApp(string? pageTag = null)
    {
        _log.Info(LogSource.App, "Restart requested");

        // Flush settings synchronously BEFORE launching the new process.
        // Without this, the new process could read stale JSON if it starts
        // faster than QuitApp's Flush completes (race on the same file).
        try { Settings.SettingsService.Instance.Flush(); } catch { }

        var exePath = Environment.ProcessPath;
        if (exePath is not null)
        {
            var args = pageTag is not null
                ? $"--settings {pageTag}"
                : "--settings";
            _log.Verbose(LogSource.App, $"spawn new process | exe={exePath} | args={args}");
            System.Diagnostics.Process.Start(exePath, args);
        }

        if (Current is App app)
            app.QuitApp();
    }

    // ── Restart from tray ──────────────────────────────────────────────────
    //
    // Launches a new bare Deckle process (no --settings) then clean
    // shutdown of the current process.
    private void RestartAppFromTray()
    {
        _log.Info(LogSource.App, "Restart from tray requested");
        try { Settings.SettingsService.Instance.Flush(); } catch { }
        var exePath = Environment.ProcessPath;
        if (exePath is not null)
        {
            _log.Verbose(LogSource.App, $"spawn new process | exe={exePath}");
            System.Diagnostics.Process.Start(exePath);
        }
        QuitApp();
    }

    private void OnHotkey(int hotkeyId)
    {
        if (_engine is null) return;

        // Friendly name for logs — avoid raw numeric ids in user-facing traces.
        string hotkeyName = hotkeyId switch
        {
            NativeMethods.HOTKEY_ID_TRANSCRIBE        => "transcribe",
            NativeMethods.HOTKEY_ID_PRIMARY_REWRITE   => "primary rewrite",
            NativeMethods.HOTKEY_ID_SECONDARY_REWRITE => "secondary rewrite",
            _                                         => $"id={hotkeyId}",
        };

        // Map hotkey id → manual rewrite profile name (null for plain
        // transcribe — engine then falls back to duration-based AutoRewriteRules).
        // Prefer the stable ProfileId (survives renames): resolve it to the
        // profile's current Name. Fall back to the legacy *ProfileName field
        // when the Id slot is empty (pre-migration configs).
        var llm = Settings.SettingsService.Instance.Current.Llm;
        string? ResolveSlotName(string? id, string? nameFallback) =>
            (!string.IsNullOrEmpty(id)
                ? llm.Profiles.Find(p => p.Id == id)?.Name
                : null)
            ?? nameFallback;
        string? manualProfile = hotkeyId switch
        {
            NativeMethods.HOTKEY_ID_PRIMARY_REWRITE   =>
                ResolveSlotName(llm.PrimaryRewriteProfileId, llm.PrimaryRewriteProfileName),
            NativeMethods.HOTKEY_ID_SECONDARY_REWRITE =>
                ResolveSlotName(llm.SecondaryRewriteProfileId, llm.SecondaryRewriteProfileName),
            _                                         => null,
        };

        // Rewrite hotkeys with no profile assigned: from Idle the engine
        // refuses with IgnoredNoProfile (no recording starts); during
        // Recording the press is still a valid Stop, profile or not. We
        // signal the requirement to the engine so it can sort it out
        // atomically — this layer no longer reads engine state directly.
        bool isRewriteHotkey = hotkeyId == NativeMethods.HOTKEY_ID_PRIMARY_REWRITE
                            || hotkeyId == NativeMethods.HOTKEY_ID_SECONDARY_REWRITE;

        // Show the HUD eagerly: if RequestToggle ends up Started, the user
        // gets feedback from the first millisecond. If it returns anything
        // else we hide the HUD again below — net cost is one extra
        // ShowPreparing/Hide round-trip on the rejected path, which is
        // cheap and the user never sees it (the press was rejected fast).
        // We only do this for what would have been a Start — no point
        // showing Preparing on a Stop press where the HUD is already up.
        // We can't know "would have been a Start" without reading state,
        // and that read is what we're trying to remove. Compromise: ask
        // the engine first, then show.
        var result = _engine.RequestToggle(
            manualProfileName: manualProfile,
            shouldPaste: Settings.SettingsService.Instance.Current.Paste.AutoPasteEnabled,
            requireProfile: isRewriteHotkey);

        switch (result)
        {
            case ToggleResult.Started:
                _log.Success(LogSource.Hotkey,
                    $"Start ({hotkeyName}{(manualProfile is null ? "" : $", LLM: {manualProfile}")})");
                _hudWindow?.ShowPreparing();
                break;

            case ToggleResult.Stopped:
                _log.Success(LogSource.Hotkey, "Stop");
                break;

            case ToggleResult.IgnoredNoProfile:
                _log.Warning(LogSource.Hotkey, $"{hotkeyName} pressed — no profile bound, ignoring");
                break;

            case ToggleResult.IgnoredBusy:
                // Engine already logged a Verbose line with the exact state
                // — no second log needed, this is the "user double-pressed"
                // case we are explicitly handling silently.
                break;

            case ToggleResult.IgnoredDisposed:
                // Engine in shutdown. Silent — Quit is the authoritative
                // signal; a stray hotkey arriving after it is expected.
                break;
        }
    }
}
