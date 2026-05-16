using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Deckle.Lighting.Ambient;

// Settings page for the Ambient Light module. Resolved by the Settings
// NavigationView from src/Deckle.Settings/SettingsWindow.xaml via the
// item Tag "Deckle.Lighting.Ambient.AmbientPage, Deckle.Lighting.Ambient".
//
// V0 surface : master Enabled toggle (with ProgressRing for transient
// Starting / Stopping), Mode selector (Game / Realistic), HDR tuning
// sliders (Exposure / Saturation / Min brightness), NotPaired InfoBar
// (Warning) with "Open Playground" action when the persisted Hue
// state is incomplete. Pair flow + Entertainment Area + Light zones +
// per-light brightness still live in the Playground for this pass —
// the InfoBar redirects there until they migrate.
//
// Persistence : event-handler style (Toggled / SelectionChanged /
// ValueChanged) that mutates AmbientSettings.Current and calls
// AmbientSettingsService.Instance.Save() inline. No view-model layer.
//
// Sync state : two subscriptions wired in Loaded and dropped in
// Unloaded. The Changed event re-syncs the controls from settings so
// a flip from the tray / Playground propagates immediately to the
// ToggleSwitch. The StateChanged event drives the transient UI
// (ProgressRing, ModeCombo gating). Both subscriptions are guarded by
// a _loading flag that suppresses the re-fire loop when handlers
// touch the same controls that triggered them.
public sealed partial class AmbientPage : Page
{
    private bool _loading = true;

    public AmbientPage()
    {
        InitializeComponent();
        Loaded   += AmbientPage_Loaded;
        Unloaded += AmbientPage_Unloaded;
    }

    private void AmbientPage_Loaded(object sender, RoutedEventArgs e)
    {
        ResyncFromSettings();
        ApplyEngineState(AmbientEngine.Current?.State ?? AmbientEngineState.Off);

        AmbientSettingsService.Instance.Changed += OnSettingsChanged;
        if (AmbientEngine.Current is not null)
        {
            AmbientEngine.Current.StateChanged += OnEngineStateChanged;
        }

        _loading = false;
    }

    private void AmbientPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AmbientSettingsService.Instance.Changed -= OnSettingsChanged;
        if (AmbientEngine.Current is not null)
        {
            AmbientEngine.Current.StateChanged -= OnEngineStateChanged;
        }
        _loading = true;
    }

    // Pulls the current persisted state into the controls. Called on
    // first load and on every Changed event. Guarded by _loading so
    // the handlers don't re-fire Save during the assignment loop.
    private void ResyncFromSettings()
    {
        bool prevLoading = _loading;
        _loading = true;
        try
        {
            var s = AmbientSettingsService.Instance.Current;

            EnabledToggle.IsOn = s.Enabled;

            ComboBoxItem? toSelect = null;
            foreach (var item in ModeCombo.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag == s.Mode.ToString())
                {
                    toSelect = cbi;
                    break;
                }
            }
            ModeCombo.SelectedItem = toSelect ?? ModeCombo.Items[0];

            ExposureSlider.Value      = s.ExposureEv;
            SaturationSlider.Value    = s.SaturationBoost * 100.0;
            MinBrightnessSlider.Value = s.MinBrightness;

            UpdateExposureText();
            UpdateSaturationText();
            UpdateMinBrightnessText();

            // Pair completeness drives the NotPaired InfoBar. The
            // criteria mirror AmbientEngine.StartAsync's validation
            // so a user who toggles ON in this state sees the InfoBar
            // BEFORE clicking (forewarned) and also AFTER the auto-
            // revert (still incomplete).
            bool paired = !string.IsNullOrEmpty(s.HueBridgeIp)
                       && !string.IsNullOrEmpty(s.HueBridgeId)
                       && !string.IsNullOrEmpty(s.HueUsername)
                       && !string.IsNullOrEmpty(s.HueLastGroupId);
            NotPairedInfoBar.IsOpen = !paired;
        }
        finally
        {
            _loading = prevLoading;
        }
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(ResyncFromSettings);
    }

    private void OnEngineStateChanged(AmbientEngineState state)
    {
        DispatcherQueue.TryEnqueue(() => ApplyEngineState(state));
    }

    // Surfaces the engine's transition state on the page. The previous
    // pass surfaced a ProgressRing for the transient Starting /
    // Stopping states, but the ring stayed stuck in one runtime
    // observation and the feature offered marginal value while a Hue
    // pair takes only ~300–800 ms ; it has been retired. The
    // ApplyEngineState pass is kept for the ModeCombo gating, which is
    // genuinely useful : changing Mode mid-Running silently desyncs
    // the radios from the pipeline shape, so we lock the combo while
    // the engine runs.
    private void ApplyEngineState(AmbientEngineState state)
    {
        ModeCombo.IsEnabled = state != AmbientEngineState.Running;
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AmbientSettingsService.Instance.Current.Enabled = EnabledToggle.IsOn;
        AmbientSettingsService.Instance.Save();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ModeCombo.SelectedItem is ComboBoxItem cbi
            && cbi.Tag is string tag
            && Enum.TryParse<AmbientMode>(tag, out var mode))
        {
            AmbientSettingsService.Instance.Current.Mode = mode;
            AmbientSettingsService.Instance.Save();
        }
    }

    private void ExposureSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateExposureText();
        if (_loading) return;
        AmbientSettingsService.Instance.Current.ExposureEv = ExposureSlider.Value;
        AmbientSettingsService.Instance.Save();
    }

    private void SaturationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateSaturationText();
        if (_loading) return;
        AmbientSettingsService.Instance.Current.SaturationBoost = SaturationSlider.Value / 100.0;
        AmbientSettingsService.Instance.Save();
    }

    private void MinBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateMinBrightnessText();
        if (_loading) return;
        AmbientSettingsService.Instance.Current.MinBrightness = (int)Math.Round(MinBrightnessSlider.Value);
        AmbientSettingsService.Instance.Save();
    }

    private void OpenPlaygroundButton_Click(object sender, RoutedEventArgs e)
    {
        AmbientEngine.OpenPlaygroundRequested?.Invoke();
    }

    private void UpdateExposureText()
    {
        double v = ExposureSlider.Value;
        ExposureValueText.Text = $"{(v >= 0 ? "+" : "")}{v:F1} EV";
    }

    private void UpdateSaturationText()
    {
        SaturationValueText.Text = $"{(int)Math.Round(SaturationSlider.Value)} %";
    }

    private void UpdateMinBrightnessText()
    {
        MinBrightnessValueText.Text = $"{(int)Math.Round(MinBrightnessSlider.Value)}";
    }
}
