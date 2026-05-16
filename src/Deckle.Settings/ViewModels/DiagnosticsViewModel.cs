using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;

namespace Deckle.Settings.ViewModels;

// ViewModel for DiagnosticsPage — bridges TelemetrySettings and
// LoggingSettings to the XAML via x:Bind. Originally migrated from
// GeneralViewModel in slice S2 (Telemetry only) ; J4 polish added the
// Logging section to host runtime emission filters orthogonal to
// disk persistence, which expanded the VM to cover two stores.
//
// Pattern : Load() pulls from each store, property changes push back
// via the matching PushXxxToSettings(). The _isSyncing flag prevents
// re-saving during Load(). The split between PushLoggingToSettings()
// and PushTelemetryToSettings() lets a single toggle touch only its
// own store — flipping Verbose logging doesn't rewrite the telemetry
// JSON file, which matters because the two share neither schema nor
// lifecycle.
public partial class DiagnosticsViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

    // ── Logging — runtime emission filters ──────────────────────────────────

    // Verbose logging master switch. When false (default), Verbose
    // events are dropped at the TelemetryService source — neither
    // LogWindow nor app.jsonl see them. When true, the full per-tick
    // instrumentation surfaces, useful when investigating pipeline
    // hiccups. Wired through LoggingSettingsService — separate store
    // from TelemetrySettings so flipping it leaves the disk-persistence
    // opt-ins untouched.
    [ObservableProperty]
    public partial bool VerboseLoggingEnabled { get; set; }

    // ── Telemetry — opt-in disk persistence ─────────────────────────────────

    // Application log — mirrors every in-app log line to app.jsonl. Top of
    // section by user request : the most asked-for diagnostic when
    // troubleshooting an issue across restarts.
    [ObservableProperty]
    public partial bool ApplicationLogToDisk { get; set; }

    // Microphone telemetry — when on, every Recording Stop logs an extra
    // line summarising the per-recording RMS distribution AND writes a
    // structured row to <telemetry>/microphone.jsonl. Calibration tool.
    [ObservableProperty]
    public partial bool MicrophoneTelemetry { get; set; }

    // Latency telemetry — per-step timings of each transcription written
    // to latency.jsonl. Timings only, no transcript text — lighter privacy
    // posture than Application log or Corpus.
    [ObservableProperty]
    public partial bool TelemetryLatencyEnabled { get; set; }

    // Corpus master — text corpus (transcription + rewrite) per profile.
    // Audio corpus is nested under it (gated by IsEnabled in XAML so it
    // can't reach the on state while the master is off).
    [ObservableProperty]
    public partial bool TelemetryCorpusEnabled { get; set; }

    [ObservableProperty]
    public partial bool RecordAudioCorpus { get; set; }

    // Storage folder override — empty = AppPaths.TelemetryDirectory.
    // FolderPickerCard.DefaultPath is wired to the resolved default in
    // the page code-behind ; the picker shows it as a placeholder when
    // the override is empty.
    [ObservableProperty]
    public partial string TelemetryStorageDirectory { get; set; }

    partial void OnVerboseLoggingEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Logging.VerboseLoggingEnabled ← {value}");
        PushLoggingToSettings();
    }

    partial void OnApplicationLogToDiskChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.ApplicationLogToDisk ← {value}");
        PushTelemetryToSettings();
    }

    partial void OnMicrophoneTelemetryChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.MicrophoneTelemetry ← {value}");
        PushTelemetryToSettings();
    }

    partial void OnTelemetryLatencyEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.LatencyEnabled ← {value}");
        PushTelemetryToSettings();
    }

    partial void OnTelemetryCorpusEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.CorpusEnabled ← {value}");
        PushTelemetryToSettings();
    }

    partial void OnRecordAudioCorpusChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.RecordAudioCorpus ← {value}");
        PushTelemetryToSettings();
    }

    partial void OnTelemetryStorageDirectoryChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.StorageDirectory ← \"{value}\"");
        PushTelemetryToSettings();
    }

    // ── Sync with LoggingSettingsService and TelemetrySettingsService ───────

    public DiagnosticsViewModel()
    {
        // Guard BEFORE any property assignment — same reason as GeneralViewModel.
        _isSyncing = true;

        VerboseLoggingEnabled = false;
        ApplicationLogToDisk = false;
        MicrophoneTelemetry = false;
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
            var l = LoggingSettingsService.Instance.Current;
            VerboseLoggingEnabled = l.VerboseLoggingEnabled;

            var t = TelemetrySettingsService.Instance.Current;
            ApplicationLogToDisk = t.ApplicationLogToDisk;
            MicrophoneTelemetry = t.MicrophoneTelemetry;
            TelemetryLatencyEnabled = t.LatencyEnabled;
            TelemetryCorpusEnabled = t.CorpusEnabled;
            RecordAudioCorpus = t.RecordAudioCorpus;
            TelemetryStorageDirectory = t.StorageDirectory;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void PushLoggingToSettings()
    {
        var l = LoggingSettingsService.Instance.Current;
        l.VerboseLoggingEnabled = VerboseLoggingEnabled;
        LoggingSettingsService.Instance.Save();
    }

    private void PushTelemetryToSettings()
    {
        var t = TelemetrySettingsService.Instance.Current;
        t.ApplicationLogToDisk = ApplicationLogToDisk;
        t.MicrophoneTelemetry = MicrophoneTelemetry;
        t.LatencyEnabled = TelemetryLatencyEnabled;
        t.CorpusEnabled = TelemetryCorpusEnabled;
        t.RecordAudioCorpus = RecordAudioCorpus;
        t.StorageDirectory = TelemetryStorageDirectory ?? "";
        TelemetrySettingsService.Instance.Save();
    }

    // ── Reset ───────────────────────────────────────────────────────────────

    public void ResetLoggingDefaults()
    {
        _isSyncing = true;
        try
        {
            VerboseLoggingEnabled = false;
        }
        finally { _isSyncing = false; }
        PushLoggingToSettings();
        _log.Info(LogSource.SetGeneral, "Logging section reset to defaults");
    }

    public void ResetTelemetryDefaults()
    {
        _isSyncing = true;
        try
        {
            ApplicationLogToDisk = false;
            MicrophoneTelemetry = false;
            TelemetryLatencyEnabled = false;
            TelemetryCorpusEnabled = false;
            RecordAudioCorpus = false;
            TelemetryStorageDirectory = "";
        }
        finally { _isSyncing = false; }
        PushTelemetryToSettings();
        _log.Info(LogSource.SetGeneral, "Telemetry section reset to defaults");
    }
}
