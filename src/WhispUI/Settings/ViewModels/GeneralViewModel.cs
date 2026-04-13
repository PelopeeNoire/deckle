using CommunityToolkit.Mvvm.ComponentModel;
using WhispUI.Logging;

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

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Start minimized ← {value}");
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

    // ── Sync with SettingsService ────────────────────────────────────────────

    public GeneralViewModel()
    {
        // Guard BEFORE any property assignment — the partial property setters
        // trigger OnXChanged which would call PushToSettings() and corrupt
        // the POCO with partially-initialized defaults.
        _isSyncing = true;

        AudioInputDeviceId = -1;
        OverlayEnabled = true;
        OverlayFadeOnProximity = true;
        OverlayPosition = "BottomCenter";
        StartMinimized = true;
        Theme = "System";

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var s = SettingsService.Instance.Current;
            AudioInputDeviceId = s.Recording.AudioInputDeviceId;
            OverlayEnabled = s.Overlay.Enabled;
            OverlayFadeOnProximity = s.Overlay.FadeOnProximity;
            OverlayPosition = s.Overlay.Position;
            StartMinimized = s.Startup.StartMinimized;
            Theme = s.Appearance.Theme;
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
        s.Overlay.Enabled = OverlayEnabled;
        s.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        s.Overlay.Position = OverlayPosition;
        s.Startup.StartMinimized = StartMinimized;
        s.Appearance.Theme = Theme;
        SettingsService.Instance.Save();
    }
}
