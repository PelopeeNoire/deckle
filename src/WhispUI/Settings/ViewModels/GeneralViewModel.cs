using CommunityToolkit.Mvvm.ComponentModel;
using WhispUI.Logging;
using WhispUI.Shell;

namespace WhispUI.Settings.ViewModels;

// ViewModel for GeneralPage — bridges the 4 AppSettings sections
// (Recording, Overlay, Startup, Appearance) to the XAML via x:Bind.
//
// Pattern: Load() pulls from the POCO, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
//
// Partial properties (not fields) for WinRT/AOT compatibility (MVVMTK0045).
public partial class GeneralViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

    // ── Recording ────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int AudioInputDeviceId { get; set; }

    partial void OnAudioInputDeviceIdChanged(int value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Audio input device ← {value}");
        PushToSettings();
    }

    [ObservableProperty]
    public partial bool AutoPasteEnabled { get; set; }

    partial void OnAutoPasteEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Auto-paste ← {value}");
        PushToSettings();
    }

    // ── Level window (calibration) ──────────────────────────────────────────
    //
    // Three sliders + an auto toggle that map onto HudChrono.{Min,Max}Dbfs +
    // DbfsCurveExponent. Each change pushes to settings AND directly into the
    // HudChrono statics via App.ApplyLevelWindow so the HUD reflects the new
    // window on the next sub-window without restart.

    [ObservableProperty]
    public partial double LevelWindowMinDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowMaxDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowExponent { get; set; }

    [ObservableProperty]
    public partial bool LevelWindowAutoCalibration { get; set; }

    partial void OnLevelWindowMinDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"LevelWindow.MinDbfs ← {value:F1} dBFS");
        PushToSettings();
        App.ApplyLevelWindow(SettingsService.Instance.Current.Recording.LevelWindow);
    }

    partial void OnLevelWindowMaxDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"LevelWindow.MaxDbfs ← {value:F1} dBFS");
        PushToSettings();
        App.ApplyLevelWindow(SettingsService.Instance.Current.Recording.LevelWindow);
    }

    partial void OnLevelWindowExponentChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"LevelWindow.DbfsCurveExponent ← {value:F2}");
        PushToSettings();
        App.ApplyLevelWindow(SettingsService.Instance.Current.Recording.LevelWindow);
    }

    partial void OnLevelWindowAutoCalibrationChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"LevelWindow.AutoCalibration ← {value}");
        PushToSettings();
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool OverlayEnabled { get; set; }

    [ObservableProperty]
    public partial bool OverlayFadeOnProximity { get; set; }

    [ObservableProperty]
    public partial string OverlayPosition { get; set; }

    partial void OnOverlayEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay enabled ← {value}");
        PushToSettings();
    }

    partial void OnOverlayFadeOnProximityChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay fade ← {value}");
        PushToSettings();
    }

    partial void OnOverlayPositionChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay position ← {value}");
        PushToSettings();
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    // AutostartEnabled is not backed by AppSettings — the source of truth is
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WhispUI. Load() reads
    // the registry, OnAutostartEnabledChanged writes it. If the write fails,
    // we revert the UI state so the toggle stays consistent with reality.
    [ObservableProperty]
    public partial bool AutostartEnabled { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool WarmupOnLaunch { get; set; }

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        bool ok = value ? AutostartService.Enable() : AutostartService.Disable();
        if (ok)
        {
            _log.Info(LogSource.SetGeneral, $"Start with Windows ← {value}");
            return;
        }

        // Write refused (GPO, ACL, missing ProcessPath…) — revert the toggle
        // so what the user sees matches what's actually in the registry.
        _isSyncing = true;
        try { AutostartEnabled = !value; }
        finally { _isSyncing = false; }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Start minimized ← {value}");
        PushToSettings();
    }

    partial void OnWarmupOnLaunchChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Warmup on launch ← {value}");
        PushToSettings();
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string Theme { get; set; }

    partial void OnThemeChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Theme ← {value}");
        PushToSettings();
        App.ApplyTheme(value);
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    // Microphone telemetry — when on, every Recording Stop logs an extra
    // line summarising the per-recording RMS distribution (min / p10 / p25 /
    // p50 / p75 / p90 / max in dBFS + linear mean RMS) AND writes a
    // structured row to <telemetry>/microphone.jsonl. Calibration tool —
    // off by default to keep the All filter readable for everyday use.
    [ObservableProperty]
    public partial bool MicrophoneTelemetry { get; set; }

    partial void OnMicrophoneTelemetryChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.MicrophoneTelemetry ← {value}");
        PushToSettings();
    }

    [ObservableProperty]
    public partial bool TelemetryLatencyEnabled { get; set; }

    [ObservableProperty]
    public partial bool TelemetryCorpusEnabled { get; set; }

    [ObservableProperty]
    public partial bool RecordAudioCorpus { get; set; }

    [ObservableProperty]
    public partial bool ApplicationLogToDisk { get; set; }

    [ObservableProperty]
    public partial string TelemetryStorageDirectory { get; set; }

    partial void OnTelemetryLatencyEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.LatencyEnabled ← {value}");
        PushToSettings();
    }

    partial void OnTelemetryCorpusEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.CorpusEnabled ← {value}");
        PushToSettings();
    }

    partial void OnRecordAudioCorpusChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.RecordAudioCorpus ← {value}");
        PushToSettings();
    }

    partial void OnApplicationLogToDiskChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.ApplicationLogToDisk ← {value}");
        PushToSettings();
    }

    partial void OnTelemetryStorageDirectoryChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.StorageDirectory ← \"{value}\"");
        PushToSettings();
    }

    // ── Sync with SettingsService ────────────────────────────────────────────

    public GeneralViewModel()
    {
        // Guard BEFORE any property assignment — the partial property setters
        // trigger OnXChanged which would call PushToSettings() and corrupt
        // the POCO with partially-initialized defaults.
        _isSyncing = true;

        AudioInputDeviceId = -1;
        AutoPasteEnabled = false;
        LevelWindowMinDbfs = -55;
        LevelWindowMaxDbfs = -32;
        LevelWindowExponent = 1.0;
        LevelWindowAutoCalibration = false;
        MicrophoneTelemetry = false;
        OverlayEnabled = true;
        OverlayFadeOnProximity = true;
        OverlayPosition = "BottomCenter";
        AutostartEnabled = false;
        StartMinimized = true;
        WarmupOnLaunch = true;
        Theme = "System";
        TelemetryLatencyEnabled = false;
        TelemetryCorpusEnabled = false;
        RecordAudioCorpus = false;
        ApplicationLogToDisk = false;
        TelemetryStorageDirectory = "";

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var s = SettingsService.Instance.Current;
            AudioInputDeviceId = s.Recording.AudioInputDeviceId;
            AutoPasteEnabled = s.Paste.AutoPasteEnabled;
            LevelWindowMinDbfs = s.Recording.LevelWindow.MinDbfs;
            LevelWindowMaxDbfs = s.Recording.LevelWindow.MaxDbfs;
            LevelWindowExponent = s.Recording.LevelWindow.DbfsCurveExponent;
            LevelWindowAutoCalibration = s.Recording.LevelWindow.AutoCalibrationEnabled;
            MicrophoneTelemetry = s.Telemetry.MicrophoneTelemetry;
            OverlayEnabled = s.Overlay.Enabled;
            OverlayFadeOnProximity = s.Overlay.FadeOnProximity;
            OverlayPosition = s.Overlay.Position;
            AutostartEnabled = AutostartService.IsEnabled();
            StartMinimized = s.Startup.StartMinimized;
            WarmupOnLaunch = s.Startup.WarmupOnLaunch;
            Theme = s.Appearance.Theme;
            TelemetryLatencyEnabled = s.Telemetry.LatencyEnabled;
            TelemetryCorpusEnabled = s.Telemetry.CorpusEnabled;
            RecordAudioCorpus = s.Telemetry.RecordAudioCorpus;
            ApplicationLogToDisk = s.Telemetry.ApplicationLogToDisk;
            TelemetryStorageDirectory = s.Telemetry.StorageDirectory;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void PushToSettings()
    {
        var s = SettingsService.Instance.Current;
        s.Recording.AudioInputDeviceId = AudioInputDeviceId;
        s.Paste.AutoPasteEnabled = AutoPasteEnabled;
        s.Recording.LevelWindow.MinDbfs = (float)LevelWindowMinDbfs;
        s.Recording.LevelWindow.MaxDbfs = (float)LevelWindowMaxDbfs;
        s.Recording.LevelWindow.DbfsCurveExponent = (float)LevelWindowExponent;
        s.Recording.LevelWindow.AutoCalibrationEnabled = LevelWindowAutoCalibration;
        s.Telemetry.MicrophoneTelemetry = MicrophoneTelemetry;
        s.Overlay.Enabled = OverlayEnabled;
        s.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        s.Overlay.Position = OverlayPosition;
        s.Startup.StartMinimized = StartMinimized;
        s.Startup.WarmupOnLaunch = WarmupOnLaunch;
        s.Appearance.Theme = Theme;
        s.Telemetry.LatencyEnabled = TelemetryLatencyEnabled;
        s.Telemetry.CorpusEnabled = TelemetryCorpusEnabled;
        s.Telemetry.RecordAudioCorpus = RecordAudioCorpus;
        s.Telemetry.ApplicationLogToDisk = ApplicationLogToDisk;
        s.Telemetry.StorageDirectory = TelemetryStorageDirectory ?? "";
        SettingsService.Instance.Save();
    }

    // ── Reset per section ───────────────────────────────────────────────────
    //
    // Each reset writes the AppSettings defaults back through the VM so x:Bind
    // TwoWay refreshes the visual tree. _isSyncing suppresses the per-property
    // PushToSettings so we issue a single Save() at the end.

    public void ResetRecordingDefaults()
    {
        _isSyncing = true;
        try
        {
            AudioInputDeviceId = -1;
            AutoPasteEnabled = false;
            LevelWindowMinDbfs = -55;
            LevelWindowMaxDbfs = -32;
            LevelWindowExponent = 1.0;
            LevelWindowAutoCalibration = false;
            OverlayEnabled = true;
            OverlayFadeOnProximity = true;
            OverlayPosition = "BottomCenter";
        }
        finally { _isSyncing = false; }
        PushToSettings();
        App.ApplyLevelWindow(SettingsService.Instance.Current.Recording.LevelWindow);
        _log.Info(LogSource.SetGeneral, "Recording section reset to defaults");
    }

    public void ResetStartupDefaults()
    {
        // Autostart lives in the registry — AutostartService handles the write
        // and returns false when the write is refused (GPO, ACL…). Mirror the
        // actual registry state back into the VM so the toggle matches reality.
        AutostartService.Disable();

        _isSyncing = true;
        try
        {
            AutostartEnabled = AutostartService.IsEnabled();
            StartMinimized = true;
            WarmupOnLaunch = true;
        }
        finally { _isSyncing = false; }
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Startup section reset to defaults");
    }

    public void ResetAppearanceDefaults()
    {
        _isSyncing = true;
        try { Theme = "System"; }
        finally { _isSyncing = false; }
        PushToSettings();
        App.ApplyTheme(Theme);
        _log.Info(LogSource.SetGeneral, "Appearance section reset to defaults");
    }

    public void ResetTelemetryDefaults()
    {
        _isSyncing = true;
        try
        {
            MicrophoneTelemetry = false;
            TelemetryLatencyEnabled = false;
            TelemetryCorpusEnabled = false;
            RecordAudioCorpus = false;
            ApplicationLogToDisk = false;
            TelemetryStorageDirectory = "";
        }
        finally { _isSyncing = false; }
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Telemetry section reset to defaults");
    }
}
