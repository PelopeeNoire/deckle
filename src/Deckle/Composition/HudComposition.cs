using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deckle.Composition;

// Variant the processing stroke is rendering. HudChrono picks one based
// on the HUD state (Recording / Transcribing / Rewriting); the live stroke
// animates its own effect properties toward the matching variant values
// without rebuilding anything — except on Recording ↔ (Transcribing /
// Rewriting) crossings where the rotation-frozen vs spinning pipelines
// cannot share a SpriteVisual, and HudChrono tears the stroke down and
// rebuilds a fresh one (see AttachProcessingVisual).
internal enum ProcessingVariant { Recording, Transcribing, Rewriting }

// HUD Composition pipeline — processing strokes for the chrono surface.
//
// Strictly internal: every visual produced here stays inside the 272x78 HUD
// rect. The HUD window runs with WS_EX_LAYERED for proximity fade (which
// disables the DWM shell shadow) and the rect is a tight fit around the
// card — any external DropShadow would clip at the HWND edge, producing a
// rectangular artifact. No shadows in this pipeline.
//
// Geometry is flush with the card edge (InsetDip = 0). CornerRadius 7 dip
// sits just inside the DWM 8-dip rounded silhouette, so the Win2D-rasterised
// stroke clears the DWM corner clip even though the two AA pipelines don't
// agree at high-curvature arcs.
//
// Pixel-perfect sizing note (CreateConicArcStroke): the stroke silhouette
// surface is dimensioned with Math.Round of the visual DIP extent, NOT
// Math.Ceiling. At non-integer DPI (e.g. 125 % gives hostSize.Y = 78.4),
// ceiling would oversize the surface by up to 1 pixel (pxH = 79 for a
// 78.4-dip visual). CompositionSurfaceBrush.Stretch = Fill then compresses
// 79 source rows into 78.4 dip — scale 0.9924 — so the stroke's outer edge
// drawn at source y = pxH lands at visual y = 77.41 instead of 78.4. That
// 1-dip gap is the stroke "disappearing" asymmetrically on the bottom/right
// edges (top/left are pinned at y=0/x=0 by the origin, so they stay flush).
// Math.Round gets the surface size within ±0.5 dip of innerSize on every
// side, and Stretch.Fill then stretches (scale ≥ ~1) to land the outer
// stroke edge flush with the visual extent on all four sides. pxSquare
// (rotation coverage) is computed from innerSize directly, not from the
// rounded pxW/pxH, so it always clears the visual diagonal.
internal static class HudComposition
{
    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Shared geometry                                                   ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // Fixed across all three variants — stroke metrics are a property of
    // the HUD rect, not of the animation.
    private const float  StrokeThickness              = 4f;    // dip, stroke width
    // `public static` (not const) — HudPlayground tunes the inset live to
    // explore stroke geometry without rebuilding the app. Shipping code
    // still reads it as if it were a const: the field reads inline cleanly
    // when nothing mutates it in a given process. Mutating live requires
    // rebuilding the stroke (paint-time geometry); the playground triggers
    // that via its existing rebuild path.
    public  static       float InsetDip                = -2f;  // dip, inset from HUD edge
    private const float  CornerRadiusDip              = 7f;    // dip, rounded-rect corner radius

    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Processing stroke — single visual, live-modulated variants        ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // One stroke is created on HUD state entry and kept alive across
    // Transcribing ↔ Rewriting. Per-state differentiation runs LIVE on
    // the same visual via Composition effects (SaturationEffect,
    // HueRotationEffect, ExposureEffect on the colour pipeline + Opacity
    // on the visual). ProcessingStroke.ApplyVariant blends toward the
    // target values over BlendSeconds — no surface rebuild, no GC, no lag.
    //
    // Recording uses the same stroke type but with a frozen-rotation
    // pipeline and paint-time geometry overrides (arc lobes parked at
    // visual 12/6 o'clock). Because paint-time knobs differ from the
    // rotating variant, crossing the Recording ↔ (Transcribing /
    // Rewriting) boundary requires a rebuild — HudChrono tears down and
    // recreates the stroke at that boundary. Transcribing ↔ Rewriting
    // stays live-blended on the same visual.
    //
    // All knobs live on ConicArcStrokeConfig below, split into blocks:
    //   1. "Baseline palette / rotations" — paint-time config, applies to
    //      all variants unless overridden (OklchLightness, HueRange,
    //      ConicSpan, ArcPeriod…).
    //   2. Runtime variants (Rewriting* / Transcribing* / Recording*) —
    //      the live knobs animated by ApplyVariant. Edit these to shape
    //      each state's look.
    //   3. Recording paint-time overrides (RecordingConic* /
    //      RecordingArcPhaseTurns / RecordingArcMirror) — consumed by
    //      CreateRecordingStroke to carve the frozen-rotation silhouette.
    //   4. BlendSeconds — per-variant transition duration.

    // ── Lexicon — vocabulary shared across the struct fields below ──────
    //
    // Paint-time (OKLCh conic palette baked once into a surface).
    // OKLCh is a perceptually uniform cylindrical colour space: at
    // constant L and C, all hues have the same perceived lightness
    // and saturation. HSV — which this used to use — does not: a
    // full-saturation HSV rainbow reads with yellow much brighter
    // than blue, which was visible as a top/bottom luminance
    // asymmetry on the conic wheel. OKLCh removes that asymmetry.
    //   OklchLightness 0 = black, 1 = white. 0.75 is bright-but-not-
    //                  blinding, comparable to a vivid mid-tone.
    //   OklchChroma    saturation in OKLab space. 0 = greyscale,
    //                  ~0.15 = vivid pastel, ~0.22 = near-maximum
    //                  in-gamut for most hues at L=0.75 (yellows and
    //                  blues start to clip above ~0.18). Gamut-clipped
    //                  values are clamped to [0, 1] sRGB at the end
    //                  — clipping reads as a gentle flattening of
    //                  those hues rather than a hard stop.
    //   HueStart       rotates hue 0 on the wheel (0 = red at 3 o'clock).
    //   HueRange       wheel slice. 1 = full rainbow, 0.5 = half, 0 = mono.
    //   WedgeCount     pie wedges. 360 = smooth ring, 12/24 = retro steps.
    //
    // Arc mask shape (white pie slice composited with the conic via
    // AlphaMaskEffect, with alpha ramps at both ends):
    //   Span           arc length in turns. 0.5 mirrored = half-circle
    //                  each; smaller = more "off" space between.
    //   LeadFade/Tail  fade extents in turns at each end (head fade-in /
    //                  tail fade-out). If Lead+Tail > Span they scale to
    //                  meet at the arc mid (pure bell, no flat core).
    //   FadeCurve      pow(t, curve) shape. 1 = linear ribbon,
    //                  2 = quadratic soft fade, 3+ = crisp comet bell,
    //                  <1 = near hard-edged solid.
    //   Mirror         paint a second arc at +π for a symmetric double
    //                  comet (Span clamps to 0.5 in mirror mode).
    //
    // Rotation (applied independently to the conic and the arc mask —
    // rational period ratios like 2:1 or 3:2 close cleanly every LCM):
    //   PeriodSeconds  seconds per full turn. Lower = faster.
    //   Direction      +1 CW, -1 CCW.
    //   PhaseTurns     start offset in turns (0..1).
    //   EaseP1/P2        cubic-bezier control points. (0,0,1,1) = linear,
    //                    (0.42,0,0.58,1) = standard ease-in-out, sharper
    //                    curves give bigger speed contrast.
    //   MinSpeedFraction fraction of the mean angular velocity
    //                    (ω_mean = 2π/period) guaranteed at every instant
    //                    of the cycle. 0 = pure easing (may visibly freeze
    //                    on the bezier plateaus). 0.3 = never below 30% of
    //                    mean. 1 = strictly constant rotation at the mean
    //                    (no pulsation). Raising the floor compresses the
    //                    peak in the same stroke — the eye reads it as
    //                    "calmer, more continuous", not "faster". See
    //                    StartRotation header for the closed-form
    //                    ω_min / ω_max expressions.
    //
    // Runtime variant knobs (live properties on the SINGLE kept-alive
    // stroke — SaturationEffect, HueRotationEffect, ExposureEffect on the
    // colour pipeline, plus SpriteVisual.Opacity — animated by
    // ApplyVariant. Switching variants is a property animation on the
    // same GPU resources — no surface rebuild, no GC, no lag):
    //   Saturation     multiplier on the baked conic. 0 = greyscale,
    //                  1 = baseline colour. Combines with OklchChroma.
    //   HueShiftTurns  runtime rotation of the colour wheel.
    //                  0 = no shift, 0.5 = red↔cyan swap, 1 = no change.
    //                  Negatives shift the other way.
    //   Exposure       EV stops. 0 = no change, +1 ≈ 2× brighter,
    //                  -1 ≈ half. Typical range [-2, +2]. Split
    //                  Dark / Light for Transcribing so the greyscale
    //                  stays readable against both substrates.
    //   Opacity        SpriteVisual.Opacity in [0..1]. Dims the whole
    //                  stroke including the silhouette; 0.6-0.8 reads
    //                  as a subtle calm variant.
    //   BlendSeconds   duration of the blend from the previous variant.
    //                  0.2-0.4 = snappy, 0.6-1.0 = breathing. Per-variant
    //                  so entering Transcribing can be slower than the
    //                  return to Rewriting.
    //
    // No base stroke layer — the permanent HUD outline is the DWM frame
    // (DWMWA_BORDER_COLOR = DWMWA_COLOR_DEFAULT in HudWindow), 1-dip and
    // theme/accent-aware. Rotating arcs composite on top; transparent
    // regions between arcs expose the DWM stroke.

