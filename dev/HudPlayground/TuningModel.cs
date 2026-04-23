// `using static` gives us unqualified access to HudComposition's nested
// `ConicArcStrokeConfig` struct. A plain `using WhispUI.Composition;`
// would only import the outer HudComposition class, not its nested
// types — C# nested-type resolution stops at the containing class.
using static WhispUI.Composition.HudComposition;

namespace HudPlayground;

// Mutable shadow of HudComposition.ConicArcStrokeConfig.
//
// The shipping config is a readonly struct with init-only properties,
// designed so the shipping app can't accidentally mutate a stroke's
// baked geometry after creation. The playground needs mutation on
// slider.ValueChanged, so we hold a mutable class here and project it
// into a fresh ConicArcStrokeConfig on every rebuild.
//
// Field defaults mirror the shipping struct defaults one-to-one — if
// Louis diverges the shipping defaults, this mirror must follow (or
// the out-of-box playground would render with different numbers than
// WhispUI.exe, which would defeat the point).
//
// Layout grouping matches the expander grouping in MainWindow.xaml.cs
// so the reader can scan both files side by side.
internal sealed class TuningModel
{
    // ── Colour palette (paint-time, OKLCh) ──────────────────────────────
    public float OklchLightness = 0.75f;
    public float OklchChroma    = 0.15f;
    public float HueStart       = 0f;
    public float HueRange       = 1f;
    public int   WedgeCount     = 360;

    // ── Hue rotation ────────────────────────────────────────────────────
    public double HuePeriodSeconds = 8.0;
    public float  HueDirection     = 1f;
    public float  HuePhaseTurns    = 0f;
    // Out-in (0.125, 0.375, 0.875, 0.625) — endpoint tangent slope 3.0
    // on both sides → C¹-continuous seam when the keyframe loops. See
    // HudComposition.StartRotation header for details.
    public float  HueEaseP1X       = 0.125f;
    public float  HueEaseP1Y       = 0.375f;
    public float  HueEaseP2X       = 0.875f;
    public float  HueEaseP2Y       = 0.625f;
    public float  HueMinSpeedFraction = 0f;

    // ── Arc mask shape ──────────────────────────────────────────────────
    public float ConicSpanTurns     = 0.4f;
    public float ConicLeadFadeTurns = 1f;
    public float ConicTailFadeTurns = 1f;
    public float ConicFadeCurve     = 4f;
    public bool  ArcMirror          = true;

    // ── Arc rotation ────────────────────────────────────────────────────
    public double ArcPeriodSeconds = 8.0;
    public float  ArcDirection     = 1f;
    public float  ArcPhaseTurns    = 0f;
    public float  ArcEaseP1X       = 0.125f;
    public float  ArcEaseP1Y       = 0.375f;
    public float  ArcEaseP2X       = 0.875f;
    public float  ArcEaseP2Y       = 0.625f;
    public float  ArcMinSpeedFraction = 0f;

    // ── Rewriting variant ───────────────────────────────────────────────
    public float  RewritingSaturation    = 1f;
    public float  RewritingHueShiftTurns = 0f;
    public float  RewritingExposure      = 0f;
    public float  RewritingOpacity       = 1f;
    public double RewritingBlendSeconds  = 1;

    // ── Transcribing variant ────────────────────────────────────────────
    public float  TranscribingSaturationDark  = 0f;
    public float  TranscribingSaturationLight = 0f;
    public float  TranscribingHueShiftTurns   = 0f;
    public float  TranscribingExposureDark    = 0f;
    public float  TranscribingExposureLight   = 0f;
    public float  TranscribingOpacity         = 1f;
    public double TranscribingBlendSeconds    = 1;

    // ── Recording variant — paint-time ──────────────────────────────────
    public float RecordingConicSpanTurns     = 0.5f;
    public float RecordingConicLeadFadeTurns = 1f;
    public float RecordingConicTailFadeTurns = 1f;
    public float RecordingConicFadeCurve     = 4f;
    public bool  RecordingArcMirror          = true;
    public float RecordingArcPhaseTurns      = 0f;

