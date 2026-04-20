using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;

namespace WhispUI.Composition;

// Variant the processing stroke is rendering. HudChrono picks one based
// on the HUD state (Transcribing / Rewriting); the live stroke animates
// its own effect properties toward the matching variant values without
// rebuilding anything.
internal enum ProcessingVariant { Transcribing, Rewriting }

// HUD Composition pipeline — processing strokes for the chrono surface.
//
// Strictly internal: every visual produced here stays inside the 272x78 HUD
// rect. The HUD window runs with WS_EX_LAYERED for proximity fade (which
// disables the DWM shell shadow) and the rect is a tight fit around the
// card — any external DropShadow would clip at the HWND edge, producing a
// rectangular artifact. No shadows in this pipeline.
//
// Geometry is inset by 1 dip so the 1-dip centered stroke sits between
// 0.5 dip and 1.5 dip from the outer edge — fully inside the DWM rounded
// silhouette (which clips at the 8-dip corner), zero risk of partial
// clipping at the rounded corners. CornerRadius follows the inset, tuned
// slightly under the geometric ideal (6.5 dip instead of 7) to keep the
// Win2D-rasterised stroke clear of the DWM corner — Win2D and DWM do not
// use identical antialiasing at high-curvature corners and a literal
// match produced a visible bleed.
internal static class HudComposition
{
    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Shared geometry                                                   ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // Fixed for both states — stroke metrics are a property of the HUD
    // rect, not of the animation.
    private const float  StrokeThickness              = 4f;    // dip, stroke width
    private const float  InsetDip                     = 4f;    // dip, inset from HUD edge
    private const float  CornerRadiusDip              = 7f;    // dip, rounded-rect corner radius

    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Processing stroke — single visual, live-modulated variants        ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // One stroke is created on first entry into a processing state and
    // kept alive across Transcribing ↔ Rewriting. Per-state differentiation
    // runs LIVE on the same visual via Composition effects (SaturationEffect,
    // HueRotationEffect, ExposureEffect on the colour pipeline + Opacity on
    // the visual). ProcessingStroke.ApplyVariant blends toward the target
    // values over BlendSeconds — no surface rebuild, no GC, no lag.
    //
    // All knobs live on ConicArcStrokeConfig below, split into three blocks:
    //   1. "Baseline palette / rotations" — paint-time config, applies to
    //      both variants (HsvSaturation, HueRange, ConicSpan, ArcPeriod…).
    //   2. Runtime variants (Rewriting* / Transcribing*) — the live knobs
    //      animated by ApplyVariant. Edit these to shape each state's look.
    //   3. BlendSeconds — per-variant transition duration.

    // ── Lexicon — vocabulary shared across the struct fields below ──────
    //
    // Paint-time (HSV conic palette baked once into a surface):
    //   HsvSaturation  0 = greyscale, 1 = full rainbow. Pastel in between.
    //   HsvValue       0 = black, 1 = full luminance. Affects readability
    //                  against the substrate — lower if Light theme
    //                  greyscale disappears into LayerFillColor.
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
    //   EaseP1/P2      cubic-bezier control points. (0,0,1,1) = linear,
    //                  (0.42,0,0.58,1) = standard ease-in-out, sharper
    //                  curves give bigger speed contrast.
    //   VelocityFloor  minimum angular velocity even on the eased
    //                  plateaus. See StartRotation header for the full
    //                  UX rationale. Hue can ease freely (the eye does
    //                  not track the colour wheel directly); the arc is
    //                  the silhouette the eye tracks, so keep the arc
    //                  floor higher so a "freeze" never reads as a bug.
    //
    // Runtime variant knobs (live properties on the SINGLE kept-alive
    // stroke — SaturationEffect, HueRotationEffect, ExposureEffect on the
    // colour pipeline, plus SpriteVisual.Opacity — animated by
    // ApplyVariant. Switching variants is a property animation on the
    // same GPU resources — no surface rebuild, no GC, no lag):
    //   Saturation     multiplier on the baked conic. 0 = greyscale,
    //                  1 = baseline colour. Combines with HsvSaturation.
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
        public float  HsvSaturation      { get; init; } = 1f;
        public float  HsvValue           { get; init; } = 1f;
        public float  HueStart           { get; init; } = 0f;
        public float  HueRange           { get; init; } = 1f;
        public int    WedgeCount         { get; init; } = 360;

