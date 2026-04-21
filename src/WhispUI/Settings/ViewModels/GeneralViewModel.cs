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

    // ── Diagnostics ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool TelemetryLatencyEnabled { get; set; }

    [ObservableProperty]
    public partial bool TelemetryCorpusEnabled { get; set; }

    [ObservableProperty]
    public partial bool RecordAudioCorpus { get; set; }

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
        s.Overlay.Enabled = OverlayEnabled;
        s.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        s.Overlay.Position = OverlayPosition;
        s.Startup.StartMinimized = StartMinimized;
        s.Startup.WarmupOnLaunch = WarmupOnLaunch;
        s.Appearance.Theme = Theme;
        s.Telemetry.LatencyEnabled = TelemetryLatencyEnabled;
        s.Telemetry.CorpusEnabled = TelemetryCorpusEnabled;
        s.Telemetry.RecordAudioCorpus = RecordAudioCorpus;
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
            OverlayEnabled = true;
            OverlayFadeOnProximity = true;
            OverlayPosition = "BottomCenter";
        }
        finally { _isSyncing = false; }
        PushToSettings();
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

    public void ResetDiagnosticsDefaults()
    {
        _isSyncing = true;
        try
        {
            TelemetryLatencyEnabled = false;
            TelemetryCorpusEnabled = false;
            RecordAudioCorpus = false;
            TelemetryStorageDirectory = "";
        }
        finally { _isSyncing = false; }
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Diagnostics section reset to defaults");
    }
}