    // Config for CreateConicArcStroke. Init-only fields with defaults —
    // each wrapper overrides only what it needs. The explicit
    // parameterless constructor is required by C# for struct field
    // initialisers to run on `new ConicArcStrokeConfig { ... }`.
    //
    // `internal` (not `private`) so the internal ProcessingStroke ctor
    // can reference it without CS0051. Still effectively HudComposition-
    // scoped — nothing outside this file constructs one.
    //
    // See the Lexicon above for what each field means; only per-field
    // deviations from the generic definition are repeated here.
    internal readonly struct ConicArcStrokeConfig
    {
        public ConicArcStrokeConfig() {}

        // ── Colour palette (paint-time, baked once) ──────────────────────
        // OKLCh replaces HSV so the baked conic wheel has perceptually
        // uniform luminance across hues — critical for the
        // Saturation=0 greyscale variants, which otherwise inherit
        // HSV's top/bottom brightness asymmetry as a grey gradient.
        public float  OklchLightness     { get; init; } = 0.75f;
        public float  OklchChroma        { get; init; } = 0.3f;
        public float  HueStart           { get; init; } = 0f;
        public float  HueRange           { get; init; } = 1f;
        public int    WedgeCount         { get; init; } = 360;

        // ── Hue rotation — spins the conic under the fixed arc mask,
        //    so the colour at the arc head walks the wheel over time ─────
        public double HuePeriodSeconds   { get; init; } = 14.0;
        public float  HueDirection       { get; init; } = 1f;
        public float  HuePhaseTurns      { get; init; } = 0f;
        // Out-in shape at (0.125, 0.375) / (0.875, 0.625) — tangent at
        // the endpoints has slope 0.375 / 0.125 = 3.0 at both t=0 and
        // t=1. Same slope on both sides means the loop is C¹ across the
        // cycle seam: no "freeze at the plateau" reading between
        // iterations. The midsection dips below the mean (slow-down ≈
        // 0.5× mean around t=0.5) and the endpoints push above (pulse
        // ≈ 3× mean), but continuously — no pause on any frame.
        // Replaces the classic in-out (0.2, 0, 0.8, 1) whose zero-slope
        // endpoints forced MinSpeedFraction as a workaround.
        public float  HueEaseP1X         { get; init; } = 0.125f;
        public float  HueEaseP1Y         { get; init; } = 0.375f;
        public float  HueEaseP2X         { get; init; } = 0.875f;
        public float  HueEaseP2Y         { get; init; } = 0.625f;
        // Vestigial — the out-in curve above no longer plateaus at cycle
        // boundaries, so the linear blend is redundant. Kept at 0 so the
        // playground can still experiment with exotic ease curves that
        // DO need a floor, but untouched in the shipping path.
        public float  HueMinSpeedFraction { get; init; } = 0f;

        // ── Arc mask shape (white pie slice, alpha-ramped at both ends) ─
        public float  ConicSpanTurns     { get; init; } = 0.5f;
        public float  ConicLeadFadeTurns { get; init; } = 1f;
        public float  ConicTailFadeTurns { get; init; } = 1f;
        public float  ConicFadeCurve     { get; init; } = 4f;
        public bool   ArcMirror          { get; init; } = true;

        // ── Arc rotation — rotates the arc mask independently of the
        //    hue rotation. This is what the eye reads as "the speed of
        //    the loading animation" ─────────────────────────────────────
        public double ArcPeriodSeconds   { get; init; } = 8.0;
        public float  ArcDirection       { get; init; } = 1f;
        public float  ArcPhaseTurns      { get; init; } = 0f;
        // Same out-in shape as HueEase for the same reason — seam
        // continuity across the cycle loop without a plateau floor.
        public float  ArcEaseP1X         { get; init; } = 0.125f;
        public float  ArcEaseP1Y         { get; init; } = 0.375f;
        public float  ArcEaseP2X         { get; init; } = 0.875f;
        public float  ArcEaseP2Y         { get; init; } = 0.625f;
        public float  ArcMinSpeedFraction { get; init; } = 0f;

        // ── Rewriting variant — target values for the live effect
        //    pipeline. Baseline neutrals leave the baked palette alone ──
        public float  RewritingSaturation       { get; init; } = 1f;
        public float  RewritingHueShiftTurns    { get; init; } = 0f;
        public float  RewritingExposure         { get; init; } = 0f;
        public float  RewritingOpacity          { get; init; } = 1f;
        public double RewritingBlendSeconds     { get; init; } = 2;

        // ── Transcribing variant — greyscale (Saturation 0) by default.
        //    Saturation + Exposure are split Dark/Light because even with
        //    OKLCh's uniform-luminance baseline, the greyscale target
        //    still depends on the substrate (light on dark / dark on
        //    light) — exposure biases the baked L=0.75 neutral up or
        //    down to read against each theme. HueShift/Opacity stay
        //    unified — widen later if per-theme control is needed ───────
        public float  TranscribingSaturationDark  { get; init; } = 0f;
        public float  TranscribingSaturationLight { get; init; } = 0f;
        public float  TranscribingHueShiftTurns   { get; init; } = 0f;
        public float  TranscribingExposureDark    { get; init; } = 0.7f;
        public float  TranscribingExposureLight   { get; init; } = -1.2f;
        public float  TranscribingOpacity         { get; init; } = 1f;
        public double TranscribingBlendSeconds    { get; init; } = 2;

        // ── Recording variant — frozen-rotation stroke with RMS-driven
        //    opacity. Two blocks:
        //
        //    PAINT-TIME (baked into Win2D conic/arc surfaces when
        //    CreateRecordingStroke runs — cannot be animated live; edit
        //    the defaults and rebuild to iterate):
        //      - ConicSpan / LeadFade / TailFade / FadeCurve / ArcMirror
        //        shape the silhouette's arc geometry. Span 0.5 + Mirror
        //        covers the full perimeter as two 180° lobes. Fades
        //        auto-scale to bell if Lead+Tail > Span.
        //      - ArcPhaseTurns rotates the arc mask at paint-time to
        //        park the lobes at visual 12/6 o'clock (see
        //        CreateRecordingStroke header for the phase math).
        //
        //    RUNTIME (consumed by ApplyVariant — animated via the same
        //    live effect pipeline as Transcribing/Rewriting, so a theme
        //    change mid-recording blends smoothly):
        //      - Saturation Dark/Light, HueShift, Exposure Dark/Light,
        //        BlendSeconds.
        //      - Defaults mirror Transcribing so the out-of-box greyscale
        //        stays theme-consistent; tune independently if Recording
        //        needs a distinct palette.
        //
        //    No RecordingOpacity — UpdateLevel owns that channel from
        //    the mic RMS stream. ApplyVariant(Recording) deliberately
        //    skips the Opacity animation to avoid fighting UpdateLevel.
        public float  RecordingConicSpanTurns      { get; init; } = 0.5f;
        public float  RecordingConicLeadFadeTurns  { get; init; } = 1f;
        public float  RecordingConicTailFadeTurns  { get; init; } = 1f;
        public float  RecordingConicFadeCurve      { get; init; } = 2f;
        public bool   RecordingArcMirror           { get; init; } = true;
        public float  RecordingArcPhaseTurns       { get; init; } = 0f;
        public float  RecordingSaturationDark      { get; init; } = 0f;
        public float  RecordingSaturationLight     { get; init; } = 0f;
        public float  RecordingHueShiftTurns       { get; init; } = 0f;
        public float  RecordingExposureDark        { get; init; } = 0.7f;
        public float  RecordingExposureLight       { get; init; } = -1.2f;
        public double RecordingBlendSeconds        { get; init; } = 2;

        // Recording hue rotation — independent from arc rotation (which is
        // always frozen in Recording). 0 = hue frozen on HuePhaseTurns
        // (uniform grey at RecordingSaturation = 0, static tint at > 0).
        // > 0 = slow hue drift across the silhouette; pair with
        // RecordingSaturation Dark/Light > 0 for the chromatic effect to be
        // visible — at Saturation = 0 strict, RGB = (V, V, V) irrespective
        // of hue, so the hue rotates mathematically but reads identical.
        // Typical drift period for a calm "chatoiement" effect: 20–30 s.
        public double RecordingHuePeriodSeconds    { get; init; } = 0;
    }

    // Live handle to a processing stroke created by CreateProcessingStroke.
    //
    // Wraps the ContainerVisual (attach point for XAML) and the animable
    // CompositionPropertySet that drives the SaturationEffect /
    // HueRotationEffect / ExposureEffect properties on the live pipeline.
    // ApplyVariant blends the PropertySet scalars + SpriteVisual.Opacity
    // from their current values to the variant targets over BlendSeconds
    // — no surface rebuild, no lag.
    // Tracks stroke creations / disposals for debugging the "animation
    // freezes after N rebuilds" class of bugs. Each stroke gets a unique
    // CreationId; when _liveStrokeCount grows unbounded (dispose missing)
    // the compositor saturates and Forever animations go silent.
    // Subscribers (like HudPlayground) read these via StrokeLifecycle
    // events below.
    private static int _creationCounter;
    private static int _liveStrokeCount;
    internal static int TotalStrokesCreated => _creationCounter;
    internal static int LiveStrokeCount     => _liveStrokeCount;
    internal static event Action<int, string>? StrokeLifecycle; // (creationId, event)

    // ── DeviceLost hook ──────────────────────────────────────────────────
    // Win2D's CanvasDevice.GetSharedDevice() returns a process-wide D3D11
    // device. If the GPU goes away (driver reset, TDR, Vulkan/D3D contention
    // with the whisper.cpp Vulkan backend running on the same GPU),
    // every CompositionDrawingSurface we baked onto that device becomes
    // invalid — conic surface, arc mask surface, stroke silhouette surface.
    // The compositor keeps rendering but the brushes sample black, which
    // reads as "the rotation froze" even though the expression animation is
    // still ticking underneath. We can't cure the device loss from here
    // (recovery = recreate CanvasDevice + repaint surfaces in the right
    // thread), but we can *observe* it — if DeviceLost fires right when
    // a freeze is observed, the Composition leak (fixed via Dispose) is
    // not the whole story and we need a device-recovery path.
    //
    // The handler runs on whatever thread Win2D raises the event on —
    // subscribers that touch UI must marshal themselves.
    internal static event Action<string>? CanvasDeviceLost;
    private static bool _deviceLostHooked;
    private static readonly object _deviceLostLock = new();

    // Called lazily by CreateConicArcStroke the first time it grabs the
    // shared CanvasDevice, so we attach exactly once per process lifetime.
    // Lock + flag guard against the rare case where two strokes are
    // created concurrently on the UI thread (should not happen, but the
    // cost of double-hooking would be two event fires per loss — cheap
    // to prevent).
    private static void EnsureDeviceLostHook(CanvasDevice device)
    {
        if (_deviceLostHooked) return;
        lock (_deviceLostLock)
        {
            if (_deviceLostHooked) return;
            device.DeviceLost += OnCanvasDeviceLost;
            _deviceLostHooked = true;
        }
    }

    private static void OnCanvasDeviceLost(CanvasDevice sender, object args)
    {
        // Re-raise for any subscriber (HudPlayground instrumentation,
        // future device-recovery code). String arg is a human-readable
        // reason — Win2D's event args are empty, so we synthesise one.
        CanvasDeviceLost?.Invoke($"CanvasDevice.DeviceLost fired (live strokes = {_liveStrokeCount})");
    }

    internal sealed class ProcessingStroke : IDisposable
    {
        public ContainerVisual Visual { get; }
        public int CreationId { get; }

        private readonly Compositor _compositor;
        private readonly CompositionPropertySet _effectProps;
        private readonly SpriteVisual _strokeVisual;
        private readonly ConicArcStrokeConfig _config;

        // Composition graph refs — kept so Dispose() can stop animations
        // and release native handles explicitly. Without these, disposing
        // only the container leaves the brushes, surfaces, propertysets
        // and their Forever animations live on the compositor — they
        // accumulate across rebuilds until the compositor saturates.
        private readonly CompositionSurfaceBrush _conicBrush;
        private readonly CompositionSurfaceBrush _arcMaskBrush;
        private readonly CompositionSurfaceBrush _strokeMaskBrush;
        private readonly CompositionEffectBrush  _effectBrush;
        private readonly CompositionDrawingSurface _conicSurface;
        private readonly CompositionDrawingSurface _arcMaskSurface;
        private readonly CompositionDrawingSurface _strokeMaskSurface;
        // Null when the corresponding rotation is frozen (static matrix
        // path) — only the animated paths allocate a PropertySet.
        private readonly CompositionPropertySet? _hueRotationProps;
        private readonly CompositionPropertySet? _arcRotationProps;