        // ── Hue rotation — spins the conic under the fixed arc mask,
        //    so the colour at the arc head walks the wheel over time ─────
        public double HuePeriodSeconds   { get; init; } = 8.0;
        public float  HueDirection       { get; init; } = 1f;
        public float  HuePhaseTurns      { get; init; } = 0f;
        public float  HueEaseP1X         { get; init; } = 0.2f;
        public float  HueEaseP1Y         { get; init; } = 0f;
        public float  HueEaseP2X         { get; init; } = 0.8f;
        public float  HueEaseP2Y         { get; init; } = 1f;
        public float  HueVelocityFloor   { get; init; } = 0f;

        // ── Arc mask shape (white pie slice, alpha-ramped at both ends) ─
        public float  ConicSpanTurns     { get; init; } = 0.4f;
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
        public float  ArcEaseP1X         { get; init; } = 0.5f;
        public float  ArcEaseP1Y         { get; init; } = 0f;
        public float  ArcEaseP2X         { get; init; } = 0.2f;
        public float  ArcEaseP2Y         { get; init; } = 1f;
        public float  ArcVelocityFloor   { get; init; } = 0f;

        // ── Rewriting variant — target values for the live effect
        //    pipeline. Baseline neutrals leave the baked palette alone ──
        public float  RewritingSaturation       { get; init; } = 1f;
        public float  RewritingHueShiftTurns    { get; init; } = 0f;
        public float  RewritingExposure         { get; init; } = 0f;
        public float  RewritingOpacity          { get; init; } = 1f;
        public double RewritingBlendSeconds     { get; init; } = 1;

