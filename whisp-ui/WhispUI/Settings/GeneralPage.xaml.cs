using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Logging;

namespace WhispUI.Settings;

public sealed partial class GeneralPage : Page
{
    private static readonly LogService _log = LogService.Instance;
    private bool _loading;

    public GeneralPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        _loading = true;
        try
        {
            PopulateAudioInputDevices();
            LoadOverlaySettings();
            LoadStartupSettings();
            LoadThemeSettings();
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Microphone source ────────────────────────────────────────────────────
    //
    // Énumère les périphériques waveIn via l'API Win32, peuple le ComboBox
    // avec "System default" en premier puis chaque device par nom.
    // L'index stocké dans AppSettings.Recording.AudioInputDeviceId est -1
    // pour WAVE_MAPPER (défaut) ou l'index waveIn (0-based).

    private void PopulateAudioInputDevices()
    {
        AudioInputCombo.Items.Clear();
        AudioInputCombo.Items.Add("System default");

        uint numDevs = NativeMethods.waveInGetNumDevs();
        for (uint i = 0; i < numDevs; i++)
        {
            var caps = new NativeMethods.WAVEINCAPSW();
            uint err = NativeMethods.waveInGetDevCapsW(i, ref caps, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WAVEINCAPSW>());
            string name = err == 0 ? caps.szPname : $"Device {i}";
            AudioInputCombo.Items.Add(name);
        }

        int configuredId = SettingsService.Instance.Current.Recording.AudioInputDeviceId;
        // configuredId = -1 → index 0 ("System default"), sinon +1 (offset du premier item)
        int comboIndex = configuredId < 0 ? 0 : configuredId + 1;
        if (comboIndex >= AudioInputCombo.Items.Count)
            comboIndex = 0; // device disparu → retour au défaut
        AudioInputCombo.SelectedIndex = comboIndex;
    }

    private void AudioInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int comboIndex = AudioInputCombo.SelectedIndex;
        // index 0 = "System default" → deviceId -1; otherwise index - 1
        int deviceId = comboIndex <= 0 ? -1 : comboIndex - 1;

        _log.Info(LogSource.SetGeneral, $"Audio input device ← {deviceId} ({AudioInputCombo.SelectedItem})");
        SettingsService.Instance.Current.Recording.AudioInputDeviceId = deviceId;
        SettingsService.Instance.Save();
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    private void LoadOverlaySettings()
    {
        var overlay = SettingsService.Instance.Current.Overlay;
        OverlayEnabledToggle.IsOn = overlay.Enabled;
        OverlayFadeToggle.IsOn = overlay.FadeOnProximity;

        // Position ComboBox: match by Tag
        for (int i = 0; i < OverlayPositionCombo.Items.Count; i++)
        {
            if (OverlayPositionCombo.Items[i] is ComboBoxItem item &&
                item.Tag as string == overlay.Position)
            {
                OverlayPositionCombo.SelectedIndex = i;
                break;
            }
        }
        if (OverlayPositionCombo.SelectedIndex < 0)
            OverlayPositionCombo.SelectedIndex = 0;
    }

    private void OverlayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _log.Info(LogSource.SetGeneral, $"Overlay enabled ← {OverlayEnabledToggle.IsOn}");
        SettingsService.Instance.Current.Overlay.Enabled = OverlayEnabledToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void OverlayFadeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _log.Info(LogSource.SetGeneral, $"Overlay fade ← {OverlayFadeToggle.IsOn}");
        SettingsService.Instance.Current.Overlay.FadeOnProximity = OverlayFadeToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void OverlayPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (OverlayPositionCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string position)
        {
            _log.Info(LogSource.SetGeneral, $"Overlay position ← {position}");
            SettingsService.Instance.Current.Overlay.Position = position;
            SettingsService.Instance.Save();
        }
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private void LoadStartupSettings()
    {
        StartMinimizedToggle.IsOn = SettingsService.Instance.Current.Startup.StartMinimized;
    }

    private void StartMinimizedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _log.Info(LogSource.SetGeneral, $"Start minimized ← {StartMinimizedToggle.IsOn}");
        SettingsService.Instance.Current.Startup.StartMinimized = StartMinimizedToggle.IsOn;
        SettingsService.Instance.Save();
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    private void LoadThemeSettings()
    {
        string theme = SettingsService.Instance.Current.Appearance.Theme;
        for (int i = 0; i < ThemeCombo.Items.Count; i++)
        {
            if (ThemeCombo.Items[i] is ComboBoxItem item &&
                item.Tag as string == theme)
            {
                ThemeCombo.SelectedIndex = i;
                break;
            }
        }
        if (ThemeCombo.SelectedIndex < 0)
            ThemeCombo.SelectedIndex = 0;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string theme)
        {
            _log.Info(LogSource.SetGeneral, $"Theme ← {theme}");
            SettingsService.Instance.Current.Appearance.Theme = theme;
            SettingsService.Instance.Save();
            App.ApplyTheme(theme);
        }
    }
}