        private bool _disposed;

        // `internal` — called from HudComposition.CreateConicArcStroke.
        // C# does not grant the enclosing class access to private members
        // of its nested types (asymmetric: nested → enclosing only), so a
        // `private` ctor here would be uncallable from CreateConicArcStroke.
        // `internal` + `internal ConicArcStrokeConfig` keeps CS0051 happy
        // while not widening the effective API — the struct is still nested.
        internal ProcessingStroke(
            ContainerVisual visual,
            Compositor compositor,
            CompositionPropertySet effectProps,
            SpriteVisual strokeVisual,
            ConicArcStrokeConfig config,
            CompositionSurfaceBrush conicBrush,
            CompositionSurfaceBrush arcMaskBrush,
            CompositionSurfaceBrush strokeMaskBrush,
            CompositionEffectBrush  effectBrush,
            CompositionDrawingSurface conicSurface,
            CompositionDrawingSurface arcMaskSurface,
            CompositionDrawingSurface strokeMaskSurface,
            CompositionPropertySet? hueRotationProps,
            CompositionPropertySet? arcRotationProps)
        {
            Visual       = visual;
            _compositor  = compositor;
            _effectProps = effectProps;
            _strokeVisual = strokeVisual;
            _config      = config;
            _conicBrush         = conicBrush;
            _arcMaskBrush       = arcMaskBrush;
            _strokeMaskBrush    = strokeMaskBrush;
            _effectBrush        = effectBrush;
            _conicSurface       = conicSurface;
            _arcMaskSurface     = arcMaskSurface;
            _strokeMaskSurface  = strokeMaskSurface;
            _hueRotationProps   = hueRotationProps;
            _arcRotationProps   = arcRotationProps;

            CreationId = System.Threading.Interlocked.Increment(ref _creationCounter);
            System.Threading.Interlocked.Increment(ref _liveStrokeCount);
            StrokeLifecycle?.Invoke(CreationId, "created");
        }

        // Blend the live effect properties toward the variant's target
        // values. Called on every HUD state entry into Transcribing or
        // Rewriting, and on live theme change while Transcribing (Exposure
        // is theme-aware). Safe to call repeatedly — each call overrides
        // any in-flight animation and starts fresh from the current value
        // via InsertExpressionKeyFrame("this.CurrentValue").
        public void ApplyVariant(ProcessingVariant variant, bool isDark)
        {
            float sat, hueShiftTurns, exposure, opacity;
            double blendSeconds;

            switch (variant)
            {
                case ProcessingVariant.Recording:
                    // Recording has its own Saturation/Hue/Exposure slots
                    // in the config; defaults mirror Transcribing but
                    // tune independently. Opacity is NOT animated here —
                    // UpdateLevel drives it from EMA-smoothed mic RMS.
                    // See UpdateLevel below.
                    sat           = isDark
                        ? _config.RecordingSaturationDark
                        : _config.RecordingSaturationLight;
                    hueShiftTurns = _config.RecordingHueShiftTurns;
                    exposure      = isDark
                        ? _config.RecordingExposureDark
                        : _config.RecordingExposureLight;
                    opacity       = 0f;                 // unused, skipped below
                    blendSeconds  = _config.RecordingBlendSeconds;
                    break;
                case ProcessingVariant.Transcribing:
                    sat           = isDark
                        ? _config.TranscribingSaturationDark
                        : _config.TranscribingSaturationLight;
                    hueShiftTurns = _config.TranscribingHueShiftTurns;
                    exposure      = isDark
                        ? _config.TranscribingExposureDark
                        : _config.TranscribingExposureLight;
                    opacity       = _config.TranscribingOpacity;
                    blendSeconds  = _config.TranscribingBlendSeconds;
                    break;
                case ProcessingVariant.Rewriting:
                    sat           = _config.RewritingSaturation;
                    hueShiftTurns = _config.RewritingHueShiftTurns;
                    exposure      = _config.RewritingExposure;
                    opacity       = _config.RewritingOpacity;
                    blendSeconds  = _config.RewritingBlendSeconds;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }

            var duration = TimeSpan.FromSeconds(Math.Max(0.001, blendSeconds));
            float hueAngleRadians = hueShiftTurns * MathF.Tau;

            AnimateScalar(_effectProps,  "Saturation", sat,             duration);
            AnimateScalar(_effectProps,  "HueAngle",   hueAngleRadians, duration);
            AnimateScalar(_effectProps,  "Exposure",   exposure,        duration);

            // Recording's Opacity is RMS-driven via UpdateLevel — leave it
            // untouched so an ApplyVariant (e.g. live theme change mid-
            // recording) doesn't knock the outline back to silence.
            if (variant != ProcessingVariant.Recording)
                AnimateScalar(_strokeVisual, "Opacity", opacity, duration);
        }

        // Push a new target opacity in [0, 1]. Called from HudChrono's
        // UpdateAudioLevel on the recording audio thread — CompositionPropertySet
        // and StartAnimation are thread-safe per Composition's contract,
        // no DispatcherQueue marshalling.
        //
        // 50 ms linear key-frame from the current value to the target.
        // InsertExpressionKeyFrame("this.CurrentValue") makes successive
        // overlapping calls blend naturally from wherever the previous
        // animation had reached — no reset to 0, no step discontinuity.
        // The Composition renderthread (vsynced to the monitor refresh —
        // 60/120/144/240 Hz) interpolates between 20 Hz RMS samples at the
        // native rate with no C#-side tick.
        //
        // Only meaningful on a Recording-variant stroke; calling it on a
        // Transcribing / Rewriting stroke would fight ApplyVariant's opacity
        // animation, so HudChrono gates the call on _currentVariant.
        public void UpdateLevel(float level)
        {
            float clamped = Math.Clamp(level, 0f, 1f);
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.InsertExpressionKeyFrame(0f, "this.CurrentValue");
            anim.InsertKeyFrame(1f, clamped);
            anim.Duration = TimeSpan.FromMilliseconds(50);
            _strokeVisual.StartAnimation("Opacity", anim);
        }

        // "Start from the current value, reach target at the end of
        // duration." InsertExpressionKeyFrame("this.CurrentValue") reads
        // the live value at animation-start, so overlapping calls blend
        // naturally from wherever the previous animation had reached.
        private void AnimateScalar(
            CompositionObject target, string property, float value, TimeSpan duration)
        {
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.InsertExpressionKeyFrame(0f, "this.CurrentValue");
            anim.InsertKeyFrame(1f, value);
            anim.Duration = duration;
            target.StartAnimation(property, anim);
        }

        // Two-phase teardown so no dangling animation fires on a freed
        // resource. Ordering below is intentional:
        //   1. Stop animations at their source (brushes, propertysets,
        //      strokeVisual). StopAnimation is a no-op when no animation
        //      is attached to the property — safe to blanket-call.
        //   2. Dispose native resources in reverse creation order:
        //      effects → brushes → surfaces → propertysets → visuals.
        //      Each Dispose() releases the native handle; managed wrappers
        //      are then GC'd whenever. A missed Dispose here means the
        //      native handle lingers — the freeze-after-N-rebuilds symptom.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ── 1. Stop all animations ───────────────────────────────
            //
            // Two layers of animations hang on this stroke:
            //
            //   a) ScalarKeyFrameAnimations on the PropertySets and the
            //      strokeVisual — these are the "what's currently moving"
            //      side. Stopping them freezes the value.
            //
            //   b) ExpressionAnimations bound to `_effectBrush` via
            //      BindEffectProperty — three of them, one per effect slot
            //      (Sat.Saturation / Hue.Angle / Exp.Exposure). Each holds
            //      a *reference* to `_effectProps` via SetReferenceParameter.
            //
            // If we only stop (a) and dispose `_effectProps`, the three
            // ExpressionAnimations on effectBrush are still bound to a
            // freed PropertySet. Disposing effectBrush then releases the
            // expressions — BUT between _effectProps.Dispose() and
            // effectBrush.Dispose(), a render tick can evaluate the
            // expression, hit the freed PropertySet, and crash. The
            // Transcribing Exposure slider crash reproduces this exact
            // race: slider → rebuild → dispose-in-flight → render tick
            // reads a half-disposed graph.
            //
            // Fix: explicitly StopAnimation on the effectBrush's animated
            // property paths *before* disposing _effectProps. Each call
            // severs the expression binding on the native side.
            try { _effectBrush.StopAnimation("Sat.Saturation"); } catch { }
            try { _effectBrush.StopAnimation("Hue.Angle");      } catch { }
            try { _effectBrush.StopAnimation("Exp.Exposure");   } catch { }

            try { _conicBrush.StopAnimation("TransformMatrix");   } catch { }
            try { _arcMaskBrush.StopAnimation("TransformMatrix"); } catch { }
            try { _strokeVisual.StopAnimation("Opacity");         } catch { }

            try { _effectProps.StopAnimation("Saturation"); } catch { }
            try { _effectProps.StopAnimation("HueAngle");   } catch { }
            try { _effectProps.StopAnimation("Exposure");   } catch { }

            if (_hueRotationProps is not null)
            {
                try { _hueRotationProps.StopAnimation("Linear"); } catch { }
                try { _hueRotationProps.StopAnimation("Eased");  } catch { }
            }
            if (_arcRotationProps is not null)
            {
                try { _arcRotationProps.StopAnimation("Linear"); } catch { }
                try { _arcRotationProps.StopAnimation("Eased");  } catch { }
            }

            // ── 2. Dispose resources ─────────────────────────────────
            // Effect brush first — it holds the ExpressionAnimations that
            // reference _effectProps. Disposing the brush releases those
            // bindings on the native side. Only then is it safe to dispose
            // _effectProps.
            try { _effectBrush.Dispose();     } catch { }
            try { _conicBrush.Dispose();      } catch { }
            try { _arcMaskBrush.Dispose();    } catch { }
            try { _strokeMaskBrush.Dispose(); } catch { }

            try { _conicSurface.Dispose();      } catch { }
            try { _arcMaskSurface.Dispose();    } catch { }
            try { _strokeMaskSurface.Dispose(); } catch { }

            try { _hueRotationProps?.Dispose(); } catch { }
            try { _arcRotationProps?.Dispose(); } catch { }
            try { _effectProps.Dispose();       } catch { }

            try { _strokeVisual.Dispose(); } catch { }
            try { Visual.Dispose();        } catch { }

