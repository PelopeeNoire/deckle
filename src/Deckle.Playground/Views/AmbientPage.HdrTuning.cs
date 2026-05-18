using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Deckle.Lighting.Ambient;

namespace Deckle.Playground;

// ─── AmbientPage — pipeline + HDR tuning sliders ────────────────────────────
//
// Pipeline toggle (master Enabled flag of the AmbientEngine) and the
// HDR knobs sandbox : Mode preset, Brightness curve (type + param),
// Smoothing α, Change threshold, Exposure, Saturation, Min brightness.
// Slider handlers write through the ViewModel which persists into
// AmbientSettings via AmbientSettingsService — same store the Settings
// AmbientPage and the engine itself consume.
//
// PushViewModelToControls is the inverse pump : after ViewModel.Load
// pulls fresh values from AmbientSettings, this method seeds every
// slider / combo / canvas so the visuals match. Wrapped by the caller
// in `_initializing = true` so the synthetic ValueChanged events fired
// by Slider.Value = X don't loop back through the VM setters.

public sealed partial class AmbientPage
{
    // ── Pipeline ────────────────────────────────────────────────────────────

    private void SetPipelineReady()
    {
        PipelineToggleButton.IsEnabled = true;
        SyncPipelineUiFromViewModel();
    }

    private void SetPipelineNotReady()
    {
        PipelineToggleButton.IsEnabled = false;
        PipelineToggleIcon.Glyph = ScreenCaptureGlyphStart;
        PipelineToggleLabel.Text = "Turn Ambient Light on";
        PipelineStatusText.Text = "Pair a bridge and pick a group first";
        PipelineStatusDot.Fill = GetThemeBrush("SystemFillColorNeutralBrush");
    }

    private void OnPipelineToggleClick(object sender, RoutedEventArgs e)
    {
        // Flip AmbientSettings.Enabled through the VM ; the VM's
        // OnEnabledChanged persists, the AmbientEngine observer in App
        // starts / stops the canonical pipeline.
        ViewModel.Enabled = !ViewModel.Enabled;
    }