    // ── Recording variant — runtime ─────────────────────────────────────
    public float  RecordingSaturationDark  = 0f;
    public float  RecordingSaturationLight = 0f;
    public float  RecordingHueShiftTurns   = 0f;
    public float  RecordingExposureDark    = 1.5f;
    public float  RecordingExposureLight   = -1.5f;
    public double RecordingBlendSeconds    = 1;
    public double RecordingHuePeriodSeconds = 0;

    // Projects the current field values into a fresh ConicArcStrokeConfig
    // so HudChrono.RebuildStroke can bake a new stroke. Called on every
    // slider change that targets a stroke field. The projection is
    // explicit field-by-field — no reflection, no codegen — so compile
    // errors flag any drift between this file and the shipping struct.
    public ConicArcStrokeConfig ToConfig() => new()
    {
        OklchLightness    = OklchLightness,
        OklchChroma       = OklchChroma,
        HueStart          = HueStart,
        HueRange          = HueRange,
        WedgeCount        = WedgeCount,

        HuePeriodSeconds  = HuePeriodSeconds,
        HueDirection      = HueDirection,
        HuePhaseTurns     = HuePhaseTurns,
        HueEaseP1X        = HueEaseP1X,
        HueEaseP1Y        = HueEaseP1Y,
        HueEaseP2X        = HueEaseP2X,
        HueEaseP2Y        = HueEaseP2Y,
        HueMinSpeedFraction = HueMinSpeedFraction,

        ConicSpanTurns     = ConicSpanTurns,
        ConicLeadFadeTurns = ConicLeadFadeTurns,
        ConicTailFadeTurns = ConicTailFadeTurns,
        ConicFadeCurve     = ConicFadeCurve,
        ArcMirror          = ArcMirror,

        ArcPeriodSeconds  = ArcPeriodSeconds,
        ArcDirection      = ArcDirection,
        ArcPhaseTurns     = ArcPhaseTurns,
        ArcEaseP1X        = ArcEaseP1X,
        ArcEaseP1Y        = ArcEaseP1Y,
        ArcEaseP2X        = ArcEaseP2X,
        ArcEaseP2Y        = ArcEaseP2Y,
        ArcMinSpeedFraction = ArcMinSpeedFraction,

        RewritingSaturation    = RewritingSaturation,
        RewritingHueShiftTurns = RewritingHueShiftTurns,
        RewritingExposure      = RewritingExposure,
        RewritingOpacity       = RewritingOpacity,
        RewritingBlendSeconds  = RewritingBlendSeconds,

        TranscribingSaturationDark  = TranscribingSaturationDark,
        TranscribingSaturationLight = TranscribingSaturationLight,
        TranscribingHueShiftTurns   = TranscribingHueShiftTurns,
        TranscribingExposureDark    = TranscribingExposureDark,
        TranscribingExposureLight   = TranscribingExposureLight,
        TranscribingOpacity         = TranscribingOpacity,
        TranscribingBlendSeconds    = TranscribingBlendSeconds,

        RecordingConicSpanTurns     = RecordingConicSpanTurns,
        RecordingConicLeadFadeTurns = RecordingConicLeadFadeTurns,
        RecordingConicTailFadeTurns = RecordingConicTailFadeTurns,
        RecordingConicFadeCurve     = RecordingConicFadeCurve,
        RecordingArcMirror          = RecordingArcMirror,
        RecordingArcPhaseTurns      = RecordingArcPhaseTurns,

        RecordingSaturationDark  = RecordingSaturationDark,
        RecordingSaturationLight = RecordingSaturationLight,
        RecordingHueShiftTurns   = RecordingHueShiftTurns,
        RecordingExposureDark    = RecordingExposureDark,
        RecordingExposureLight   = RecordingExposureLight,
        RecordingBlendSeconds    = RecordingBlendSeconds,
        RecordingHuePeriodSeconds = RecordingHuePeriodSeconds,
    };
}