            System.Threading.Interlocked.Decrement(ref _liveStrokeCount);
            StrokeLifecycle?.Invoke(CreationId, "disposed");
        }
    }

    // Processing stroke — single rainbow double-comet shared by the
    // Transcribing and Rewriting states. The returned ProcessingStroke
    // exposes ApplyVariant(…) for the live state blend.
    //
    // Struct defaults on ConicArcStrokeConfig ARE the whole config.
    // Tweak the defaults to iterate on the visual.
    internal static ProcessingStroke CreateProcessingStroke(
        Compositor compositor, Vector2 hostSize,
        ConicArcStrokeConfig? configOverride = null)
    {
        // Optional override used by HudChrono.RebuildStroke (HudPlayground
        // slider wiring). Null keeps the shipping defaults.
        return CreateConicArcStroke(
            compositor, hostSize,
            configOverride ?? new ConicArcStrokeConfig());
    }

    // Recording stroke — the same double-comet pipeline as the processing
    // stroke, but with rotation frozen and the arc positioned at visual 12
    // and 6 o'clock. Opacity starts at 0 (invisible outline) and is driven
    // by ProcessingStroke.UpdateLevel from HudChrono's EMA-smoothed mic
    // RMS.
    //
    // All Recording-specific knobs live on the Recording* fields of
    // ConicArcStrokeConfig (see the struct section above). This factory
    // reads a default-constructed config and maps the Recording* paint-
    // time fields into the generic paint-time slots that
    // CreateConicArcStroke consumes — tweak the defaults on the struct
    // itself to iterate, just like the Transcribing*/Rewriting* knobs.
    //
    // ── Arc span + fades (from RecordingConic* defaults) ────────────────
    // Span 0.5 with Mirror = full 360° coverage split into two 180° arcs
    // that meet exactly at the sides (3 and 9 o'clock) with no overlap.
    // LeadFade/TailFade at 0.5 each get auto-scaled by the drawing code to
    // spanTurns/total = 0.25 each — pure bell, no solid core, peak opacity
    // at the lobe centre, smooth fade-out at both sides where the arcs
    // meet. Matches the design intent: the energy stays on the sides
    // and the strong fades take care of the centre.
    //
    // ── Arc phase math (from RecordingArcPhaseTurns default) ────────────
    // With Span 0.5, the source arc centre is at spanRadians/2 = 0.25·τ
    // (90° math). In Win2D's Y-down space that's (cos 90°, sin 90°)
    // = (0, 1) — straight down, 6 o'clock. Mirror at +π lands at 270° math
    // = (0, -1) = straight up, 12 o'clock.
    //
    // Target: lobes at visual 12 and 6 o'clock. Source already has a lobe
    // at 6 and mirror at 12, so we need a +0.5 turn rotation to realign
    // (the chirality flip: source 6 → visual 12, source 12 → visual 6).
    // RecordingArcPhaseTurns = 0.5 → 0.25 + 0.5 = 0.75·τ = 270° math =
    // 12 o'clock, mirror 0.75 + 0.5 = 1.25 ≡ 0.25·τ = 90° math =
    // 6 o'clock. ✓
    //
    // HuePhase doesn't matter at RecordingSaturation = 0 (uniform grey),
    // kept at 0. RecordingHuePeriodSeconds > 0 overrides the generic
    // HuePeriodSeconds to let the hue drift slowly under the frozen arc
    // lobes — requires RecordingSaturation > 0 to be visible.
    internal static ProcessingStroke CreateRecordingStroke(
        Compositor compositor, Vector2 hostSize,
        ConicArcStrokeConfig? configOverride = null)
    {
        // `with` copies the caller's (default) config and overrides the
        // generic paint-time slots consumed by CreateConicArcStroke with
        // the Recording* paint-time values. All other fields inherit
        // their defaults — the Recording* runtime fields come through
        // unchanged and are read by ApplyVariant and by the initialVariant
        // seed path below.
        //
        // HudPlayground passes a pre-customized config via configOverride
        // so its Recording* sliders land on the live struct before the
        // `with` copy bakes them into the generic slots.
        var defaults = configOverride ?? new ConicArcStrokeConfig();
        bool hueRotates = defaults.RecordingHuePeriodSeconds > 0;
        var cfg = defaults with
        {
            ConicSpanTurns     = defaults.RecordingConicSpanTurns,
            ConicLeadFadeTurns = defaults.RecordingConicLeadFadeTurns,
            ConicTailFadeTurns = defaults.RecordingConicTailFadeTurns,
            ConicFadeCurve     = defaults.RecordingConicFadeCurve,
            ArcMirror          = defaults.RecordingArcMirror,
            ArcPhaseTurns      = defaults.RecordingArcPhaseTurns,
            // Only override the generic hue period when Recording wants
            // live hue motion. Otherwise the value is ignored anyway
            // (freezeHueRotation = true below).
            HuePeriodSeconds   = hueRotates
                ? defaults.RecordingHuePeriodSeconds
                : defaults.HuePeriodSeconds,
        };
        return CreateConicArcStroke(
            compositor, hostSize, cfg,
            freezeHueRotation: !hueRotates,
            freezeArcRotation: true,
            initialOpacity:    0f,
            initialVariant:    ProcessingVariant.Recording);
    }

    // Implementation of the double-comet pipeline driven by
    // CreateProcessingStroke. Composition has no conic-gradient brush, and
    // CompositionLinearGradientBrush paints in bounding-box coordinates
    // (colour varies along a fixed screen axis, not along the
    // rounded-rect perimeter), so a true rainbow that walks around the
    // stroke needs Win2D. SpriteShape.StrokeBrush also refuses any brush
    // other than Color / Linear / Radial gradient — no SurfaceBrush,
    // no MaskBrush, no EffectBrush. So we don't stroke a shape: we
    // paint surfaces off-screen and composite them on a plain SpriteVisual.
    //
    // Three off-screen surfaces:
    //   1. Conic surface   — full 360° colour ring, painted once as pie
    //                        wedges with HSV(hue, S, V), hue = angle / 2π.
    //                        At saturation = 0 it collapses to a uniform
    //                        greyscale surface (Transcribing).
    //   2. Arc mask        — white pie slice in [0, 2π·Span] with alpha
    //                        ramps at both ends (LeadFade, TailFade);
    //                        transparent outside. Optionally mirrored
    //                        at +π for a symmetric double-comet look
    //                        (ArcMirror).
    //   3. Stroke silhouette — rounded-rect stroke outline on a
    //                        transparent background, static.
    //
    // Each surface is sampled by its own CompositionSurfaceBrush. The
    // conic and arc brushes each drive their own TransformMatrix
    // ExpressionAnimation at independent rates (HuePeriodSeconds vs
    // ArcPeriodSeconds) — this is the whole point of the split: the arc
    // window sweeps around at one rate while the spectrum spins
    // underneath at another, so every hue eventually appears at the arc
    // head instead of being locked to a fixed source-angle. The stroke
    // silhouette does not rotate.
    //
    // Composition uses a Win2D AlphaMaskEffect graph in a
    // CompositionEffectBrush:
    //
    //   step1 = AlphaMask(Source = conic,   AlphaMask = arc)
    //   final = AlphaMask(Source = step1,   AlphaMask = strokeSilhouette)
    //
    // CompositionMaskBrush is not used because its Source property
    // forbids nesting another MaskBrush (Source must be
    // Color / Surface / NineGrid / Effect brush), and we need two layers
    // of alpha masking.
    //
    // No base stroke painted in this pipeline. The permanent HUD frame
    // is the DWM HWND border (DWMWA_BORDER_COLOR = DWMWA_COLOR_DEFAULT,
    // set in HudWindow.xaml.cs) — theme-aware, always on, visible
    // through the transparent regions between arcs. Painting a second
    // stroke here would occlude it with a non-theme-tracking colour.
    //
    // Surface sizing. The conic and arc surfaces are SQUARE, sized so
    // their inscribed circle contains the visual at every rotation:
    // pxSquare = ceil(√(pxW² + pxH²)) = visual diagonal. Smaller squares
    // leave visual corners outside the source at intermediate angles —
    // those pixels sample out of bounds and go transparent, producing
    // gaps that sweep with rotation. CompositionStretch.None centres the
    // source 1:1 on the visual (alignment ratios default to 0.5),
    // preserving the oversized brush-space footprint; any other stretch
    // mode rescales the source back down to the visual's extent and
    // defeats the coverage guarantee. The stroke silhouette surface is
    // pxW × pxH because it is not rotated, and its brush uses
    // CompositionStretch.Fill to map 1:1 onto the visual.
    //
    // Rotation via ExpressionAnimation because TransformMatrix is a
    // Matrix3x2 with no built-in KeyFrameAnimation type. A scalar Angle
    // on a CompositionPropertySet drives a 0 → 2π keyframe animation, and
    // the matrix is rebuilt every frame by an expression that rotates
    // around the VISUAL centre (innerSize / 2) — not the source centre.
    // CompositionSurfaceBrush.TransformMatrix is evaluated in SpriteVisual
    // space, AFTER Stretch/alignment have placed the source. Rotating
    // around the source centre instead would orbit the oversized square
    // around a point well outside the visual — the "half the stroke
    // missing at most phases" symptom we hit initially.
    // `freezeHueRotation` / `freezeArcRotation` pin the conic and arc
    // surfaces at their HuePhaseTurns / ArcPhaseTurns offsets via a static
    // TransformMatrix (no KeyFrameAnimation, no Composition-driven angular
    // motion). The two flags are independent so a variant can spin one
    // brush while freezing the other. Recording uses
    // freezeArcRotation=true (lobes parked at visual 12/6 o'clock) while
    // freezeHueRotation toggles on RecordingHuePeriodSeconds: 0 = frozen
    // grey, >0 = slow hue drift across the silhouette.
    //
    // `initialOpacity ≥ 0` overrides cfg.TranscribingOpacity for the
    // SpriteVisual's seed opacity. Sentinel value -1 falls back to the
    // Transcribing-baseline behaviour used by Processing strokes. Recording
    // passes 0 so the outline spawns invisible — UpdateLevel ramps it up
    // from there as mic RMS arrives.
    //
    // `initialVariant` picks which runtime-variant baseline seeds the
    // initial Saturation / Hue / Exposure values (and the effectProps
    // scalars that drive them). Transcribing is the default because a
    // processing stroke always enters the graph in that state; Recording
    // passes its own variant so cold-start paints with Recording*
    // values from frame 1, avoiding a Transcribing-to-Recording flash
    // before ApplyVariant runs. Rewriting is never seeded from cold —
    // it only ever follows a prior Transcribing via ApplyVariant blend.
    private static ProcessingStroke CreateConicArcStroke(
        Compositor compositor, Vector2 hostSize, ConicArcStrokeConfig cfg,
        bool              freezeHueRotation = false,
        bool              freezeArcRotation = false,
        float             initialOpacity = -1f,
        ProcessingVariant initialVariant = ProcessingVariant.Transcribing)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        // Math.Round (NOT Ceiling) — cf. the "Pixel-perfect sizing note" in
        // the file header. At fractional-DIP extents (e.g. hostSize.Y = 78.4
        // at 125 % DPI), Ceiling oversizes the silhouette surface by up to
        // 1 px, and Stretch.Fill then compresses it back into the visual —
        // scale < 1 — so the stroke's outer edge drawn at source y = pxH
        // lands inside the visual extent, producing a 1-dip gap on the
        // bottom/right (top/left stay flush because the origin pins y=0
        // and x=0 on both surface and visual).
        int pxW = Math.Max(1, (int)MathF.Round(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Round(innerSize.Y));
        // Rotating surfaces need side ≥ visual diagonal so the inscribed
        // circle of the source covers all four visual corners at every
        // rotation angle — cf. header comment. Compute from innerSize
        // directly (not from the rounded pxW/pxH) so a down-rounded pxW
        // or pxH at fractional DPI can't clip the diagonal coverage.
        int pxSquare = (int)Math.Ceiling(Math.Sqrt(
            (double)innerSize.X * innerSize.X +
            (double)innerSize.Y * innerSize.Y));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        // Wire the process-wide DeviceLost hook the first time we touch
        // the shared device. Idempotent — see EnsureDeviceLostHook header.
        EnsureDeviceLostHook(canvasDevice);
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);

        // ── Surface 1: conic rainbow (full 360°, no arc carving here) ────
        // Surface painting extracted to PaintConicSurface so the naked
        // mask preview factory (CreateNakedMaskPreview, end of file) can
        // reuse the exact same logic without risking drift.
        var conicSurface = PaintConicSurface(canvasDevice, graphicsDevice, pxSquare, cfg);

        // ── Surface 2: arc mask (white pie slice, fade at both ends) ─────
        // Painted with straight alpha; Win2D premultiplies on write into a
        // Premultiplied surface. Colour channels are full white so the
        // mask's luminance does not tint the conic — only its alpha drives
        // the AlphaMaskEffect output. When cfg.ArcMirror is true, the
        // same arc is painted a second time at +π (180°) inside the same
        // surface, so both copies rotate together and stay in perfect
        // symmetry. Extracted to PaintArcMaskSurface for CreateNaked-
        // MaskPreview reuse.
        //
        // fillColor = Colors.White: downstream AlphaMaskEffect reads only
        // .A, so the mask's RGB is invisible to the shipping stroke. We
        // still write premultiplied-consistent bytes (white · α).
        var arcMaskSurface = PaintArcMaskSurface(
            canvasDevice, graphicsDevice, pxSquare, cfg, Colors.White);

        // ── Surface 3: stroke silhouette (static rounded-rect outline) ───
        // Inset by 0.5 dip so the 1-dip stroke is centred on the same path
        // the ShapeVisual would walk, preserving pixel-centre alignment
        // with the DWM frame.
        var strokeMaskSurface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxW, pxH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(strokeMaskSurface))
        {
            ds.Clear(Colors.Transparent);
            var rect = new Windows.Foundation.Rect(
                StrokeThickness / 2f,
                StrokeThickness / 2f,
                pxW - StrokeThickness,
                pxH - StrokeThickness);
            ds.DrawRoundedRectangle(rect, CornerRadiusDip, CornerRadiusDip, Colors.White, StrokeThickness);
        }

        // ── Surface brushes ──────────────────────────────────────────────
        // Stretch.None on the two rotating brushes preserves the oversized
        // pxSquare footprint in brush space. Stretch.Fill on the static
        // stroke brush maps 1:1 onto the visual (it is already pxW × pxH).
        var conicBrush = compositor.CreateSurfaceBrush(conicSurface);
        conicBrush.Stretch = CompositionStretch.None;

        var arcMaskBrush = compositor.CreateSurfaceBrush(arcMaskSurface);
        arcMaskBrush.Stretch = CompositionStretch.None;

        var strokeMaskBrush = compositor.CreateSurfaceBrush(strokeMaskSurface);
        strokeMaskBrush.Stretch = CompositionStretch.Fill;

        // ── Independent rotations for conic and arc ──────────────────────
        // Rotation around the VISUAL centre via T(-c) · R(θ) · T(+c)
        // composite, expressed in Composition's Matrix3x2 helpers.
        // CreateRotation takes radians; row-vector convention means
        // translations flank the rotation symmetrically. c = innerSize/2
        // because TransformMatrix is in SpriteVisual space, not source
        // pixel space.
        var visualCentre = new Vector2(innerSize.X / 2f, innerSize.Y / 2f);

        // Hue rotation — spin or freeze independently of the arc rotation.
        // Static TransformMatrix pins the brush at its HuePhaseTurns offset
        // with NO KeyFrameAnimation: same T(-c) · R(θ) · T(+c) composite
        // that StartRotation builds at t=0, baked into a one-shot matrix.
        // System.Numerics.Matrix3x2 uses row-vector convention, matching
        // Composition's expression-side maths.
        CompositionPropertySet? hueRotationProps = null;
        if (freezeHueRotation)
        {
            conicBrush.TransformMatrix =
                Matrix3x2.CreateTranslation(-visualCentre) *
                Matrix3x2.CreateRotation(MathF.Tau * cfg.HuePhaseTurns) *
                Matrix3x2.CreateTranslation( visualCentre);
        }
        else
        {
            hueRotationProps = StartRotation(
                compositor, conicBrush, visualCentre,
                cfg.HuePeriodSeconds,
                cfg.HueDirection,
                cfg.HuePhaseTurns,
                cfg.HueEaseP1X, cfg.HueEaseP1Y,
                cfg.HueEaseP2X, cfg.HueEaseP2Y,
                cfg.HueMinSpeedFraction);
        }

        // Arc rotation — spin or freeze independently of the hue rotation.
        // Recording always freezes the arc (lobes parked at visual 12/6
        // o'clock via RecordingArcPhaseTurns); Transcribing / Rewriting
        // always spin.
        CompositionPropertySet? arcRotationProps = null;
        if (freezeArcRotation)
        {
            arcMaskBrush.TransformMatrix =
                Matrix3x2.CreateTranslation(-visualCentre) *
                Matrix3x2.CreateRotation(MathF.Tau * cfg.ArcPhaseTurns) *
                Matrix3x2.CreateTranslation( visualCentre);
        }
        else
        {
            arcRotationProps = StartRotation(
                compositor, arcMaskBrush, visualCentre,
                cfg.ArcPeriodSeconds,
                cfg.ArcDirection,
                cfg.ArcPhaseTurns,
                cfg.ArcEaseP1X, cfg.ArcEaseP1Y,
                cfg.ArcEaseP2X, cfg.ArcEaseP2Y,
                cfg.ArcMinSpeedFraction);
        }

        // ── Effect graph ─────────────────────────────────────────────────
        //   Conic ──► Sat ──► Hue ──► Exp ──► AlphaMask(Arc) ──► AlphaMask(Stroke)
        //
        // Three live colour knobs (SaturationEffect, HueRotationEffect,
        // ExposureEffect) sit between the conic palette and the masking
        // stage. Each has its animable property exposed on the brush under
        // the name "<EffectName>.<PropertyName>", and bound (via a single
        // ExpressionAnimation per property) to a scalar on `effectProps`.
        // ProcessingStroke.ApplyVariant animates those scalars — never
        // the effect properties directly — so a "start from current value"
        // expression keyframe reads the PropertySet scalar, which always
        // holds the last animated value (the effect-brush property is a
        // derived mirror and `this.CurrentValue` on it is not reliable
        // mid-binding).
        //
        // Order rationale:
        //   Saturation BEFORE HueRotation so a greyscale target (Sat=0)
        //     already kills the palette — a HueRotation on grey is a no-op
        //     and doesn't waste GPU. If HueRotation were first, going grey
        //     would still shift hues on the way down.
        //   Exposure LAST so brightness shifts apply to the already-tinted
        //     colour. Putting it first would change the "source" brightness
        //     before Saturation read it, shifting the desaturated grey
        //     level away from the expected neutral.
        //
        // AlphaMaskEffect semantics: output = (Source.RGB, Source.A * Mask.A).
        // Both masks compound; the final pixel RGB is the (effect-modified)
        // conic colour, alpha = conic.A · arc.A · stroke.A.
        //
        // Initial effect values follow initialVariant's (Dark) baseline.
        // Processing strokes pass Transcribing (first processing state;
        // Rewriting can only follow a prior Transcribing via ApplyVariant
        // blend — seeding Rewriting values cold caused a visible rainbow
        // flash we fixed by defaulting to Transcribing). Recording strokes
        // pass their own variant so cold-start paints with Recording*
        // values from frame 1, skipping any Transcribing-to-Recording
        // flash before ApplyVariant runs.
        float seedSaturation = initialVariant switch
        {
            ProcessingVariant.Recording    => cfg.RecordingSaturationDark,
            ProcessingVariant.Rewriting    => cfg.RewritingSaturation,
            _                              => cfg.TranscribingSaturationDark,
        };
        float seedHueShiftTurns = initialVariant switch
        {
            ProcessingVariant.Recording    => cfg.RecordingHueShiftTurns,
            ProcessingVariant.Rewriting    => cfg.RewritingHueShiftTurns,
            _                              => cfg.TranscribingHueShiftTurns,
        };
        float seedExposure = initialVariant switch
        {
            ProcessingVariant.Recording    => cfg.RecordingExposureDark,
            ProcessingVariant.Rewriting    => cfg.RewritingExposure,
            _                              => cfg.TranscribingExposureDark,
        };

        var saturationEffect = new SaturationEffect
        {
            Name       = "Sat",
            Saturation = seedSaturation,
            Source     = new CompositionEffectSourceParameter("Conic"),
        };
        var hueEffect = new HueRotationEffect
        {
            Name   = "Hue",
            Angle  = seedHueShiftTurns * MathF.Tau,
            Source = saturationEffect,
        };
        var exposureEffect = new ExposureEffect
        {
            Name     = "Exp",
            Exposure = seedExposure,
            Source   = hueEffect,
        };
        var effectGraph = new AlphaMaskEffect
        {
            Source = new AlphaMaskEffect
            {
                Source    = exposureEffect,
                AlphaMask = new CompositionEffectSourceParameter("Arc"),
            },
            AlphaMask = new CompositionEffectSourceParameter("Stroke"),
        };

        // Declaring animable properties on the factory is what makes
        // effectBrush.StartAnimation("Sat.Saturation", …) legal — without
        // this list Composition rejects the property name.
        var effectFactory = compositor.CreateEffectFactory(
            effectGraph,
            new[] { "Sat.Saturation", "Hue.Angle", "Exp.Exposure" });
        var effectBrush = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("Conic",  conicBrush);
        effectBrush.SetSourceParameter("Arc",    arcMaskBrush);
        effectBrush.SetSourceParameter("Stroke", strokeMaskBrush);

        // PropertySet holds the live-animable scalars. ApplyVariant
        // animates these; the ExpressionAnimations below propagate their
        // values to the effect-brush properties every frame.
        var effectProps = compositor.CreatePropertySet();
        effectProps.InsertScalar("Saturation", seedSaturation);
        effectProps.InsertScalar("HueAngle",   seedHueShiftTurns * MathF.Tau);
        effectProps.InsertScalar("Exposure",   seedExposure);

        BindEffectProperty(compositor, effectBrush, "Sat.Saturation", effectProps, "Saturation");
        BindEffectProperty(compositor, effectBrush, "Hue.Angle",      effectProps, "HueAngle");
        BindEffectProperty(compositor, effectBrush, "Exp.Exposure",   effectProps, "Exposure");

        var strokeVisual = compositor.CreateSpriteVisual();
        strokeVisual.Size    = innerSize;
        strokeVisual.Offset  = new Vector3(InsetDip, InsetDip, 0f);
        strokeVisual.Brush   = effectBrush;
        // initialOpacity sentinel (-1) falls back to the Transcribing
        // baseline used by Processing strokes. Recording passes 0 so the
        // outline spawns invisible — UpdateLevel ramps it with mic RMS.
        strokeVisual.Opacity = initialOpacity >= 0f ? initialOpacity : cfg.TranscribingOpacity;

        container.Children.InsertAtTop(strokeVisual);
        return new ProcessingStroke(
            container, compositor, effectProps, strokeVisual, cfg,
            conicBrush, arcMaskBrush, strokeMaskBrush, effectBrush,
            conicSurface, arcMaskSurface, strokeMaskSurface,
            hueRotationProps, arcRotationProps);
    }

    // Forwards an effect-brush property to a named PropertySet scalar via
    // a trivial ExpressionAnimation — "whatever the scalar is, the effect
    // property reads the same". Separates "what's animated" (the scalar,
    // driven by ApplyVariant) from "what consumes the value" (the effect
    // graph). Needed because Composition KeyFrameAnimations on the effect
    // brush itself don't support reading "this.CurrentValue" reliably when
    // another animation is in flight on the same property.
    private static void BindEffectProperty(
        Compositor compositor,
        CompositionEffectBrush effectBrush,
        string effectPropertyPath,
        CompositionPropertySet source,
        string sourcePropertyName)
    {
        var expr = compositor.CreateExpressionAnimation($"src.{sourcePropertyName}");
        expr.SetReferenceParameter("src", source);
        effectBrush.StartAnimation(effectPropertyPath, expr);
    }

    // ── Cyclic rotation with out-in easing + vestigial speed floor ─────
    //
    // What it does (UX):
    //   The rotations breathe — fast at the cycle seam, gentle slow-down
    //   around the midpoint, fast again at the next seam — via a
    //   cubic-bezier shaped as an OUT-IN curve (0.125, 0.375, 0.875, 0.625).
    //
    //   The defining property: both endpoint tangents have slope 3.0
    //   (= 0.375/0.125 = (1-0.625)/(1-0.875)). When the KeyFrame animation
    //   loops via AnimationIterationBehavior.Forever, the seam between
    //   cycles is C¹-continuous: the eye reads the motion as one
    //   continuous pulse repeating, not a start-stop-restart.
    //
    //   Contrast with the earlier in-out (0.2, 0, 0.8, 1): zero-slope
    //   endpoints meant every cycle ended at near-zero angular velocity,
    //   and the next cycle started at zero again. Two plateaus adjacent
    //   in time read as "the animation broke". That forced a linear-blend
    //   workaround (minSpeedFraction) to lift the floor, which itself
    //   compressed the peak toward the mean — pulse lost either way.
    //
    //   Out-in solves this structurally: no plateaus at the seam, so the
    //   pulse shape (slow → fast → slow → fast) is preserved intact.
    //
    // Velocity profile (at ω_mean = 2π/8s ≈ 45°/s, minSpeedFraction = 0):
    //   At t = 0, 1      ω ≈ 3 · ω_mean ≈ 135°/s  (peak at the seam)
    //   At t ≈ 0.5       ω ≈ 0.5 · ω_mean ≈ 22°/s (mid-cycle slow-down)
    //   Angular velocity never hits zero.
    //
    // `minSpeedFraction` (f) — vestigial with the current out-in curve
    // but kept for playground experimentation with exotic ease shapes.
    // Blends a pure linear scalar with the eased one:
    //   angle(t) = Linear · f + Eased · (1 − f)
    //   ω(t)     = ω_mean · [ f + (1 − f)·E'(t) ]
    // At f = 0 (shipping default) the eased curve is used as-is.
    //
    // How it works (implementation):
    //   Two scalars on the same PropertySet animate over the period —
    //   `Linear` (no easing function = linear interpolation) and `Eased`
    //   (cubic-bezier). An ExpressionAnimation rebuilds the Matrix3x2
    //   every frame around the visual centre with the angle computed as:
    //
    //       angle = Linear · f + Eased · (1 − f)
    //
    //   Both scalars start at startAngle and end at endAngle over the
    //   same period, so the sum is in [startAngle, endAngle] at all
    //   times and lands exactly on endAngle at period end — the loop
    //   closes seamlessly with no phase drift across iterations.
    // Returns the internal PropertySet so the caller (typically
    // CreateConicArcStroke) can hold a strong ref and explicitly
    // StopAnimation / Dispose at teardown. Letting the managed ref
    // die leaks the two ScalarKeyFrameAnimations (Linear / Eased)
    // indefinitely on the compositor — after enough rebuilds the
    // compositor saturates and Forever animations stop firing.
    private static CompositionPropertySet StartRotation(
        Compositor compositor,
        CompositionSurfaceBrush brush,
        Vector2 visualCentre,
        double periodSeconds,
        float direction,
        float phaseTurns,
        float easeP1X, float easeP1Y,
        float easeP2X, float easeP2Y,
        float minSpeedFraction)
    {
        float startAngle = MathF.Tau * phaseTurns;
        float fullAngle  = MathF.Tau * direction;
        float endAngle   = startAngle + fullAngle;

        var props = compositor.CreatePropertySet();
        props.InsertScalar("Linear", startAngle);
        props.InsertScalar("Eased",  startAngle);

        // Clamp period to a strictly positive minimum. A zero-duration
        // KeyFrameAnimation with IterationBehavior.Forever resolves to
        // end-state on frame 0 and then never advances — visually
        // indistinguishable from "the stroke froze". The shipping
        // defaults are 8s so this clamp is a no-op there; the
        // HudPlayground sliders can now land on 0 without killing the
        // animation silently. 0.05s ≈ 20 turns/second, the fastest
        // readable rotation before the eye sees strobing — a sensible
        // floor for the playground's "how fast can I push it" probing.
        double clampedPeriod = Math.Max(0.05, periodSeconds);
        var duration = TimeSpan.FromSeconds(clampedPeriod);

        // Linear scalar — no easing function = default linear interpolation
        // between keyframes. Constant angular velocity = 2π / period.
        var linearAnim = compositor.CreateScalarKeyFrameAnimation();
        linearAnim.InsertKeyFrame(0f, startAngle);
        linearAnim.InsertKeyFrame(1f, endAngle);
        linearAnim.Duration          = duration;
        linearAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        props.StartAnimation("Linear", linearAnim);

        // Eased scalar — cubic-bezier easing. Velocity may be near zero
        // around the curve plateaus; the linear scalar carries the floor.
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(easeP1X, easeP1Y),
            new Vector2(easeP2X, easeP2Y));
        var easedAnim = compositor.CreateScalarKeyFrameAnimation();
        easedAnim.InsertKeyFrame(0f, startAngle);
        easedAnim.InsertKeyFrame(1f, endAngle, easing);
        easedAnim.Duration          = duration;
        easedAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        props.StartAnimation("Eased", easedAnim);

        // `minSpeedFraction` (f) mixes the two scalars: pure easing at f=0,
        // pure linear at f=1. Clamped defensively so a stray config value
        // can't invert the rotation (f < 0 would negate the Eased
        // contribution) or amplify it past the unit interval (f > 1 would
        // make Linear contribute more than the mean).
        float clampedFraction = Math.Clamp(minSpeedFraction, 0f, 1f);

        // CRITICAL — Composition's expression language is NOT C#. Numeric
        // literals are written without any suffix: `1.0` is a Float, `1`
        // is an Int. A C# `1.0f` would be parsed as `1.0 * f` with `f` as
        // a missing variable (default 0), turning `(1.0f - minFrac)` into
        // `-minFrac` and the whole expression into a yo-yo motion. Stay
        // strict on the literal syntax here.
        var matrixExpr = compositor.CreateExpressionAnimation(
            "Matrix3x2.CreateTranslation(negCentre) * " +
            "Matrix3x2.CreateRotation(props.Linear * minFrac + props.Eased * (1.0 - minFrac)) * " +
            "Matrix3x2.CreateTranslation(posCentre)");
        matrixExpr.SetReferenceParameter("props", props);
        matrixExpr.SetVector2Parameter("negCentre", -visualCentre);
        matrixExpr.SetVector2Parameter("posCentre",  visualCentre);
        matrixExpr.SetScalarParameter ("minFrac",    clampedFraction);
        brush.StartAnimation("TransformMatrix", matrixExpr);
        return props;
    }

    // Full-circle angular gradient surface. pxSquare × pxSquare,
    // painted per pixel: each pixel's colour is OKLCh(OklchLightness,
    // OklchChroma, hue) where hue = (atan2(dy, dx) / 2π) * HueRange
    // + HueStart, optionally quantised into WedgeCount posterised
    // sectors. Shared between CreateConicArcStroke (the shipping
    // stroke) and CreateNakedMaskPreview (dev diagnostic).
    //
    // Why per-pixel instead of rasterising WedgeCount triangles —
    // the old approach drew 360 CanvasGeometry polygons whose shared
    // edges meet at subpixel positions all the way down to the
    // centre. Even with D2D's antialiasing, the triangle boundaries
    // each produce a small coverage error; summed over 360 seams
    // fanning out from the centre, the errors align into a visible
    // radial moiré (the "grid pattern" first spotted on the baked
    // palette screenshot). Per-pixel evaluation has no
    // polygon seams — every pixel is an independent atan2 sample —
    // so the gradient comes out perfectly smooth.
    //
    // Cost: pxSquare² OklchToRgb calls at bake time (≈74 k calls for
    // 272² — a few ms on a cold cache). Paint runs once per stroke
    // creation (variant boundary cross or playground config change),
    // never per frame, so this is immaterial at runtime.
    private static CompositionDrawingSurface PaintConicSurface(
        CanvasDevice canvasDevice,
        CompositionGraphicsDevice graphicsDevice,
        int pxSquare,
        ConicArcStrokeConfig cfg)
    {
        var surface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxSquare, pxSquare),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        // BGRA premultiplied layout — matches the surface format above.
        // WedgeCount doubles as a posterisation knob: at 360 (default)
        // on a 272-pixel circle each wedge is ~0.76 px wide → reads as
        // continuous; at 12/24 it snaps into visible retro sectors.
        var bytes       = new byte[pxSquare * pxSquare * 4];
        float centre    = pxSquare / 2f;
        int   wedges    = Math.Max(1, cfg.WedgeCount);
        float wedgeStep = MathF.Tau / wedges;

        // Pre-compute the wedge palette once. Each pixel only needs
        // atan2 + a table lookup, instead of a full OKLCh conversion
        // per pixel — cuts bake time from ~15 ms to ~2 ms at 272².
        var palette = new byte[wedges * 3];
        for (int i = 0; i < wedges; i++)
        {
            float centreTurns = (i + 0.5f) / wedges;
            float hue = cfg.HueStart + centreTurns * cfg.HueRange;
            var c = OklchToRgb(cfg.OklchLightness, cfg.OklchChroma, hue);
            int p = i * 3;
            palette[p + 0] = c.B;
            palette[p + 1] = c.G;
            palette[p + 2] = c.R;
        }

        for (int y = 0; y < pxSquare; y++)
        {
            float dy = y + 0.5f - centre;
            for (int x = 0; x < pxSquare; x++)
            {
                float dx = x + 0.5f - centre;

                // atan2 yields [-π, π]; shift to [0, 2π) so the wedge
                // index below stays in positive angle space.
                float ang = MathF.Atan2(dy, dx);
                if (ang < 0) ang += MathF.Tau;

                int wi = (int)(ang / wedgeStep);
                if (wi >= wedges) wi = wedges - 1;

                int idx = (y * pxSquare + x) * 4;
                int p   = wi * 3;
                bytes[idx + 0] = palette[p + 0];
                bytes[idx + 1] = palette[p + 1];
                bytes[idx + 2] = palette[p + 2];
                bytes[idx + 3] = 0xFF;
            }
        }

        // CanvasBitmap.CreateFromBytes takes the Windows.Graphics.DirectX
        // pixel-format enum; the file's `using Microsoft.Graphics.DirectX`
        // resolves the unqualified name to Microsoft's homonym, which is
        // what CompositionDrawingSurface expects above. Fully-qualify
        // here to route the literal to the right overload.
        using var bitmap = CanvasBitmap.CreateFromBytes(
            canvasDevice, bytes, pxSquare, pxSquare,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Colors.Transparent);
        ds.DrawImage(bitmap);

        return surface;
    }

    // Arc-shaped alpha mask surface — single span in [0, 2π·ConicSpanTurns]
    // with lead/tail alpha ramps governed by ConicLeadFadeTurns,
    // ConicTailFadeTurns, ConicFadeCurve; optionally mirrored at +π when
    // ArcMirror is set. Full white, straight-alpha. Shared with
    // CreateNakedMaskPreview so the dev rail sees the exact same fade
    // geometry the shipping stroke uses.
    //
    // Why per-pixel (not polygonal wedges like the old code) — same
    // reason as PaintConicSurface: rasterising WedgeCount triangles that
    // share a vertex at the centre produces a radial moiré at high wedge
    // counts, because D2D's antialiased coverage at each shared seam is a
    // tiny bit off-1 and the errors stack along every fan ray. That moiré
    // was invisible on the Conic-only preview once that path went
    // per-pixel, but it reappeared whenever the ArcMask was composited in
    // (Rewriting, Combined, and the shipping stroke for Transcribing /
    // Rewriting / Recording — all of which route through AlphaMaskEffect
    // with Arc as the mask). Per-pixel eliminates the polygon seams
    // entirely; alpha is computed independently at each pixel from its
    // own atan2 angle, with the same lead / tail / curve / mirror
    // semantics the polygonal path had.
    //
    // Bonus: no CanvasGeometry.CreatePolygon calls, which removes the
    // degenerate-triangle edge case (near-colinear vertices at high
    // WedgeCount) that Win2D can throw on.
    // `fillColor` — premultiplied RGB written alongside the coverage alpha.
    // Shipping passes Colors.White because the downstream AlphaMaskEffect
    // only reads .A (colour is invisible to the stroke's masking stage).
    // The playground's Naked rail passes a theme-aware opaque colour
    // (black on light, white on dark) so ArcMask and ArcMask-only overlays
    // are legible against LayerFillColorDefaultBrush in both themes —
    // without a colour knob the white-on-alpha mask vanished on light.
    private static CompositionDrawingSurface PaintArcMaskSurface(
        CanvasDevice canvasDevice,
        CompositionGraphicsDevice graphicsDevice,
        int pxSquare,
        ConicArcStrokeConfig cfg,
        Color fillColor)
    {
        var surface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxSquare, pxSquare),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        // Mirror: max Span is 0.5 so the two arcs can't overlap.
        // Without mirror: Span can go up to 1 (full ring).
        float maxSpanTurns  = cfg.ArcMirror ? 0.5f : 1f;
        float spanTurns     = Math.Clamp(cfg.ConicSpanTurns, 0f, maxSpanTurns);
        float leadFadeTurns = Math.Clamp(cfg.ConicLeadFadeTurns, 0f, spanTurns);
        float tailFadeTurns = Math.Clamp(cfg.ConicTailFadeTurns, 0f, spanTurns);
        // If the two fades would overlap past the span, scale both so
        // they just meet at the mid of the arc (bell shape, no solid
        // core). Otherwise preserve the user-requested lengths.
        float totalFadeTurns = leadFadeTurns + tailFadeTurns;
        if (totalFadeTurns > spanTurns && totalFadeTurns > 0f)
        {
            float scale = spanTurns / totalFadeTurns;
            leadFadeTurns *= scale;
            tailFadeTurns *= scale;
        }
        float spanRadians      = MathF.Tau * spanTurns;
        float leadFadeRadians  = MathF.Tau * leadFadeTurns;
        float tailFadeRadians  = MathF.Tau * tailFadeTurns;
        float tailStartRadians = spanRadians - tailFadeRadians;
        float curve            = MathF.Max(0.01f, cfg.ConicFadeCurve);
        bool  mirror           = cfg.ArcMirror;

        // Early-out for a degenerate span (no arc visible). Return the
        // empty transparent surface rather than iterating the full grid
        // with alpha=0 — saves ~3 ms on 272² at no visual cost.
        if (spanRadians <= 0f)
            return surface;

        var bytes  = new byte[pxSquare * pxSquare * 4];
        float centre = pxSquare / 2f;

        for (int y = 0; y < pxSquare; y++)
        {
            float dy = y + 0.5f - centre;
            for (int x = 0; x < pxSquare; x++)
            {
                float dx = x + 0.5f - centre;

                // atan2 yields [-π, π]; shift to [0, 2π) for positive
                // angle space matching the polygonal path's convention.
                float ang = MathF.Atan2(dy, dx);
                if (ang < 0) ang += MathF.Tau;

                // Mirror collapses the second branch (ang ∈ [π, 2π))
                // onto the first [0, π) so a single alpha profile
                // computation covers both arcs. The polygonal path used
                // a branch loop with identical alpha profiles — the
                // collapse here is the per-pixel equivalent.
                if (mirror && ang >= MathF.PI)
                    ang -= MathF.PI;

                float alpha;
                if (ang >= spanRadians)
                {
                    alpha = 0f;
                }
                else if (leadFadeRadians > 0f && ang < leadFadeRadians)
                {
                    // Leading ramp: 0 at a=0, 1 at a=LeadFade.
                    float t = ang / leadFadeRadians;
                    alpha = MathF.Pow(t, curve);
                }
                else if (tailFadeRadians > 0f && ang >= tailStartRadians)
                {
                    // Trailing ramp: 1 at a=Span-TailFade, 0 at a=Span.
                    float t = (ang - tailStartRadians) / tailFadeRadians;
                    alpha = MathF.Pow(1f - t, curve);
                }
                else
                {
                    alpha = 1f;
                }

                byte a = (byte)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
                int idx = (y * pxSquare + x) * 4;

                // Premultiplied BGRA: a mask with fill colour (R, G, B) at
                // coverage α stores as (B·α/255, G·α/255, R·α/255, α).
                // AlphaMaskEffect downstream reads .A as the mask value;
                // RGB is invisible to the shipping stroke's masking stage
                // but matters for the playground's ArcMask / Combined naked
                // rails where the user sees the surface directly — hence
                // the theme-driven fillColor parameter.
                bytes[idx + 0] = (byte)((fillColor.B * a) / 255);
                bytes[idx + 1] = (byte)((fillColor.G * a) / 255);
                bytes[idx + 2] = (byte)((fillColor.R * a) / 255);
                bytes[idx + 3] = a;
            }
        }

        using var bitmap = CanvasBitmap.CreateFromBytes(
            canvasDevice, bytes, pxSquare, pxSquare,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Colors.Transparent);
        ds.DrawImage(bitmap);

        return surface;
    }

    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Naked mask diagnostic (HudPlayground only)                        ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // Exposes the raw conic and arc-mask surfaces — the same ones
    // CreateConicArcStroke composites behind the stroke silhouette — so
    // the developer can verify in the playground whether the brush
    // geometry is centred on its own rotation axis. The shipping stroke clips every
    // sample to the rounded-rect silhouette, which hides any rotation
    // wobble or off-centre brush footprint; the naked preview removes that
    // silhouette and lets the full pxSquare × pxSquare footprint rotate
    // openly. Not referenced by shipping code — only the playground's
    // Naked rail wires it up.
    internal enum NakedMaskPart
    {
        Conic    = 1,   // raw 360° HSV rainbow ring, full square
        ArcMask  = 2,   // alpha-ramped pie slice(s), monochrome
        Combined = 3,   // Conic ⊗ ArcMask, no stroke silhouette
    }

    // Disposable bundle returned by CreateNakedMaskPreview. Mirrors the
    // ProcessingStroke pattern on purpose: the rotation PropertySets
    // returned by StartRotation drive two Forever ScalarKeyFrameAnimations
    // that the compositor keeps live until they are explicitly stopped.
    // If the caller (PlaygroundWindow) lets the PropertySets fall out of
    // scope on every rebuild, the compositor accumulates orphan
    // animations; after enough slider moves it saturates and Forever
    // animations across the whole window silently freeze — which is the
    // "Conic preview frozen mid-animation" regression that was reported.
    //
    // Ownership convention: the caller holds the NakedPreview while the
    // bundle is mounted on the visual tree, disposes it BEFORE replacing
    // it with a fresh one (and before the host Window closes). Dispose
    // stops both rotations' Linear + Eased animations and releases every
    // Composition object the bundle allocated.
    internal sealed class NakedPreview : IDisposable
    {
        public ContainerVisual Container { get; }

        private readonly SpriteVisual _sprite;
        private readonly CompositionSurfaceBrush _conicBrush;
        private readonly CompositionSurfaceBrush _arcMaskBrush;
        private readonly CompositionEffectBrush? _effectBrush;
        private readonly CompositionDrawingSurface _conicSurface;
        private readonly CompositionDrawingSurface _arcMaskSurface;
        private readonly CompositionPropertySet _conicRotationProps;
        private readonly CompositionPropertySet _arcRotationProps;

        private bool _disposed;

        internal NakedPreview(
            ContainerVisual container,
            SpriteVisual sprite,
            CompositionSurfaceBrush conicBrush,
            CompositionSurfaceBrush arcMaskBrush,
            CompositionEffectBrush? effectBrush,
            CompositionDrawingSurface conicSurface,
            CompositionDrawingSurface arcMaskSurface,
            CompositionPropertySet conicRotationProps,
            CompositionPropertySet arcRotationProps)
        {
            Container            = container;
            _sprite              = sprite;
            _conicBrush          = conicBrush;
            _arcMaskBrush        = arcMaskBrush;
            _effectBrush         = effectBrush;
            _conicSurface        = conicSurface;
            _arcMaskSurface      = arcMaskSurface;
            _conicRotationProps  = conicRotationProps;
            _arcRotationProps    = arcRotationProps;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 1. Stop every Forever animation so the native scheduler drops
            //    its refs. ExpressionAnimation on the brush's TransformMatrix
            //    is what binds the PropertySet scalars; stop it first so
            //    stopping the scalars doesn't race against a live read.
            try { _conicBrush.StopAnimation("TransformMatrix");   } catch { }
            try { _arcMaskBrush.StopAnimation("TransformMatrix"); } catch { }

            try { _conicRotationProps.StopAnimation("Linear"); } catch { }
            try { _conicRotationProps.StopAnimation("Eased");  } catch { }
            try { _arcRotationProps.StopAnimation("Linear");   } catch { }
            try { _arcRotationProps.StopAnimation("Eased");    } catch { }

            // 2. Dispose in the same order the shipping stroke uses:
            //    effect brush first (it binds the source brushes), then
            //    surface brushes, then surfaces, then property sets, then
            //    the sprite + container last.
            try { _effectBrush?.Dispose();   } catch { }
            try { _conicBrush.Dispose();     } catch { }
            try { _arcMaskBrush.Dispose();   } catch { }

            try { _conicSurface.Dispose();   } catch { }
            try { _arcMaskSurface.Dispose(); } catch { }

            try { _conicRotationProps.Dispose(); } catch { }
            try { _arcRotationProps.Dispose();   } catch { }

            try { _sprite.Dispose();    } catch { }
            try { Container.Dispose();  } catch { }
        }
    }

    // Returns a ContainerVisual sized pxSquare × pxSquare — the same
    // visual-diagonal coverage the shipping stroke bakes its rotating
    // brush into. Inside sits one SpriteVisual filling that container,
    // with a brush chosen by `part`:
    //   - Conic    : SurfaceBrush over the painted conic surface.
    //   - ArcMask  : SurfaceBrush over the painted arc mask surface.
    //   - Combined : CompositionEffectBrush running AlphaMaskEffect on
    //                the two surfaces, identical to the first mask stage
    //                in CreateConicArcStroke but WITHOUT the stroke
    //                silhouette alpha stage — exposes the full pre-clip
    //                arc geometry to the eye.
    //
    // Both brushes spin independently at HuePeriodSeconds /
    // ArcPeriodSeconds around the sprite's geometric centre
    // (pxSquare / 2). If the rotation centre and the brush-painting
    // centre disagree (the working hypothesis behind the top/bottom
    // luminance asymmetry observation), the wobble reads immediately on the naked
    // preview as a drifting lobe or a wandering dead-spot.
    //
    // No effect-pipeline colour knobs (Saturation / Hue / Exposure) —
    // those are state-blend concerns; the diagnostic is about geometry,
    // and stripping the colour pipeline keeps the visual signal focused
    // on where each lobe actually lands.
    //
    // `arcFillColor` — the caller picks a theme-legible opaque colour for
    // the arc mask surface (black on light substrates, white on dark). The
    // Combined path composites through AlphaMaskEffect and reads only the
    // mask's .A, so the colour is invisible there; the ArcMask rail draws
    // the mask directly and relies on this colour to show up against the
    // window's LayerFillColorDefaultBrush in both themes.
    //
    // Returns a NakedPreview bundle the caller MUST hold and Dispose when
    // replaced — see the type-level comment on NakedPreview for why.
    internal static NakedPreview CreateNakedMaskPreview(
        Compositor compositor, Vector2 hudSize,
        ConicArcStrokeConfig cfg, NakedMaskPart part,
        Color arcFillColor)
    {
        // Reuse the exact pxSquare math from CreateConicArcStroke so the
        // naked preview paints the same brush footprint. Any drift here
        // would invalidate the diagnostic — the user would be looking at
        // a different geometry than the shipping stroke samples.
        var innerSize = new Vector2(hudSize.X - 2f * InsetDip, hudSize.Y - 2f * InsetDip);
        int pxSquare = (int)Math.Ceiling(Math.Sqrt(
            (double)innerSize.X * innerSize.X +
            (double)innerSize.Y * innerSize.Y));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        EnsureDeviceLostHook(canvasDevice);
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            compositor, canvasDevice);

        var container = compositor.CreateContainerVisual();
        container.Size = new Vector2(pxSquare, pxSquare);

        var conicSurface   = PaintConicSurface  (canvasDevice, graphicsDevice, pxSquare, cfg);
        var arcMaskSurface = PaintArcMaskSurface(canvasDevice, graphicsDevice, pxSquare, cfg, arcFillColor);

        // Stretch.Fill — source is pxSquare, sprite is pxSquare, map 1:1.
        // (CreateConicArcStroke uses Stretch.None because its sprite is
        // innerSize, not pxSquare; here the sprite IS pxSquare so Fill
        // lands trivially.)
        var conicBrush = compositor.CreateSurfaceBrush(conicSurface);
        conicBrush.Stretch = CompositionStretch.Fill;

        var arcMaskBrush = compositor.CreateSurfaceBrush(arcMaskSurface);
        arcMaskBrush.Stretch = CompositionStretch.Fill;

        // Rotate around the sprite's OWN centre. The shipping stroke
        // rotates around the visual centre (innerSize/2) because its
        // sprite is innerSize and the surface is oversized pxSquare; here
        // the sprite is pxSquare and the surface is pxSquare, so the
        // correct rotation centre is pxSquare/2. This is the whole point
        // of the diagnostic: if the brush's painted centre doesn't match
        // pxSquare/2, the naked preview will show a wobble the shipping
        // stroke silently masks.
        var centre = new Vector2(pxSquare / 2f, pxSquare / 2f);

        // Capture the returned PropertySets — see NakedPreview class
        // comment. Letting them die on GC leaks two Forever animations
        // per rebuild and eventually freezes every Composition animation
        // in the window.
        var conicRotationProps = StartRotation(
            compositor, conicBrush, centre,
            cfg.HuePeriodSeconds,
            cfg.HueDirection,
            cfg.HuePhaseTurns,
            cfg.HueEaseP1X, cfg.HueEaseP1Y,
            cfg.HueEaseP2X, cfg.HueEaseP2Y,
            cfg.HueMinSpeedFraction);
        var arcRotationProps = StartRotation(
            compositor, arcMaskBrush, centre,
            cfg.ArcPeriodSeconds,
            cfg.ArcDirection,
            cfg.ArcPhaseTurns,
            cfg.ArcEaseP1X, cfg.ArcEaseP1Y,
            cfg.ArcEaseP2X, cfg.ArcEaseP2Y,
            cfg.ArcMinSpeedFraction);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Size = new Vector2(pxSquare, pxSquare);

        CompositionEffectBrush? effectBrush = null;
        switch (part)
        {
            case NakedMaskPart.Conic:
                sprite.Brush = conicBrush;
                break;
            case NakedMaskPart.ArcMask:
                sprite.Brush = arcMaskBrush;
                break;
            case NakedMaskPart.Combined:
                // Single AlphaMaskEffect — output = (Conic.RGB, Conic.A · Arc.A).
                // No Sat/Hue/Exp nodes (those are state-blend concerns;
                // the diagnostic is about geometry). Pattern matches the
                // shipping CreateConicArcStroke (no factory disposal; the
                // factory is short-lived and collected on GC).
                var effectGraph = new AlphaMaskEffect
                {
                    Source    = new CompositionEffectSourceParameter("Conic"),
                    AlphaMask = new CompositionEffectSourceParameter("Arc"),
                };
                var effectFactory = compositor.CreateEffectFactory(effectGraph);
                effectBrush = effectFactory.CreateBrush();
                effectBrush.SetSourceParameter("Conic", conicBrush);
                effectBrush.SetSourceParameter("Arc",   arcMaskBrush);
                sprite.Brush = effectBrush;
                break;
        }

        container.Children.InsertAtTop(sprite);

        return new NakedPreview(
            container, sprite,
            conicBrush, arcMaskBrush, effectBrush,
            conicSurface, arcMaskSurface,
            conicRotationProps, arcRotationProps);
    }

    // OKLCh → sRGB conversion. L and C are OKLab cylindrical
    // coordinates (Björn Ottosson, 2020). hTurns is the hue in turns
    // (0..1 = full wheel), matching the rest of the paint code.
    //
    // Why OKLCh — the OKLab colour space is designed to be
    // perceptually uniform. At a fixed L, every hue at the same C
    // reads as the same perceived lightness, so the conic wheel has
    // no brightness asymmetry — yellow at the top is not lighter
    // than blue at the bottom. This is the asymmetry HSV exhibits
    // (Rec. 709 luminance for pure yellow ≈ 0.93, pure blue ≈ 0.07),
    // which was visible as a top/bottom gradient when the Saturation
    // effect pulled the stroke to greyscale.
    //
    // Pipeline: OKLCh → OKLab → linear sRGB → gamma-corrected sRGB.
    // Linear sRGB values may fall outside [0, 1] for L/C combinations
    // that exit the sRGB gamut (yellows and blues at L=0.75 start
    // clipping around C ≈ 0.18). We clamp to [0, 1] on the gamma
    // output — that reads as a gentle flattening of the out-of-gamut
    // hues rather than a hard stop, which is good enough for a
    // rotating conic wheel where no individual hue lingers.
    private static Color OklchToRgb(float L, float C, float hTurns)
    {
        float hRad = hTurns * MathF.Tau;
        float a    = C * MathF.Cos(hRad);
        float b    = C * MathF.Sin(hRad);

        // OKLab → non-linear cone responses (Björn Ottosson's matrix).
        float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
        float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
        float s_ = L - 0.0894841775f * a - 1.2914855480f * b;

        // Cube to recover linear cone responses.
        float lc = l_ * l_ * l_;
        float mc = m_ * m_ * m_;
        float sc = s_ * s_ * s_;

        // Cone responses → linear sRGB.
        float rLin = +4.0767416621f * lc - 3.3077115913f * mc + 0.2309699292f * sc;
        float gLin = -1.2684380046f * lc + 2.6097574011f * mc - 0.3413193965f * sc;
        float bLin = -0.0041960863f * lc - 0.7034186147f * mc + 1.7076147010f * sc;

        return Color.FromArgb(
            0xFF,
            (byte)MathF.Round(Math.Clamp(LinearToSrgb(rLin), 0f, 1f) * 255f),
            (byte)MathF.Round(Math.Clamp(LinearToSrgb(gLin), 0f, 1f) * 255f),
            (byte)MathF.Round(Math.Clamp(LinearToSrgb(bLin), 0f, 1f) * 255f));
    }

    // sRGB OETF (IEC 61966-2-1). Handles the linear toe for small
    // values so the curve stays continuous at the piecewise seam.
    // Negative inputs are mirrored through the curve — produces a
    // symmetric result that clamps cleanly to 0 afterwards.
    private static float LinearToSrgb(float x)
    {
        if (x < 0f) return -LinearToSrgb(-x);
        return x <= 0.0031308f
            ? 12.92f * x
            : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }

}
