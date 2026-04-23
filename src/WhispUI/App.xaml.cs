using WhispUI.Interop;
using WhispUI.Logging;
using WhispUI.Logging.Sinks;
using WhispUI.Shell;

namespace WhispUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private static readonly LogService _log = LogService.Instance;

    private MessageOnlyHost? _messageHost;
    private HotkeyManager? _hotkeyManager;
    private LogWindow? _logWindow;

    internal static SettingsWindow? SettingsWin => (Current as App)?._settingsWindow;
    private SettingsWindow? _settingsWindow;
    private HudWindow? _hudWindow;
    private TrayIconManager? _tray;
    private WhispEngine? _engine;

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
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
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

        // File sink first — captures every event from boot, including the
        // startup milestones flushed at the end of OnLaunched. Writes under
        // the telemetry storage directory (benchmark/ in dev layout).
        TelemetryService.Instance.AddSink(new JsonlFileSink());
        Milestone("filesink");

        _engine = new WhispEngine();
        Milestone("engine");

        // LogWindow created once, never destroyed.
        _logWindow = new LogWindow();

        TelemetryService.Instance.AddSink(_logWindow);
        Milestone("logwindow");

        // SettingsWindow created once, never destroyed. No initial Show:
        // opened only on demand via tray. The "Logs" footer item in
        // NavigationView opens the shared LogWindow via this callback.
        _settingsWindow = new SettingsWindow
        {
            OnShowLogsRequested = () => _logWindow.ShowAndActivate(),
        };
        Milestone("settingswindow");

        // HudWindow created once, never destroyed. No initial Show: the
        // constructor captures the HWND and sets up subclass / raw input /
        // extended styles directly on the native handle — no need to show the
        // window for this to work. The first ShowRecording triggers
        // ShowNoActivate, which positions bottom-center and calls SW_SHOWNOACTIVATE.
        _hudWindow = new HudWindow();

        // HUD feedback sink: picks up log entries that carry a UserFeedback
        // payload and surfaces them on the HUD. Events without feedback flow
        // only through the file sink and LogWindow. Added after HudWindow is
        // constructed so the closure captures a non-null reference.
        TelemetryService.Instance.AddSink(new HudFeedbackSink(fb => _hudWindow.ShowUserFeedback(fb)));
        Milestone("hudwindow");

        _tray = new TrayIconManager
        {
            OnShowLogs        = () => _logWindow.ShowAndActivate(),
            OnShowSettings    = () => _settingsWindow.ShowAndActivate(),
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
            // Beacon app icon in LogWindow: red = recording, grey = idle.
            _logWindow.SetRecordingState(status == "Recording");

            // HUD: driven by status transition. Background thread → HudWindow
            // marshals internally via DispatcherQueue.
            if (status == "Recording")
                _hudWindow.ShowRecording();
            else if (status == "Transcribing")
                _hudWindow.SwitchToTranscribing();
            // Engine emits "Réécriture (...)" today; the FR→EN sweep at
            // WhispEngine.cs:858 ("Rewriting (...)") lands once the parallel
            // logs branch is merged. Match both so the dispatcher is robust
            // across the swap.
            else if (status.StartsWith("Réécriture") || status.StartsWith("Rewriting"))
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
                    // Paste skipped (UIA unsure, foreground = WhispUI, no focus,
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
        _hotkeyManager.Register();
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
            _settingsWindow?.ShowAndActivate(pageTag);
        }

        sw.Stop();
        milestones.Add($"total {sw.ElapsedMilliseconds}ms");
        _log.Verbose(LogSource.App, "startup milestones | " + string.Join(" | ", milestones));
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

        if (Current is not App app) return;

        foreach (var window in new Microsoft.UI.Xaml.Window?[]
                     { app._settingsWindow, app._logWindow, app._hudWindow })
        {
            if (window is null) continue;
            if (window.Content is Microsoft.UI.Xaml.FrameworkElement fe)
                fe.RequestedTheme = theme;
            if (window.AppWindow?.TitleBar is { } tb)
                tb.PreferredTheme = titleBarTheme;
        }
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
        try { _hotkeyManager?.Dispose(); } catch (Exception ex) { _log.Warning(LogSource.App, "hotkeys dispose: " + ex.Message); }
        try { _tray?.Dispose();          } catch (Exception ex) { _log.Warning(LogSource.App, "tray dispose: " + ex.Message); }
        try { _messageHost?.Dispose();   } catch (Exception ex) { _log.Warning(LogSource.App, "message host dispose: " + ex.Message); }
        try { _engine?.Dispose();        } catch (Exception ex) { _log.Warning(LogSource.App, "engine dispose: " + ex.Message); }
        Environment.Exit(0);
    }

    // ── Restart from Settings ───────────────────────────────────────────────
    //
    // Launches a new WhispUI process with --settings so Settings reopen
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
    // Launches a new bare WhispUI process (no --settings) then clean
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

        // Rewrite hotkeys with no profile assigned → no-op (user left the
        // shortcut unbound). We don't start recording at all: no mic probe,
        // no HUD, no transcription. The warning makes it visible in logs.
        bool isRewriteHotkey = hotkeyId == NativeMethods.HOTKEY_ID_PRIMARY_REWRITE
                            || hotkeyId == NativeMethods.HOTKEY_ID_SECONDARY_REWRITE;
        if (isRewriteHotkey &&
            string.IsNullOrWhiteSpace(manualProfile) &&
            !_engine.IsRecording)
        {
            _log.Warning(LogSource.Hotkey, $"{hotkeyName} pressed — no profile bound, ignoring");
            return;
        }

        if (!_engine.IsRecording)
        {
            _log.Success(LogSource.Hotkey,
                $"Start ({hotkeyName}{(manualProfile is null ? "" : $", LLM: {manualProfile}")})");

            // Show the HUD immediately in its "Preparing" state so the user
            // gets visual feedback from the very first millisecond after the
            // hotkey press, before the mic probe and any model load that
            // might be needed. ShowRecording() later replaces the neutral
            // digits with the recording accent when StatusChanged fires.
            _hudWindow?.ShowPreparing();

            _engine.StartRecording(
                manualProfileName: manualProfile,
                shouldPaste: Settings.SettingsService.Instance.Current.Paste.AutoPasteEnabled);
        }
        else
        {
            _log.Success(LogSource.Hotkey, "Stop");
            _engine.StopRecording();
        }
    }
}