        // ── Transcribing variant — greyscale (Saturation 0) by default.
        //    Saturation + Exposure are split Dark/Light because the baked
        //    palette is at HsvValue=1 (near-white), so on Light the
        //    greyscale has to be pulled down by negative exposure to stay
        //    visible against LayerFillColor. HueShift/Opacity stay
        //    unified — widen later if per-theme control is needed ───────
        public float  TranscribingSaturationDark  { get; init; } = 0f;
        public float  TranscribingSaturationLight { get; init; } = 0f;
        public float  TranscribingHueShiftTurns   { get; init; } = 0f;
        public float  TranscribingExposureDark    { get; init; } = 0f;
        public float  TranscribingExposureLight   { get; init; } = 0f;
        public float  TranscribingOpacity         { get; init; } = 1f;
        public double TranscribingBlendSeconds    { get; init; } = 1;
    }

    // Live handle to a processing stroke created by CreateProcessingStroke.
    //
    // Wraps the ContainerVisual (attach point for XAML) and the animable
    // CompositionPropertySet that drives the SaturationEffect /
    // HueRotationEffect / ExposureEffect properties on the live pipeline.
    // ApplyVariant blends the PropertySet scalars + SpriteVisual.Opacity
    // from their current values to the variant targets over BlendSeconds
    // — no surface rebuild, no lag.
    internal sealed class ProcessingStroke : IDisposable
    {
        public ContainerVisual Visual { get; }

        private readonly Compositor _compositor;
        private readonly CompositionPropertySet _effectProps;
        private readonly SpriteVisual _strokeVisual;
        private readonly ConicArcStrokeConfig _config;

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
            ConicArcStrokeConfig config)
        {
            Visual       = visual;
            _compositor  = compositor;
            _effectProps = effectProps;
            _strokeVisual = strokeVisual;
            _config      = config;
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
            AnimateScalar(_strokeVisual, "Opacity",    opacity,         duration);
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

        public void Dispose() => Visual.Dispose();
    }

    // Processing stroke — single rainbow double-comet shared by the
    // Transcribing and Rewriting states. The returned ProcessingStroke
    // exposes ApplyVariant(…) for the live state blend.
    //
    // Struct defaults on ConicArcStrokeConfig ARE the whole config.
    // Tweak the defaults to iterate on the visual.
    internal static ProcessingStroke CreateProcessingStroke(
        Compositor compositor, Vector2 hostSize)
    {
        return CreateConicArcStroke(compositor, hostSize, new ConicArcStrokeConfig());
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
    private static ProcessingStroke CreateConicArcStroke(
        Compositor compositor, Vector2 hostSize, ConicArcStrokeConfig cfg)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        int pxW = Math.Max(1, (int)MathF.Ceiling(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Ceiling(innerSize.Y));
        // Rotating surfaces need side ≥ visual diagonal so the inscribed
        // circle of the source covers all four visual corners at every
        // rotation angle — cf. header comment.
        int pxSquare = (int)Math.Ceiling(Math.Sqrt((double)pxW * pxW + (double)pxH * pxH));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);

        // ── Surface 1: conic rainbow (full 360°, no arc carving here) ────
        var conicSurface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxSquare, pxSquare),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(conicSurface))
        {
            ds.Clear(Colors.Transparent);
            var centre = new Vector2(pxSquare / 2f, pxSquare / 2f);
            float radius = pxSquare * MathF.Sqrt(2f) * 0.5f;
            int wedges = Math.Max(3, cfg.WedgeCount);
            float step = MathF.Tau / wedges;

            for (int i = 0; i < wedges; i++)
            {
                float a0  = i * step;
                float a1  = a0 + step;
                float mid = a0 + step * 0.5f;

                float hue = cfg.HueStart + (mid / MathF.Tau) * cfg.HueRange;
                var color = HsvToRgb(hue, cfg.HsvSaturation, cfg.HsvValue);

                var p0 = centre;
                var p1 = centre + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
                var p2 = centre + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;

                using var wedge = CanvasGeometry.CreatePolygon(canvasDevice, new[] { p0, p1, p2 });
                ds.FillGeometry(wedge, color);
            }
        }

        // ── Surface 2: arc mask (white pie slice, fade at both ends) ─────
        // Painted with straight alpha; Win2D premultiplies on write into a
        // Premultiplied surface. Colour channels are full white so the
        // mask's luminance does not tint the conic — only its alpha drives
        // the AlphaMaskEffect output. When cfg.ArcMirror is true, the
        // same arc is painted a second time at +π (180°) inside the same
        // surface, so both copies rotate together and stay in perfect
        // symmetry.
        var arcMaskSurface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxSquare, pxSquare),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(arcMaskSurface))
        {
            ds.Clear(Colors.Transparent);
            var centre = new Vector2(pxSquare / 2f, pxSquare / 2f);
            float radius = pxSquare * MathF.Sqrt(2f) * 0.5f;
            int wedges = Math.Max(3, cfg.WedgeCount);
            float step = MathF.Tau / wedges;

            // Mirror: max Span is 0.5 so the two arcs can't overlap.
            // Without mirror: Span can go up to 1 (full ring).
            float maxSpanTurns  = cfg.ArcMirror ? 0.5f : 1f;
            float spanTurns     = Math.Clamp(cfg.ConicSpanTurns, 0f, maxSpanTurns);
            float leadFadeTurns = Math.Clamp(cfg.ConicLeadFadeTurns, 0f, spanTurns);
            float tailFadeTurns = Math.Clamp(cfg.ConicTailFadeTurns, 0f, spanTurns);
            // If the two fades would overlap past the span, scale both
            // so they just meet at the mid of the arc (bell shape, no
            // solid core). Otherwise preserve the user-requested lengths.
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

            // One branch = one arc painted. Two branches when mirror is
            // enabled — the second offset by π (180°) from the first.
            int branchCount = cfg.ArcMirror ? 2 : 1;

            for (int branch = 0; branch < branchCount; branch++)
            {
                float branchOffset = branch * MathF.PI;

                for (int i = 0; i < wedges; i++)
                {
                    float a0  = i * step;
                    float a1  = a0 + step;
                    float mid = a0 + step * 0.5f;

                    if (mid >= spanRadians) break;

                    float alpha;
                    if (leadFadeRadians > 0f && mid < leadFadeRadians)
                    {
                        // Leading ramp: 0 at a=0, 1 at a=LeadFade.
                        float t = mid / leadFadeRadians;
                        alpha = MathF.Pow(t, curve);
                    }
                    else if (tailFadeRadians > 0f && mid >= tailStartRadians)
                    {
                        // Trailing ramp: 1 at a=Span-TailFade, 0 at a=Span.
                        float t = (mid - tailStartRadians) / tailFadeRadians;
                        alpha = MathF.Pow(1f - t, curve);
                    }
                    else
                    {
                        alpha = 1f;
                    }
                    if (alpha <= 0f) continue;

                    var color = Color.FromArgb((byte)MathF.Round(alpha * 255f), 255, 255, 255);

                    // Apply the branch offset only to the drawn geometry.
                    // The alpha profile is computed on un-offset angles
                    // so both branches share the exact same shape.
                    float d0 = a0 + branchOffset;
                    float d1 = a1 + branchOffset;
                    var p0 = centre;
                    var p1 = centre + new Vector2(MathF.Cos(d0), MathF.Sin(d0)) * radius;
                    var p2 = centre + new Vector2(MathF.Cos(d1), MathF.Sin(d1)) * radius;

                    using var wedge = CanvasGeometry.CreatePolygon(canvasDevice, new[] { p0, p1, p2 });
                    ds.FillGeometry(wedge, color);
                }
            }
        }

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

        StartRotation(
            compositor, conicBrush, visualCentre,
            cfg.HuePeriodSeconds,
            cfg.HueDirection,
            cfg.HuePhaseTurns,
            cfg.HueEaseP1X, cfg.HueEaseP1Y,
            cfg.HueEaseP2X, cfg.HueEaseP2Y,
            cfg.HueVelocityFloor);

        StartRotation(
            compositor, arcMaskBrush, visualCentre,
            cfg.ArcPeriodSeconds,
            cfg.ArcDirection,
            cfg.ArcPhaseTurns,
            cfg.ArcEaseP1X, cfg.ArcEaseP1Y,
            cfg.ArcEaseP2X, cfg.ArcEaseP2Y,
            cfg.ArcVelocityFloor);

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
        // Initial effect values use the Transcribing (Dark) baseline. The
        // stroke is only ever created on the first enter into a processing
        // state, and that state is always Transcribing (Rewriting can only
        // follow a prior Transcribing). Seeding Rewriting values here caused
        // a visible rainbow flash on cold start before the first ApplyVariant
        // blend reached greyscale. Using Transcribing Dark saturation=0 keeps
        // the first paint frame already greyscale; any theme/exposure mismatch
        // is corrected instantly by the ApplyVariant call that follows attach.
        var saturationEffect = new SaturationEffect
        {
            Name       = "Sat",
            Saturation = cfg.TranscribingSaturationDark,
            Source     = new CompositionEffectSourceParameter("Conic"),
        };
        var hueEffect = new HueRotationEffect
        {
            Name   = "Hue",
            Angle  = cfg.TranscribingHueShiftTurns * MathF.Tau,
            Source = saturationEffect,
        };
        var exposureEffect = new ExposureEffect
        {
            Name     = "Exp",
            Exposure = cfg.TranscribingExposureDark,
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
        effectProps.InsertScalar("Saturation", cfg.TranscribingSaturationDark);
        effectProps.InsertScalar("HueAngle",   cfg.TranscribingHueShiftTurns * MathF.Tau);
        effectProps.InsertScalar("Exposure",   cfg.TranscribingExposureDark);

        BindEffectProperty(compositor, effectBrush, "Sat.Saturation", effectProps, "Saturation");
        BindEffectProperty(compositor, effectBrush, "Hue.Angle",      effectProps, "HueAngle");
        BindEffectProperty(compositor, effectBrush, "Exp.Exposure",   effectProps, "Exposure");

        var strokeVisual = compositor.CreateSpriteVisual();
        strokeVisual.Size    = innerSize;
        strokeVisual.Offset  = new Vector3(InsetDip, InsetDip, 0f);
        strokeVisual.Brush   = effectBrush;
        strokeVisual.Opacity = cfg.TranscribingOpacity;

        container.Children.InsertAtTop(strokeVisual);
        return new ProcessingStroke(container, compositor, effectProps, strokeVisual, cfg);
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

    // ── Rotation with velocity floor ─────────────────────────────────────
    //
    // What it does (UX):
    //   The rotations breathe — slow, fast, slow — via a cubic-bezier
    //   easing curve. With a strong easing (e.g. (0.5, 0, 0.2, 1)), the
    //   curve lingers so long at near-zero derivative around the start
    //   and end of each cycle that the eye reads it as "the animation
    //   broke" rather than "it's slowing down". The floor blends in a
    //   constant linear rotation so motion never fully stops, even at
    //   the easing plateaus.
    //
    // Floor values, perceived effect:
    //   0.0   pure easing — may visibly freeze (broken feel).
    //   0.3   easing dominant, never below ~30% of mean speed.
    //   0.6   mostly steady, easing adds a gentle pulse.
    //   1.0   pure linear — no easing at all, robotic.
    //
    // Side note on max velocity: the peak is determined by the floor and
    // the easing curve — `peak = floor + (1 - floor)·peakEasedVelocity`.
    // Higher floor = lower peak (compresses toward linear). For faster
    // peaks at constant min, shorten the period instead.
    //
    // How it works (implementation):
    //   Two scalars on the same PropertySet animate over the period —
    //   `Linear` (no easing function = linear interpolation) and `Eased`
    //   (cubic-bezier). An ExpressionAnimation rebuilds the Matrix3x2
    //   every frame around the visual centre with the angle computed as:
    //
    //       angle = Linear·floor + Eased·(1 - floor)
    //
    //   Both scalars start at startAngle and end at endAngle over the
    //   same period, so the sum is in [startAngle, endAngle] at all
    //   times and lands exactly on endAngle at period end — the loop
    //   closes seamlessly with no phase drift across iterations.
    //
    //   We sum instead of reshaping the easing because Composition's
    //   CubicBezierEasingFunction only exposes the two interior control
    //   points — the endpoints are fixed at (0,0) and (1,1), so we can't
    //   lift the start derivative directly. The linear+eased sum gives
    //   the same guarantee (min velocity = floor·meanVelocity) without
    //   fighting the API.
    private static void StartRotation(
        Compositor compositor,
        CompositionSurfaceBrush brush,
        Vector2 visualCentre,
        double periodSeconds,
        float direction,
        float phaseTurns,
        float easeP1X, float easeP1Y,
        float easeP2X, float easeP2Y,
        float floor)
    {
        float startAngle = MathF.Tau * phaseTurns;
        float fullAngle  = MathF.Tau * direction;
        float endAngle   = startAngle + fullAngle;

        var props = compositor.CreatePropertySet();
        props.InsertScalar("Linear", startAngle);
        props.InsertScalar("Eased",  startAngle);

        var duration = TimeSpan.FromSeconds(periodSeconds);

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

        // Floor mixes the two: pure easing at floor=0, pure linear at
        // floor=1. Clamped defensively so a stray config value can't
        // invert the rotation or amplify it past the unit interval.
        float clampedFloor = Math.Clamp(floor, 0f, 1f);

        // CRITICAL — Composition's expression language is NOT C#. Numeric
        // literals are written without any suffix: `1.0` is a Float, `1`
        // is an Int. A C# `1.0f` would be parsed as `1.0 * f` with `f` as
        // a missing variable (default 0), turning `(1.0f - floor)` into
        // `-floor` and the whole expression into a yo-yo motion. Stay
        // strict on the literal syntax here.
        var matrixExpr = compositor.CreateExpressionAnimation(
            "Matrix3x2.CreateTranslation(negCentre) * " +
            "Matrix3x2.CreateRotation(props.Linear * floor + props.Eased * (1.0 - floor)) * " +
            "Matrix3x2.CreateTranslation(posCentre)");
        matrixExpr.SetReferenceParameter("props", props);
        matrixExpr.SetVector2Parameter("negCentre", -visualCentre);
        matrixExpr.SetVector2Parameter("posCentre",  visualCentre);
        matrixExpr.SetScalarParameter ("floor",      clampedFloor);
        brush.StartAnimation("TransformMatrix", matrixExpr);
    }

    // HSV → RGB conversion (h, s, v in [0, 1]). Continuous derivative at
    // h = 0 / h = 1 wrap, which is the whole point of using this instead of
    // an RGB lerp over a closed palette.
    private static Color HsvToRgb(float h, float s, float v)
    {
        h = ((h % 1f) + 1f) % 1f;
        float c       = v * s;
        float hPrime  = h * 6f;
        float x       = c * (1f - MathF.Abs((hPrime % 2f) - 1f));
        float r, g, b;
        if      (hPrime < 1f) { r = c; g = x; b = 0f; }
        else if (hPrime < 2f) { r = x; g = c; b = 0f; }
        else if (hPrime < 3f) { r = 0f; g = c; b = x; }
        else if (hPrime < 4f) { r = 0f; g = x; b = c; }
        else if (hPrime < 5f) { r = x; g = 0f; b = c; }
        else                  { r = c; g = 0f; b = x; }
        float m = v - c;
        return Color.FromArgb(
            0xFF,
            (byte)MathF.Round((r + m) * 255f),
            (byte)MathF.Round((g + m) * 255f),
            (byte)MathF.Round((b + m) * 255f));
    }

    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  Recording outline — single theme-coloured stroke, opacity-driven  ║
    // ╚════════════════════════════════════════════════════════════════════╝
    //
    // Attached to HudChrono.ProcessingSurfaceHost only during the Recording
    // state. Its single animable channel is SpriteVisual.Opacity, bound to
    // a PropertySet scalar "Level" via an ExpressionAnimation — the
    // HudChrono consumer pushes EMA-smoothed mic RMS into Level, and the
    // Composition renderthread (vsynced to the monitor refresh) interpolates
    // between 20 Hz samples via short 50 ms KeyFrameAnimations. No C#-side
    // framerate is fixed: 60 Hz and 240 Hz monitors both get a continuous
    // ramp without code change.
    //
    // Detached before a ProcessingStroke (Transcribing / Rewriting) is
    // attached on the same surface host — the two strokes never coexist,
    // so they share the inset / radius / silhouette metrics for pixel
    // compatibility but their attach is mutually exclusive.

    // Live handle returned by CreateRecordingOutline. Callers hold onto it
    // to push level updates (thread-safe from the recording thread) and to
    // recolour the stroke when the UI theme flips at runtime.
    internal sealed class RecordingOutline : IDisposable
    {
        public ContainerVisual Visual { get; }

        private readonly Compositor _compositor;
        private readonly CompositionPropertySet _props;
        private readonly CompositionColorBrush _colorBrush;

        internal RecordingOutline(
            ContainerVisual visual,
            Compositor compositor,
            CompositionPropertySet props,
            CompositionColorBrush colorBrush)
        {
            Visual      = visual;
            _compositor = compositor;
            _props      = props;
            _colorBrush = colorBrush;
        }

        // Push a new target level in [0, 1]. Clamped, then animated with a
        // 50 ms linear key-frame from the current value to the target.
        // InsertExpressionKeyFrame("this.CurrentValue") makes successive
        // overlapping calls blend naturally from wherever the previous
        // animation had reached — no reset to 0, no step discontinuity.
        //
        // Composition contracts CompositionPropertySet + StartAnimation as
        // thread-safe off the UI thread, so this can be called directly
        // from the recording audio thread without marshalling. The render
        // pipeline picks up the new animation at the next frame.
        public void UpdateLevel(float level)
        {
            float clamped = Math.Clamp(level, 0f, 1f);
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.InsertExpressionKeyFrame(0f, "this.CurrentValue");
            anim.InsertKeyFrame(1f, clamped);
            anim.Duration = TimeSpan.FromMilliseconds(50);
            _props.StartAnimation("Level", anim);
        }

        // Swap the stroke colour live. The caller resolves the new Color
        // from a theme brush (TextFillColorPrimaryBrush in practice) on
        // ActualThemeChanged and hands it in; the ColorBrush flip propagates
        // through the AlphaMaskEffect pipeline at the next frame.
        public void Retheme(Color color) => _colorBrush.Color = color;

        public void Dispose() => Visual.Dispose();
    }

    // Build a RecordingOutline for the HUD surface. `color` is the resolved
    // theme colour for the stroke (typically TextFillColorPrimary — white on
    // dark, black on light). Inset / corner radius / thickness mirror
    // CreateConicArcStroke so the two strokes paint on the exact same
    // silhouette.
    internal static RecordingOutline CreateRecordingOutline(
        Compositor compositor, Vector2 hostSize, Color color)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        int pxW = Math.Max(1, (int)MathF.Ceiling(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Ceiling(innerSize.Y));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);

        // Stroke silhouette — same geometry as CreateConicArcStroke (the
        // 15 lines are duplicated rather than factored into a shared helper
        // to keep the two pipelines decoupled; refactor only if a third
        // consumer appears).
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

        var strokeMaskBrush = compositor.CreateSurfaceBrush(strokeMaskSurface);
        strokeMaskBrush.Stretch = CompositionStretch.Fill;

        // Plain opaque colour source — Retheme() mutates .Color live.
        var colorBrush = compositor.CreateColorBrush(color);

        // AlphaMaskEffect: output = (color.RGB, color.A * strokeMask.A).
        // Non-silhouette pixels go transparent, silhouette pixels take the
        // theme colour. SpriteVisual.Opacity multiplies in on top.
        var effectGraph = new AlphaMaskEffect
        {
            Source    = new CompositionEffectSourceParameter("Color"),
            AlphaMask = new CompositionEffectSourceParameter("Stroke"),
        };
        var effectFactory = compositor.CreateEffectFactory(effectGraph);
        var effectBrush   = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("Color",  colorBrush);
        effectBrush.SetSourceParameter("Stroke", strokeMaskBrush);

        var strokeVisual = compositor.CreateSpriteVisual();
        strokeVisual.Size    = innerSize;
        strokeVisual.Offset  = new Vector3(InsetDip, InsetDip, 0f);
        strokeVisual.Brush   = effectBrush;
        strokeVisual.Opacity = 0f;

        // PropertySet scalar "Level" — the single live channel. Seeded at
        // 0 so the outline spawns invisible; the first EMA-smoothed RMS
        // push animates it up from there.
        var props = compositor.CreatePropertySet();
        props.InsertScalar("Level", 0f);

        // Bind SpriteVisual.Opacity to props.Level — one ExpressionAnimation
        // evaluated every Composition frame, no C# tick, no dispatcher.
        var opacityExpr = compositor.CreateExpressionAnimation("props.Level");
        opacityExpr.SetReferenceParameter("props", props);
        strokeVisual.StartAnimation("Opacity", opacityExpr);

        container.Children.InsertAtTop(strokeVisual);
        return new RecordingOutline(container, compositor, props, colorBrush);
    }
}
