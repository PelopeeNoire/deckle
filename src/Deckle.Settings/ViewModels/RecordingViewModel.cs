using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Capture;
using Deckle.Logging;

namespace Deckle.Settings.ViewModels;

// ViewModel for RecordingPage — bridges CaptureSettings (audio device,
// level window) to the XAML via x:Bind. Migrated from GeneralViewModel
// in slice S3 ; in pass2 the Behaviour properties (auto-paste + overlay)
// were moved to GeneralViewModel because they describe the app's overall
// behaviour, not the capture pipeline. What remains here is microphone
// device selection and voice level window calibration.
//
// Pattern: Load() pulls from the POCOs, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
// Level window changes also push directly into the AudioLevelMapper
// statics via SettingsHost.ApplyLevelWindow so the HUD reflects the new
// curve on the next sub-window without restart.
public partial class RecordingViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

    // ── Microphone ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int AudioInputDeviceId { get; set; }

    partial void OnAudioInputDeviceIdChanged(int value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Audio input device ← {value}");
        PushToSettings();
    }

    // ── Level window (calibration) ──────────────────────────────────────────

    [ObservableProperty]
    public partial double LevelWindowMinDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowMaxDbfs { get; set; }

    [ObservableProperty]
    public partial double LevelWindowExponent { get; set; }

    [ObservableProperty]
    public partial bool LevelWindowAutoCalibration { get; set; }

    // Slider drags fire ValueChanged on every step (50+ events per drag),
    // so we keep the per-edit log line at Verbose level. PushToSettings
    // is fine on every step (the file save is debounced one level deeper
    // inside SettingsService); SettingsHost.ApplyLevelWindow ultimately
    // writes a few static fields in Capture.AudioLevelMapper, also free.
    partial void OnLevelWindowMinDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.MinDbfs ← {value:F1} dBFS");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(CaptureSettingsService.Instance.Current.LevelWindow);
    }

    partial void OnLevelWindowMaxDbfsChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.MaxDbfs ← {value:F1} dBFS");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(CaptureSettingsService.Instance.Current.LevelWindow);
    }

    partial void OnLevelWindowExponentChanged(double value)
    {
        if (_isSyncing) return;
        _log.Verbose(LogSource.SetGeneral, $"LevelWindow.DbfsCurveExponent ← {value:F2}");
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(CaptureSettingsService.Instance.Current.LevelWindow);
    }

    partial void OnLevelWindowAutoCalibrationChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"LevelWindow.AutoCalibration ← {value}");
        PushToSettings();
    }

    // ── Sync with CaptureSettingsService ────────────────────────────────────

    public RecordingViewModel()
    {
        _isSyncing = true;

        AudioInputDeviceId = -1;
        LevelWindowMinDbfs = -55;
        LevelWindowMaxDbfs = -32;
        LevelWindowExponent = 1.0;
        LevelWindowAutoCalibration = false;

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var capture = CaptureSettingsService.Instance.Current;

            AudioInputDeviceId = capture.AudioInputDeviceId;
            LevelWindowMinDbfs = capture.LevelWindow.MinDbfs;
            LevelWindowMaxDbfs = capture.LevelWindow.MaxDbfs;
            LevelWindowExponent = capture.LevelWindow.DbfsCurveExponent;
            LevelWindowAutoCalibration = capture.LevelWindow.AutoCalibrationEnabled;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void PushToSettings()
    {
        var capture = CaptureSettingsService.Instance.Current;

        capture.AudioInputDeviceId = AudioInputDeviceId;
        capture.LevelWindow.MinDbfs = (float)LevelWindowMinDbfs;
        capture.LevelWindow.MaxDbfs = (float)LevelWindowMaxDbfs;
        capture.LevelWindow.DbfsCurveExponent = (float)LevelWindowExponent;
        capture.LevelWindow.AutoCalibrationEnabled = LevelWindowAutoCalibration;

        CaptureSettingsService.Instance.Save();
    }

    public void ResetRecordingDefaults()
    {
        _isSyncing = true;
        try
        {
            AudioInputDeviceId = -1;
            LevelWindowMinDbfs = -55;
            LevelWindowMaxDbfs = -32;
            LevelWindowExponent = 1.0;
            LevelWindowAutoCalibration = false;
        }
        finally { _isSyncing = false; }
        PushToSettings();
        SettingsHost.ApplyLevelWindow?.Invoke(CaptureSettingsService.Instance.Current.LevelWindow);
        _log.Info(LogSource.SetGeneral, "Recording section reset to defaults");
    }
}
