using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Capture;
using Deckle.Logging;

namespace Deckle.Settings.ViewModels;

// ViewModel for RecordingPage — bridges CaptureSettings (audio device,
// level window) + AppSettings.Paste/Overlay to the XAML via x:Bind.
// Migrated from GeneralViewModel in slice S3 along with the page
// extraction (Recording was previously a section under General; it's
// now a dedicated page that owns everything around the capture
// pipeline and its visual feedback in the HUD).
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

    // ── Microphone + paste ──────────────────────────────────────────────────

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

    // ── Overlay HUD ──────────────────────────────────────────────────────────

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

    // ── Sync with Capture + Shell SettingsService ───────────────────────────

    public RecordingViewModel()
    {
        _isSyncing = true;

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

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var capture = CaptureSettingsService.Instance.Current;
            var shell   = SettingsService.Instance.Current;

            AudioInputDeviceId = capture.AudioInputDeviceId;
            AutoPasteEnabled = shell.Paste.AutoPasteEnabled;
            LevelWindowMinDbfs = capture.LevelWindow.MinDbfs;
            LevelWindowMaxDbfs = capture.LevelWindow.MaxDbfs;
            LevelWindowExponent = capture.LevelWindow.DbfsCurveExponent;
            LevelWindowAutoCalibration = capture.LevelWindow.AutoCalibrationEnabled;
            OverlayEnabled = shell.Overlay.Enabled;
            OverlayFadeOnProximity = shell.Overlay.FadeOnProximity;
            OverlayAnimations = shell.Overlay.Animations;
            OverlayPosition = shell.Overlay.Position;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void PushToSettings()
    {
        var capture = CaptureSettingsService.Instance.Current;
        var shell   = SettingsService.Instance.Current;

        capture.AudioInputDeviceId = AudioInputDeviceId;
        capture.LevelWindow.MinDbfs = (float)LevelWindowMinDbfs;
        capture.LevelWindow.MaxDbfs = (float)LevelWindowMaxDbfs;
        capture.LevelWindow.DbfsCurveExponent = (float)LevelWindowExponent;
        capture.LevelWindow.AutoCalibrationEnabled = LevelWindowAutoCalibration;

        shell.Paste.AutoPasteEnabled = AutoPasteEnabled;
        shell.Overlay.Enabled = OverlayEnabled;
        shell.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        shell.Overlay.Animations = OverlayAnimations;
        shell.Overlay.Position = OverlayPosition;

        CaptureSettingsService.Instance.Save();
        SettingsService.Instance.Save();
    }

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
        SettingsHost.ApplyLevelWindow?.Invoke(CaptureSettingsService.Instance.Current.LevelWindow);
        _log.Info(LogSource.SetGeneral, "Recording section reset to defaults");
    }
}
