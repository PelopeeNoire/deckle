using System.Numerics;
using Deckle.Audio;
using Deckle.Composition;

namespace Deckle.Playground;

// ─── HudPage — tuning expanders ─────────────────────────────────────────────
//
// One Add*Expander method per logical knob group (palette, hue rotation,
// arc rotation, recording variant, etc.) plus the matching Reset*
// method that snaps the group back to compiled defaults. Filtered by
// HudViewModel.ActiveTuningSections so only the expanders that affect
// the active target are rendered ; Parked is always appended (collapsed
// by default) for the transition-only knobs.
//
// Step-frequency convention (kept aligned with HudPage.RowFactories) :
//   fractional [0..1, ease Y, saturation/opacity/hue]   → 0.05
//   period seconds [0..60, 0.5..30]                     → 0.5
//   short period (Swipe / fade curve)                   → 0.1
//   exposure [-2..2]                                    → 0.1
//   blend seconds [0..5]                                → 0.1
//   dBFS (MinDbfs / MaxDbfs)                            → 1
//   sim RMS values [0..0.3]                             → 0.001
//   WedgeCount (int)                                    → 1

public sealed partial class HudPage
{
    // HUD geometry — dimensions of the stroke rect itself, independent
    // of any conic/arc/recording variant. InsetDip lives on
    // HudComposition (paint-time, baked into the stroke geometry on
    // creation), so the slider must trigger a stroke rebuild on change.
    private void AddHudGeometryExpander()
    {
        var stack = NewExpander("HUD geometry", ResetHudGeometry);
        AddFloatRow(stack, "InsetDip", -16, 4, 0.5, HudComposition.InsetDip,
            v => HudComposition.InsetDip = (float)v, rebuild: true);
    }

    private void ResetHudGeometry()
    {
        HudComposition.InsetDip = -2f;
        RebuildTuningPanel();
        RequestRebuild();
    }

    private void AddPaletteExpander()
    {
        var stack = NewExpander("Palette (OKLCh)", ResetPalette);
        AddFloatRow(stack, "OklchLightness", 0, 1, 0.05, _tuning.OklchLightness,
            v => _tuning.OklchLightness = (float)v, rebuild: true);
        AddFloatRow(stack, "OklchChroma", 0, 0.4, 0.05, _tuning.OklchChroma,
            v => _tuning.OklchChroma = (float)v, rebuild: true);
        AddFloatRow(stack, "HueStart", 0, 1, 0.05, _tuning.HueStart,
            v => _tuning.HueStart = (float)v, rebuild: true);
        AddFloatRow(stack, "HueRange", 0, 1, 0.05, _tuning.HueRange,
            v => _tuning.HueRange = (float)v, rebuild: true);
        AddIntRow(stack, "WedgeCount", 16, 720, _tuning.WedgeCount,
            v => _tuning.WedgeCount = v, rebuild: true);
    }

    private void ResetPalette()
    {
        var d = new TuningModel();
        _tuning.OklchLightness = d.OklchLightness;
        _tuning.OklchChroma    = d.OklchChroma;
        _tuning.HueStart       = d.HueStart;
        _tuning.HueRange       = d.HueRange;
        _tuning.WedgeCount     = d.WedgeCount;
        RebuildTuningPanel();
    }

    private void AddConicFadeExpander()
    {
        var stack = NewExpander("Conic fade & span", ResetConicFade);
        AddFloatRow(stack, "ConicSpanTurns", 0.05, 1.0, 0.05, _tuning.ConicSpanTurns,
            v => _tuning.ConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicLeadFadeTurns", 0, 1, 0.05, _tuning.ConicLeadFadeTurns,
            v => _tuning.ConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicTailFadeTurns", 0, 1, 0.05, _tuning.ConicTailFadeTurns,
            v => _tuning.ConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicFadeCurve", 0.5, 10, 0.1, _tuning.ConicFadeCurve,
            v => _tuning.ConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "ArcMirror", _tuning.ArcMirror,
            v => _tuning.ArcMirror = v, rebuild: true);
    }

    private void ResetConicFade()
    {
        var d = new TuningModel();
        _tuning.ConicSpanTurns     = d.ConicSpanTurns;
        _tuning.ConicLeadFadeTurns = d.ConicLeadFadeTurns;
        _tuning.ConicTailFadeTurns = d.ConicTailFadeTurns;
        _tuning.ConicFadeCurve     = d.ConicFadeCurve;
        _tuning.ArcMirror          = d.ArcMirror;
        RebuildTuningPanel();
    }