    private void SyncPipelineUiFromViewModel()
    {
        if (PipelineToggleButton is null) return;
        bool enabled = ViewModel.Enabled;
        PipelineToggleIcon.Glyph  = enabled ? ScreenCaptureGlyphStop : ScreenCaptureGlyphStart;
        PipelineToggleLabel.Text  = enabled ? "Turn Ambient Light off" : "Turn Ambient Light on";
        PipelineStatusText.Text   = enabled ? "Running" : "Stopped";
        PipelineStatusDot.Fill    = GetThemeBrush(enabled
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorNeutralBrush");

        int desiredIndex = ViewModel.UseMultiLight ? 1 : 0;
        if (PipelineModeRadios is not null
            && PipelineModeRadios.SelectedIndex != desiredIndex)
        {
            PipelineModeRadios.SelectedIndex = desiredIndex;
        }
    }

    private void ApplyPipelineReadiness()
    {
        if (PipelineToggleButton is null) return;
        var s = AmbientSettingsService.Instance.Current;
        bool paired = HuePairingService.Instance.Bridge?.IsPaired == true;
        bool hasGroup = !string.IsNullOrEmpty(s.HueLastGroupId);
        if (paired && hasGroup) SetPipelineReady();
        else                    SetPipelineNotReady();
    }

    private void OnPipelineModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not RadioButtons radios) return;
        bool useMulti = radios.SelectedIndex switch
        {
            0 => false,
            1 => true,
            _ => ViewModel.UseMultiLight,
        };
        if (ViewModel.UseMultiLight == useMulti) return;
        ViewModel.UseMultiLight = useMulti;
    }

    // ── HDR tuning : combo selectors + slider Min/Max + canvas type ─────────

    private double SelectCurveParamForType(BrightnessCurveType type)
        => type switch
        {
            BrightnessCurveType.Gamma  => ViewModel.BrightnessCurveParam,
            BrightnessCurveType.SCurve => ViewModel.BrightnessCurveSCurveSteepness,
            _                          => ViewModel.BrightnessCurveParam,
        };

    private void SelectBrightnessCurveTypeInCombo(BrightnessCurveType type)
    {
        string tag = type.ToString();
        for (int i = 0; i < PlaygroundBrightnessCurveCombo.Items.Count; i++)
        {
            if (PlaygroundBrightnessCurveCombo.Items[i] is ComboBoxItem cbi
                && (cbi.Tag as string) == tag)
            {
                PlaygroundBrightnessCurveCombo.SelectedIndex = i;
                return;
            }
        }
        PlaygroundBrightnessCurveCombo.SelectedIndex = 1;
    }

    private void UpdatePlaygroundBrightnessCurveDependentUi()
    {
        var type = ReadBrightnessCurveTypeFromCombo();
        bool paramHasEffect = type == BrightnessCurveType.Gamma
                           || type == BrightnessCurveType.SCurve;
        PlaygroundGammaSlider.IsEnabled = paramHasEffect;

        // Slider range follows the active curve : Gamma stays close
        // to its practical interval [1.0, 3.0] ; SCurve goes all the
        // way to 15. Order matters when shrinking (Max < current Value
        // clamps Value), so we always set Max before Value is
        // re-projected by the caller.
        switch (type)
        {
            case BrightnessCurveType.Gamma:
                PlaygroundGammaSlider.Maximum = 3.0;
                PlaygroundGammaSlider.Minimum = 1.0;
                break;
            case BrightnessCurveType.SCurve:
                PlaygroundGammaSlider.Maximum = 15.0;
                PlaygroundGammaSlider.Minimum = 1.0;
                break;
            default:
                PlaygroundGammaSlider.Maximum = 15.0;
                PlaygroundGammaSlider.Minimum = 1.0;
                break;
        }

        PlaygroundGammaCanvas.CurveType = type;
        PlaygroundGammaCanvas.Opacity   = paramHasEffect ? 1.0 : 0.4;

        PlaygroundGammaSliderRow.Visibility = paramHasEffect
            ? Visibility.Visible
            : Visibility.Collapsed;

        PlaygroundGammaCaption.Text = type switch
        {
            BrightnessCurveType.Linear      => "Direct pass-through — input max channel is sent to the lamp as-is. No parameter to tune ; rely on smoothing and min brightness for fine control.",
            BrightnessCurveType.Gamma       => "Power-law squash on the bottom of the bri range. Higher γ dims dim scenes harder without touching saturated highlights. 1.0 — linear · 1.8 — default · 2.5 — strongly dimmed shadows.",
            BrightnessCurveType.SCurve      => "Logistic S-curve pushed mid-tones away from grey in both directions. Higher steepness = harder contrast. 1.0 — almost linear · 2.0 — default · 5.0 — near-step.",
            BrightnessCurveType.Logarithmic => "Lifts the bottom of the range so even very dim scenes stay clearly lit. No parameter to tune — the curve is fixed.",
            _ => string.Empty,
        };

        // Smoothing slider range — same constraint as the gamma
        // slider above. Maximum first, then Minimum (Range invariant).
        PlaygroundSmoothingSlider.Maximum = 1.0;
        PlaygroundSmoothingSlider.Minimum = 0.05;
    }

    private BrightnessCurveType ReadBrightnessCurveTypeFromCombo()
    {
        if (PlaygroundBrightnessCurveCombo.SelectedItem is ComboBoxItem cbi
         && cbi.Tag is string tag
         && Enum.TryParse<BrightnessCurveType>(tag, out var parsed))
        {
            return parsed;
        }
        return BrightnessCurveType.Gamma;
    }

    private void SelectAmbientModeInCombo(AmbientMode mode)
    {
        string tag = mode.ToString();
        for (int i = 0; i < PlaygroundAmbientModeCombo.Items.Count; i++)
        {
            if (PlaygroundAmbientModeCombo.Items[i] is ComboBoxItem cbi
                && (cbi.Tag as string) == tag)
            {
                PlaygroundAmbientModeCombo.SelectedIndex = i;
                return;
            }
        }
        PlaygroundAmbientModeCombo.SelectedIndex = 3;
    }

    // ── Push VM → controls ──────────────────────────────────────────────────
    //
    // Inverse of the slider handlers below. Seeded after ViewModel.Load
    // by both OnPageLoaded (first nav) and OnNavigatedTo (cached page
    // reuse) ; the caller is responsible for wrapping in
    // `_initializing = true` so the synthetic ValueChanged events fired
    // here don't loop back through the VM setters and re-Save.

    private void PushViewModelToControls()
    {
        PlaygroundExposureSlider.Value         = ViewModel.ExposureEv;
        PlaygroundSaturationSlider.Value       = ViewModel.SaturationBoost * 100.0;
        PlaygroundMinBrightnessSlider.Value    = ViewModel.MinBrightness;

        // ComboBoxes first : SelectBrightnessCurveTypeInCombo drives
        // the curve type that UpdatePlaygroundBrightnessCurveDependentUi
        // reads to rescale the param slider Max. Then Max is set. Only
        // then can the Value safely take any SCurve k up to 15 without
        // being clamped by a stale Gamma 3.0 ceiling.
        SelectBrightnessCurveTypeInCombo(ViewModel.BrightnessCurveType);
        SelectAmbientModeInCombo(ViewModel.Mode);
        UpdatePlaygroundBrightnessCurveDependentUi();

        double curveParam = SelectCurveParamForType(ViewModel.BrightnessCurveType);
        PlaygroundGammaSlider.Value            = curveParam;
        PlaygroundGammaCanvas.Gamma            = curveParam;
        PlaygroundSmoothingSlider.Value        = ViewModel.SmoothingAlpha;
        PlaygroundChangeThresholdSlider.Value  = ViewModel.ChangeThreshold;

        UpdatePlaygroundExposureText();
        UpdatePlaygroundSaturationText();
        UpdatePlaygroundMinBrightnessText();
        UpdatePlaygroundGammaText();
        UpdatePlaygroundSmoothingText();
        UpdatePlaygroundChangeThresholdText();
    }

    // ── Slider handlers ─────────────────────────────────────────────────────

    private void OnPlaygroundGammaSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundGammaText();
        PlaygroundGammaCanvas.Gamma = PlaygroundGammaSlider.Value;

        if (_initializing) return;

        // Route to the parameter that belongs to the active curve,
        // leaving the other curves' parameters untouched. Linear /
        // Logarithmic ignore the slider but we still write through
        // into the Gamma slot so the value is preserved if the user
        // switches back later.
        var type = ReadBrightnessCurveTypeFromCombo();
        switch (type)
        {
            case BrightnessCurveType.SCurve:
                ViewModel.BrightnessCurveSCurveSteepness = PlaygroundGammaSlider.Value;
                break;
            default:
                ViewModel.BrightnessCurveParam = PlaygroundGammaSlider.Value;
                break;
        }
    }

    private void OnPlaygroundExposureSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundExposureText();
        if (_initializing) return;
        ViewModel.ExposureEv = PlaygroundExposureSlider.Value;
    }

    private void OnPlaygroundSaturationSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundSaturationText();
        if (_initializing) return;
        ViewModel.SaturationBoost = PlaygroundSaturationSlider.Value / 100.0;
    }

    private void OnPlaygroundMinBrightnessSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundMinBrightnessText();
        if (_initializing) return;
        ViewModel.MinBrightness = (int)Math.Round(PlaygroundMinBrightnessSlider.Value);
    }

    private void OnPlaygroundSmoothingSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundSmoothingText();
        if (_initializing) return;
        ViewModel.SmoothingAlpha = PlaygroundSmoothingSlider.Value;
    }

    private void OnPlaygroundChangeThresholdSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundChangeThresholdText();
        if (_initializing) return;
        ViewModel.ChangeThreshold = (int)Math.Round(PlaygroundChangeThresholdSlider.Value);
    }

    private void OnPlaygroundBrightnessCurveTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var type = ReadBrightnessCurveTypeFromCombo();

        // Order matters when the slider range is shrinking : Max must
        // be set before Value so we don't transiently clamp a valid
        // SCurve k=15 onto Gamma's 3.0 ceiling and lose the user's
        // intent.
        UpdatePlaygroundBrightnessCurveDependentUi();
        bool prev = _initializing;
        _initializing = true;
        try
        {
            PlaygroundGammaSlider.Value = SelectCurveParamForType(type);
        }
        finally
        {
            _initializing = prev;
        }

        UpdatePlaygroundGammaText();

        if (_initializing) return;
        ViewModel.BrightnessCurveType = type;
    }

    private void OnPlaygroundAmbientModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (PlaygroundAmbientModeCombo.SelectedItem is ComboBoxItem cbi
            && cbi.Tag is string tag
            && Enum.TryParse<AmbientMode>(tag, out var mode))
        {
            // VM.OnModeChanged calls ApplyPreset which copies the
            // preset's tunings onto every other knob and fires Changed
            // → OnAmbientSettingsChanged → PushViewModelToControls.
            // All sliders refresh in one pass.
            ViewModel.Mode = mode;
        }
    }

    // ── Value-text formatters ───────────────────────────────────────────────

    private void UpdatePlaygroundGammaText()
    {
        var type = ReadBrightnessCurveTypeFromCombo();
        PlaygroundGammaValueText.Text = type switch
        {
            BrightnessCurveType.Gamma  => $"γ {PlaygroundGammaSlider.Value:F2}",
            BrightnessCurveType.SCurve => $"k {PlaygroundGammaSlider.Value:F2}",
            _                          => "—",
        };
    }

    private void UpdatePlaygroundExposureText()
    {
        double v = PlaygroundExposureSlider.Value;
        PlaygroundExposureValueText.Text = $"{(v >= 0 ? "+" : "")}{v:F1} EV";
    }

    private void UpdatePlaygroundSaturationText()
        => PlaygroundSaturationValueText.Text = $"{(int)Math.Round(PlaygroundSaturationSlider.Value)} %";

    private void UpdatePlaygroundMinBrightnessText()
        => PlaygroundMinBrightnessValueText.Text = $"{(int)Math.Round(PlaygroundMinBrightnessSlider.Value)}";

    private void UpdatePlaygroundSmoothingText()
        => PlaygroundSmoothingValueText.Text = $"α {PlaygroundSmoothingSlider.Value:F2}";

    private void UpdatePlaygroundChangeThresholdText()
        => PlaygroundChangeThresholdValueText.Text = $"{(int)Math.Round(PlaygroundChangeThresholdSlider.Value)}";
}
