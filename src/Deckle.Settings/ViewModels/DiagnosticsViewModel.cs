using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;

namespace Deckle.Settings.ViewModels;

// ViewModel for DiagnosticsPage — bridges TelemetrySettings to the XAML
// via x:Bind. Migrated from GeneralViewModel in slice S2 along with the
// page extraction (Telemetry was previously a section under General;
// it's now a dedicated page with room for future sections like
// log-level and LogWindow buffer settings).
//
// Pattern: Load() pulls from the POCO, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
public partial class DiagnosticsViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

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

    partial void OnApplicationLogToDiskChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.ApplicationLogToDisk ← {value}");
        PushToSettings();
    }

    partial void OnMicrophoneTelemetryChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Telemetry.MicrophoneTelemetry ← {value}");
        PushToSettings();
    }

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

    // ── Sync with TelemetrySettingsService ───────────────────────────────────

    public DiagnosticsViewModel()
    {
        // Guard BEFORE any property assignment — same reason as GeneralViewModel.
        _isSyncing = true;

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

    private void PushToSettings()
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
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Telemetry section reset to defaults");
    }
}