    private void AddHueRotationExpander()
    {
        var stack = NewExpander("Hue rotation", ResetHueRotation);
        AddFloatRow(stack, "HuePeriodSeconds", 0, 60, 0.5, _tuning.HuePeriodSeconds,
            v => _tuning.HuePeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "HueDirection", _tuning.HueDirection,
            v => _tuning.HueDirection = v);
        AddFloatRow(stack, "HueEaseP1.X", 0, 1, 0.05, _tuning.HueEaseP1X,
            v => _tuning.HueEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP1.Y", -0.5, 1.5, 0.05, _tuning.HueEaseP1Y,
            v => _tuning.HueEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.X", 0, 1, 0.05, _tuning.HueEaseP2X,
            v => _tuning.HueEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.Y", -0.5, 1.5, 0.05, _tuning.HueEaseP2Y,
            v => _tuning.HueEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetHueRotation()
    {
        var d = new TuningModel();
        _tuning.HuePeriodSeconds = d.HuePeriodSeconds;
        _tuning.HueDirection     = d.HueDirection;
        _tuning.HueEaseP1X       = d.HueEaseP1X;
        _tuning.HueEaseP1Y       = d.HueEaseP1Y;
        _tuning.HueEaseP2X       = d.HueEaseP2X;
        _tuning.HueEaseP2Y       = d.HueEaseP2Y;
        RebuildTuningPanel();
    }

    private void AddArcRotationExpander()
    {
        var stack = NewExpander("Arc rotation", ResetArcRotation);
        AddFloatRow(stack, "ArcPeriodSeconds", 0.5, 30, 0.5, _tuning.ArcPeriodSeconds,
            v => _tuning.ArcPeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "ArcDirection", _tuning.ArcDirection,
            v => _tuning.ArcDirection = v);
        AddFloatRow(stack, "ArcEaseP1.X", 0, 1, 0.05, _tuning.ArcEaseP1X,
            v => _tuning.ArcEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP1.Y", -0.5, 1.5, 0.05, _tuning.ArcEaseP1Y,
            v => _tuning.ArcEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.X", 0, 1, 0.05, _tuning.ArcEaseP2X,
            v => _tuning.ArcEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.Y", -0.5, 1.5, 0.05, _tuning.ArcEaseP2Y,
            v => _tuning.ArcEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetArcRotation()
    {
        var d = new TuningModel();
        _tuning.ArcPeriodSeconds = d.ArcPeriodSeconds;
        _tuning.ArcDirection     = d.ArcDirection;
        _tuning.ArcEaseP1X       = d.ArcEaseP1X;
        _tuning.ArcEaseP1Y       = d.ArcEaseP1Y;
        _tuning.ArcEaseP2X       = d.ArcEaseP2X;
        _tuning.ArcEaseP2Y       = d.ArcEaseP2Y;
        RebuildTuningPanel();
    }

    private void AddSwipeExpander()
    {
        var stack = NewExpander("Swipe (Transcribing / Rewriting)", ResetSwipe);
        // Static mutables on SwipeWaveAnimator (Deckle.Composition) —
        // read live each vsync by the animator's Tick, no rebuild needed.
        AddFloatRow(stack, "SwipeCycleSeconds", 0.1, 6.0, 0.1,
            SwipeWaveAnimator.SwipeCycleSeconds,
            v => SwipeWaveAnimator.SwipeCycleSeconds = (float)v);
        AddFloatRow(stack, "SwipeEaseP1.X", 0, 1, 0.05, SwipeWaveAnimator.SwipeEaseP1.X,
            v => SwipeWaveAnimator.SwipeEaseP1 = new Vector2((float)v, SwipeWaveAnimator.SwipeEaseP1.Y));
        AddFloatRow(stack, "SwipeEaseP1.Y", -0.5, 1.5, 0.05, SwipeWaveAnimator.SwipeEaseP1.Y,
            v => SwipeWaveAnimator.SwipeEaseP1 = new Vector2(SwipeWaveAnimator.SwipeEaseP1.X, (float)v));
        AddFloatRow(stack, "SwipeEaseP2.X", 0, 1, 0.05, SwipeWaveAnimator.SwipeEaseP2.X,
            v => SwipeWaveAnimator.SwipeEaseP2 = new Vector2((float)v, SwipeWaveAnimator.SwipeEaseP2.Y));
        AddFloatRow(stack, "SwipeEaseP2.Y", -0.5, 1.5, 0.05, SwipeWaveAnimator.SwipeEaseP2.Y,
            v => SwipeWaveAnimator.SwipeEaseP2 = new Vector2(SwipeWaveAnimator.SwipeEaseP2.X, (float)v));
        AddFloatRow(stack, "SwipeRiseAlpha", 0.01, 1.0, 0.01, SwipeWaveAnimator.SwipeRiseAlpha,
            v => SwipeWaveAnimator.SwipeRiseAlpha = (float)v);
        AddFloatRow(stack, "SwipeDecayAlpha", 0.005, 0.5, 0.005, SwipeWaveAnimator.SwipeDecayAlpha,
            v => SwipeWaveAnimator.SwipeDecayAlpha = (float)v);
        AddIntRow(stack, "SwipeHeadDomain", 6, 12, SwipeWaveAnimator.SwipeHeadDomain,
            v => SwipeWaveAnimator.SwipeHeadDomain = v);
        AddToggleRow(stack, "Simulate changed digits",
            _simulateChangedDigits,
            v => { _simulateChangedDigits = v; ApplyTarget(); });
    }

    private void ResetSwipe()
    {
        SwipeWaveAnimator.SwipeCycleSeconds = 3.0f;
        SwipeWaveAnimator.SwipeEaseP1       = new Vector2(0.7f, 0f);
        SwipeWaveAnimator.SwipeEaseP2       = new Vector2(0.1f, 1f);
        SwipeWaveAnimator.SwipeRiseAlpha    = 0.05f;
        SwipeWaveAnimator.SwipeDecayAlpha   = 0.025f;
        SwipeWaveAnimator.SwipeHeadDomain   = 8;
        _simulateChangedDigits = true;
        RebuildTuningPanel();
        ApplyTarget();
    }

    private void AddRecordingExpander()
    {
        var stack = NewExpander("Recording", ResetRecording);
        AddFloatRow(stack, "RecordingConicSpanTurns", 0.05, 1, 0.05, _tuning.RecordingConicSpanTurns,
            v => _tuning.RecordingConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicLeadFadeTurns", 0, 1, 0.05, _tuning.RecordingConicLeadFadeTurns,
            v => _tuning.RecordingConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicTailFadeTurns", 0, 1, 0.05, _tuning.RecordingConicTailFadeTurns,
            v => _tuning.RecordingConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicFadeCurve", 0.5, 10, 0.1, _tuning.RecordingConicFadeCurve,
            v => _tuning.RecordingConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "RecordingArcMirror", _tuning.RecordingArcMirror,
            v => _tuning.RecordingArcMirror = v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationDark", 0, 1, 0.05, _tuning.RecordingSaturationDark,
            v => _tuning.RecordingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationLight", 0, 1, 0.05, _tuning.RecordingSaturationLight,
            v => _tuning.RecordingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingHueShiftTurns", 0, 1, 0.05, _tuning.RecordingHueShiftTurns,
            v => _tuning.RecordingHueShiftTurns = (float)v, rebuild: true);
        // Exposure clamped to D2D1_EXPOSURE spec [-2, +2].
        AddFloatRow(stack, "RecordingExposureDark", -2, 2, 0.1, _tuning.RecordingExposureDark,
            v => _tuning.RecordingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingExposureLight", -2, 2, 0.1, _tuning.RecordingExposureLight,
            v => _tuning.RecordingExposureLight = (float)v, rebuild: true);
    }

    private void ResetRecording()
    {
        var d = new TuningModel();
        _tuning.RecordingConicSpanTurns     = d.RecordingConicSpanTurns;
        _tuning.RecordingConicLeadFadeTurns = d.RecordingConicLeadFadeTurns;
        _tuning.RecordingConicTailFadeTurns = d.RecordingConicTailFadeTurns;
        _tuning.RecordingConicFadeCurve     = d.RecordingConicFadeCurve;
        _tuning.RecordingArcMirror          = d.RecordingArcMirror;
        _tuning.RecordingSaturationDark     = d.RecordingSaturationDark;
        _tuning.RecordingSaturationLight    = d.RecordingSaturationLight;
        _tuning.RecordingHueShiftTurns      = d.RecordingHueShiftTurns;
        _tuning.RecordingExposureDark       = d.RecordingExposureDark;
        _tuning.RecordingExposureLight      = d.RecordingExposureLight;
        RebuildTuningPanel();
    }

    private void AddTranscribingExpander()
    {
        var stack = NewExpander("Transcribing", ResetTranscribing);
        AddFloatRow(stack, "TranscribingSaturationDark", 0, 1, 0.05, _tuning.TranscribingSaturationDark,
            v => _tuning.TranscribingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingSaturationLight", 0, 1, 0.05, _tuning.TranscribingSaturationLight,
            v => _tuning.TranscribingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingHueShiftTurns", 0, 1, 0.05, _tuning.TranscribingHueShiftTurns,
            v => _tuning.TranscribingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureDark", -2, 2, 0.1, _tuning.TranscribingExposureDark,
            v => _tuning.TranscribingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureLight", -2, 2, 0.1, _tuning.TranscribingExposureLight,
            v => _tuning.TranscribingExposureLight = (float)v, rebuild: true);
    }

    private void ResetTranscribing()
    {
        var d = new TuningModel();
        _tuning.TranscribingSaturationDark  = d.TranscribingSaturationDark;
        _tuning.TranscribingSaturationLight = d.TranscribingSaturationLight;
        _tuning.TranscribingHueShiftTurns   = d.TranscribingHueShiftTurns;
        _tuning.TranscribingExposureDark    = d.TranscribingExposureDark;
        _tuning.TranscribingExposureLight   = d.TranscribingExposureLight;
        RebuildTuningPanel();
    }

    private void AddRewritingExpander()
    {
        var stack = NewExpander("Rewriting", ResetRewriting);
        AddFloatRow(stack, "RewritingSaturation", 0, 1, 0.05, _tuning.RewritingSaturation,
            v => _tuning.RewritingSaturation = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingHueShiftTurns", 0, 1, 0.05, _tuning.RewritingHueShiftTurns,
            v => _tuning.RewritingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingExposure", -2, 2, 0.1, _tuning.RewritingExposure,
            v => _tuning.RewritingExposure = (float)v, rebuild: true);
    }

    private void ResetRewriting()
    {
        var d = new TuningModel();
        _tuning.RewritingSaturation    = d.RewritingSaturation;
        _tuning.RewritingHueShiftTurns = d.RewritingHueShiftTurns;
        _tuning.RewritingExposure      = d.RewritingExposure;
        RebuildTuningPanel();
    }

    private void AddAudioMappingExpander()
    {
        var stack = NewExpander("Audio mapping (Recording)", ResetAudioMapping);
        // Static mutables on AudioLevelMapper (Deckle.Audio) — no
        // rebuild, read live each sample.
        AddFloatRow(stack, "EmaAlpha", 0, 1, 0.05, AudioLevelMapper.EmaAlpha,
            v => AudioLevelMapper.EmaAlpha = (float)v);
        AddFloatRow(stack, "MinDbfs", -80, 0, 1, AudioLevelMapper.MinDbfs,
            v => AudioLevelMapper.MinDbfs = (float)v);
        AddFloatRow(stack, "MaxDbfs", -60, 0, 1, AudioLevelMapper.MaxDbfs,
            v => AudioLevelMapper.MaxDbfs = (float)v);
        AddFloatRow(stack, "DbfsCurveExponent", 0.5, 4, 0.05, AudioLevelMapper.DbfsCurveExponent,
            v => AudioLevelMapper.DbfsCurveExponent = (float)v);
    }

    private void ResetAudioMapping()
    {
        AudioLevelMapper.EmaAlpha          = 0.25f;
        AudioLevelMapper.MinDbfs           = -55f;
        AudioLevelMapper.MaxDbfs           = -32f;
        AudioLevelMapper.DbfsCurveExponent = 1.0f;
        RebuildTuningPanel();
    }

    private void AddSimulatedRmsExpander()
    {
        var stack = NewExpander("Simulated RMS (Recording only)", ResetSimulatedRms);
        // Step 0.001 (not 0.05/0.01) — the RMS range is 0..0.3 and the
        // meaningful defaults (0.013 = engine gate at -38 dBFS, 0.100 =
        // conversational mid -20 dBFS) need 3 decimals to display
        // cleanly.
        AddFloatRow(stack, "SimRmsMin", 0, 0.3, 0.001, _simRmsMin,
            v => _simRmsMin = (float)v);
        AddFloatRow(stack, "SimRmsMax", 0, 0.3, 0.001, _simRmsMax,
            v => _simRmsMax = (float)v);
        AddFloatRow(stack, "SimRmsPeriodSeconds", 0.2, 10, 0.1, _simRmsPeriodSeconds,
            v => _simRmsPeriodSeconds = (float)v);
        AddToggleRow(stack, "Manual override", _simManualOverride,
            v => _simManualOverride = v);
        AddFloatRow(stack, "SimRmsManualValue", 0, 0.3, 0.001, _simManualValue,
            v => _simManualValue = (float)v);
    }

    private void ResetSimulatedRms()
    {
        _simRmsMin           = 0.013f;
        _simRmsMax           = 0.100f;
        _simRmsPeriodSeconds = 2.0f;
        _simManualOverride   = false;
        _simManualValue      = 0.012f;
        RebuildTuningPanel();
    }

    // Parked expander — fields only observable during a variant
    // transition or at stroke creation. Muted by construction in the
    // single-target playground ; collapsed by default so they don't
    // drown the active knobs, but kept visible for default-tweaking.
    private void AddParkedExpander()
    {
        var stack = NewExpander("Variant transitions — parked", ResetParked, expanded: false);
        AddFloatRow(stack, "HuePhaseTurns", 0, 1, 0.05, _tuning.HuePhaseTurns,
            v => _tuning.HuePhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcPhaseTurns", 0, 1, 0.05, _tuning.ArcPhaseTurns,
            v => _tuning.ArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingArcPhaseTurns", 0, 1, 0.05, _tuning.RecordingArcPhaseTurns,
            v => _tuning.RecordingArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "HueMinSpeedFraction", 0, 1, 0.05, _tuning.HueMinSpeedFraction,
            v => _tuning.HueMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcMinSpeedFraction", 0, 1, 0.05, _tuning.ArcMinSpeedFraction,
            v => _tuning.ArcMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingBlendSeconds", 0, 5, 0.1, _tuning.RecordingBlendSeconds,
            v => _tuning.RecordingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RecordingHuePeriodSeconds", 0, 60, 0.5, _tuning.RecordingHuePeriodSeconds,
            v => _tuning.RecordingHuePeriodSeconds = v, rebuild: true);
        AddFloatRow(stack, "TranscribingOpacity", 0, 1, 0.05, _tuning.TranscribingOpacity,
            v => _tuning.TranscribingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingBlendSeconds", 0, 5, 0.1, _tuning.TranscribingBlendSeconds,
            v => _tuning.TranscribingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RewritingOpacity", 0, 1, 0.05, _tuning.RewritingOpacity,
            v => _tuning.RewritingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingBlendSeconds", 0, 5, 0.1, _tuning.RewritingBlendSeconds,
            v => _tuning.RewritingBlendSeconds = v, rebuild: true);
    }

    private void ResetParked()
    {
        var d = new TuningModel();
        _tuning.HuePhaseTurns             = d.HuePhaseTurns;
        _tuning.ArcPhaseTurns             = d.ArcPhaseTurns;
        _tuning.RecordingArcPhaseTurns    = d.RecordingArcPhaseTurns;
        _tuning.HueMinSpeedFraction       = d.HueMinSpeedFraction;
        _tuning.ArcMinSpeedFraction       = d.ArcMinSpeedFraction;
        _tuning.RecordingBlendSeconds     = d.RecordingBlendSeconds;
        _tuning.RecordingHuePeriodSeconds = d.RecordingHuePeriodSeconds;
        _tuning.TranscribingOpacity       = d.TranscribingOpacity;
        _tuning.TranscribingBlendSeconds  = d.TranscribingBlendSeconds;
        _tuning.RewritingOpacity          = d.RewritingOpacity;
        _tuning.RewritingBlendSeconds     = d.RewritingBlendSeconds;
        RebuildTuningPanel();
    }
}
