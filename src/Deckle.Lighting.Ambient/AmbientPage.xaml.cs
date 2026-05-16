using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Deckle.Lighting.Ambient;

// Settings page for the Ambient Light module. Resolved by the Settings
// NavigationView from src/Deckle.Settings/SettingsWindow.xaml via the
// item Tag "Deckle.Lighting.Ambient.AmbientPage, Deckle.Lighting.Ambient".
//
// V0 surface : master Enabled toggle, Mode selector (Game / Realistic),
// HDR tuning sliders (Exposure / Saturation / Min brightness). Pair
// flow, Entertainment Area selection, per-light zone assignment and
// per-light brightness sliders still live in the Playground for this
// pass — phase I migrates them here.
//
// Persistence : event-handler style (Toggled / SelectionChanged /
// ValueChanged) that mutes AmbientSettings.Current and calls
// AmbientSettingsService.Instance.Save() inline. No view-model layer
// — the page is short enough that the indirection wouldn't add
// clarity. The _loading flag suppresses the initial Load-time
// synchronisation so the very first round of property assignments
// doesn't trigger a Save.
public sealed partial class AmbientPage : Page
{
    private bool _loading = true;

    public AmbientPage()
    {
        InitializeComponent();
        Loaded += AmbientPage_Loaded;
    }

    private void AmbientPage_Loaded(object sender, RoutedEventArgs e)
    {
        var s = AmbientSettingsService.Instance.Current;

        EnabledToggle.IsOn = s.Enabled;

        // Mode combo : match the Tag to the enum name. Defaults to
        // index 0 (Game) if the persisted value doesn't match — keeps
        // a freshly-introduced enum value from leaving the combo
        // blank.
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

        _loading = false;
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
