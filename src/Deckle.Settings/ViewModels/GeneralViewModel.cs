using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;
using Deckle.Shell;

namespace Deckle.Settings.ViewModels;

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
    // Three sliders + an auto toggle that map onto AudioLevelMapper's
    // {Min,Max}Dbfs + DbfsCurveExponent statics in Deckle.Capture. Each
    // change pushes to settings AND directly into those statics via
    // SettingsHost.ApplyLevelWindow (wired by App at boot) so the HUD
    // reflects the new window on the next sub-window without restart.

    [ObservableProperty]
    public partial double LevelWindowMinDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowMaxDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowExponent { get; set; }

    [ObservableProperty]
    public partial bool LevelWindowAutoCalibration { get; set; }

    // Slider drags fire ValueChanged on every step (50+ events per drag),
    // so we keep the per-edit log line at Verbose level — visible in the
    // All filter for debugging, hidden from Activity / Steps where it
    // would drown the actual pipeline narrative. PushToSettings is fine
    // on every step (the file save is debounced one level deeper inside
    // SettingsService); SettingsHost.ApplyLevelWindow ultimately writes
    // a few static fields in Capture.AudioLevelMapper, also free.
    partial void OnLevelWindowMinDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.MinDbfs ← {value:F1} dBFS");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(SettingsService.Instance.Current.Capture.LevelWindow);
    }

    partial void OnLevelWindowMaxDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.MaxDbfs ← {value:F1} dBFS");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(SettingsService.Instance.Current.Capture.LevelWindow);
    }

    partial void OnLevelWindowExponentChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.DbfsCurveExponent ← {value:F2}");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(SettingsService.Instance.Current.Capture.LevelWindow);
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
    public partial bool OverlayAnimations { get; set; }

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

    partial void OnOverlayAnimationsChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay animations ← {value}");
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
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Deckle. Load() reads
    // the registry, OnAutostartEnabledChanged writes it. If the write fails,
    // we revert the UI state so the toggle stays consistent with reality.
    [ObservableProperty]
    public partial bool AutostartEnabled { get; set; }

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
        SettingsHost.ApplyTheme?.Invoke(value);
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

    // ── Backup ───────────────────────────────────────────────────────────────
    //
    // BackupDirectory is the user override for where snapshots live (empty =
    // AppPaths.SettingsBackupDirectory, see SettingsService.ResolveBackupDirectory).
    // Backups is the live list refilled by RefreshBackups() — called on Load,
    // after CreateBackup, and any time BackupDirectory changes. The PowerToys-
    // style UI only surfaces the latest snapshot (file name + created at);
    // older snapshots remain on disk for manual access via the folder picker.

    [ObservableProperty]
    public partial string BackupDirectory { get; set; }

    public ObservableCollection<BackupInfo> Backups { get; } = new();

    public BackupInfo? LatestBackup => Backups.Count > 0 ? Backups[0] : null;

    public bool HasBackup => LatestBackup is not null;

    public string LatestBackupFileName => LatestBackup is null
        ? "—"
        : Path.GetFileName(LatestBackup.Path);

    public string LatestBackupCreatedAt => LatestBackup is null
        ? "No backup yet"
        : LatestBackup.Timestamp.LocalDateTime.ToString("g");

    // Projection of the resolved backup directory (user override or default).
    // Read-only display string used in the SettingsExpander Location card —
    // shows where snapshots actually land, not what the user typed in the
    // override field. Refreshed when BackupDirectory changes.
    public string BackupLocationDisplay => SettingsBackupService.GetDirectory();

    partial void OnBackupDirectoryChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Paths.BackupDirectory ← \"{value}\"");
        PushToSettings();
        OnPropertyChanged(nameof(BackupLocationDisplay));
        RefreshBackups();
    }

    public void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in SettingsBackupService.ListBackups())
            Backups.Add(b);

        OnPropertyChanged(nameof(LatestBackup));
        OnPropertyChanged(nameof(HasBackup));
        OnPropertyChanged(nameof(LatestBackupFileName));
        OnPropertyChanged(nameof(LatestBackupCreatedAt));
        OnPropertyChanged(nameof(BackupLocationDisplay));
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
        OverlayAnimations = true;
        OverlayPosition = "BottomCenter";
        AutostartEnabled = false;
        WarmupOnLaunch = true;
        Theme = "System";
        TelemetryLatencyEnabled = false;
        TelemetryCorpusEnabled = false;
        RecordAudioCorpus = false;
        ApplicationLogToDisk = false;
        TelemetryStorageDirectory = "";
        BackupDirectory = "";

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var s = SettingsService.Instance.Current;
            AudioInputDeviceId = s.Capture.AudioInputDeviceId;
            AutoPasteEnabled = s.Paste.AutoPasteEnabled;
            LevelWindowMinDbfs = s.Capture.LevelWindow.MinDbfs;
            LevelWindowMaxDbfs = s.Capture.LevelWindow.MaxDbfs;
            LevelWindowExponent = s.Capture.LevelWindow.DbfsCurveExponent;
            LevelWindowAutoCalibration = s.Capture.LevelWindow.AutoCalibrationEnabled;
            MicrophoneTelemetry = s.Telemetry.MicrophoneTelemetry;
            OverlayEnabled = s.Overlay.Enabled;
            OverlayFadeOnProximity = s.Overlay.FadeOnProximity;
            OverlayAnimations = s.Overlay.Animations;
            OverlayPosition = s.Overlay.Position;
            AutostartEnabled = AutostartService.IsEnabled();
            WarmupOnLaunch = s.Startup.WarmupOnLaunch;
            Theme = s.Appearance.Theme;
            TelemetryLatencyEnabled = s.Telemetry.LatencyEnabled;
            TelemetryCorpusEnabled = s.Telemetry.CorpusEnabled;
            RecordAudioCorpus = s.Telemetry.RecordAudioCorpus;
            ApplicationLogToDisk = s.Telemetry.ApplicationLogToDisk;
            TelemetryStorageDirectory = s.Telemetry.StorageDirectory;
            BackupDirectory = s.Paths.BackupDirectory;
        }
        finally
        {
            _isSyncing = false;
        }

        // Refresh outside the _isSyncing guard so any future logic in
        // RefreshBackups that touches observable state behaves normally.
        RefreshBackups();
    }

    private void PushToSettings()
    {
        var s = SettingsService.Instance.Current;
        s.Capture.AudioInputDeviceId = AudioInputDeviceId;
        s.Paste.AutoPasteEnabled = AutoPasteEnabled;
        s.Capture.LevelWindow.MinDbfs = (float)LevelWindowMinDbfs;
        s.Capture.LevelWindow.MaxDbfs = (float)LevelWindowMaxDbfs;
        s.Capture.LevelWindow.DbfsCurveExponent = (float)LevelWindowExponent;
        s.Capture.LevelWindow.AutoCalibrationEnabled = LevelWindowAutoCalibration;
        s.Telemetry.MicrophoneTelemetry = MicrophoneTelemetry;
        s.Overlay.Enabled = OverlayEnabled;
        s.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        s.Overlay.Animations = OverlayAnimations;
        s.Overlay.Position = OverlayPosition;
        s.Startup.WarmupOnLaunch = WarmupOnLaunch;
        s.Appearance.Theme = Theme;
        s.Telemetry.LatencyEnabled = TelemetryLatencyEnabled;
        s.Telemetry.CorpusEnabled = TelemetryCorpusEnabled;
        s.Telemetry.RecordAudioCorpus = RecordAudioCorpus;
        s.Telemetry.ApplicationLogToDisk = ApplicationLogToDisk;
        s.Telemetry.StorageDirectory = TelemetryStorageDirectory ?? "";
        s.Paths.BackupDirectory = BackupDirectory ?? "";
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
            OverlayAnimations = true;
            OverlayPosition = "BottomCenter";
        }
        finally { _isSyncing = false; }
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(SettingsService.Instance.Current.Capture.LevelWindow);
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
        SettingsHost.ApplyTheme?.Invoke(Theme);
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
