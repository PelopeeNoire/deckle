using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;

namespace WhispUI.Composition;

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
    // ║  Tunables                                                          ║
    // ╚════════════════════════════════════════════════════════════════════╝
    // Every value in this block drives visible rendering. Edit and rebuild.
    // The rest of the file is structural (brush wiring, composition math)
    // — change those only when revising the pipeline itself.
    //
    // Grouped by subsystem. This whole block is expected to shrink once
    // the visual design is locked — tunables that no longer need live
    // iteration will migrate back into the rendering code or to a config
    // file. For now, maximum surface for iteration.

    // ── Shared stroke geometry ────────────────────────────────────────────
    private const float  StrokeThickness              = 1f;    // dip, stroke width
    private const float  InsetDip                     = 1f;    // dip, inset from HUD edge
    private const float  CornerRadiusDip              = 7f;    // dip, rounded-rect corner radius

    // ── Transcribing — linear grey shimmer ────────────────────────────────
    // Kept simple on purpose: this state is slated for architectural
    // unification with Rewriting (same Win2D conic pipeline, just
    // RewritingHsvSaturation = 0 for greyscale). Once that lands, these
    // tunables disappear and Transcribing reuses the Rewriting knobs.
    private const double TranscribingPeriodSeconds    = 4.0;   // seconds for one full turn
    private const byte   TranscribingGreyDark         = 0x65;  // low gradient stop (0x00..0xFF)
    private const byte   TranscribingGreyLight        = 0xDF;  // high gradient stop (0x00..0xFF)

    // ── Rewriting — colour palette ────────────────────────────────────────
    // HSV-based rainbow sweep, continuous at the 0/2π hue wrap.
    //   HsvSaturation / HsvValue — 0..1. Drop S for pastel, V for darker.
    //   HueStart                  — 0..1, offset in the colour wheel where
    //                               hue 0 sits. 0 = red at 3 o'clock,
    //                               0.33 = green at 3 o'clock, etc.
    //   HueRange                  — 0..1, fraction of the wheel the palette
    //                               covers. 1 = full rainbow, 0.5 = half
    //                               rainbow mirrored, 0.1 = nearly
    //                               monochrome. Combined with HueStart this
    //                               carves arbitrary palette slices (cool
    //                               tones only, warm tones only, etc.).
    private const float  RewritingHsvSaturation       = 1f;
    private const float  RewritingHsvValue            = 1f;
    private const float  RewritingHueStart            = 0f;
    private const float  RewritingHueRange            = 1f;

    // ── Rewriting — source sampling ───────────────────────────────────────
    //   WedgeCount — number of pie wedges painted to form the conic.
    //                360 = smooth, lower values give a chunky, stepped
    //                rainbow (try 24 or 12 for a retro look).
    private const int    RewritingWedgeCount          = 360;

    // ── Rewriting — base rotation ─────────────────────────────────────────
    // Single constant-rate spin. A secondary "burst" animation (accel/decel
    // overlaid on this base) is planned but not yet implemented.
    //   PeriodSeconds    — seconds per full turn.
    //   Direction        — +1 = screen-clockwise, -1 = CCW.
    //   PhaseTurns       — initial rotation as fraction of a full turn
    //                      (0..1). Shifts where the "red tip" sits at t=0.
    // Cubic bezier easing of the 0 → 2π keyframe. Four scalars are the
    // P1.X, P1.Y, P2.X, P2.Y of the curve. (0,0,1,1) = linear,
    // (0.42,0,0.58,1) = ease-in-out, (0,0,0.58,1) = ease-out,
    // (0.42,0,1,1) = ease-in. Because this animation loops forever, keep
    // the start and end tangents matched (symmetric bezier, e.g.
    // (a,b,1-a,1-b)) to avoid a visible jolt at the loop boundary.
    private const double RewritingPeriodSeconds       = 8.0;
    private const float  RewritingRotationDirection   = 1f;
    private const float  RewritingRotationPhaseTurns  = 0f;
    private const float  RewritingRotationEaseP1X     = 0f;
    private const float  RewritingRotationEaseP1Y     = 0f;
    private const float  RewritingRotationEaseP2X     = 1f;
    private const float  RewritingRotationEaseP2Y     = 1f;

    // ╔════════════════════════════════════════════════════════════════════╗
    // ║  End of tunables                                                   ║
    // ╚════════════════════════════════════════════════════════════════════╝

    // Diagonal gradient stroke for Transcribing, animated as a shimmer that
    // rotates around the card centre. StartPoint and EndPoint are animated
    // directly via Vector2KeyFrameAnimation — 5 keyframes tracing the four
    // cardinal directions around (0.5, 0.5) over TranscribingPeriodSeconds,
    // closing the loop with a fifth keyframe identical to the first. The
    // gradient axis therefore rotates through a full turn each period while
    // staying diametrically opposed through the centre.
    //
    // Neutral greys hardcoded for this first pass — theme-resource tracking
    // (candidates: ControlStrokeColorDefault → ControlStrokeColorSecondary
    // with ActualThemeChanged re-resolve) is a follow-up.
    internal static ContainerVisual CreateTranscribingStroke(
        Compositor compositor, Vector2 hostSize)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);

        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = new Vector2(1f, 0.5f);
        gradient.EndPoint   = new Vector2(0f, 0.5f);
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f,
            Color.FromArgb(0xFF, TranscribingGreyDark,  TranscribingGreyDark,  TranscribingGreyDark)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f,
            Color.FromArgb(0xFF, TranscribingGreyLight, TranscribingGreyLight, TranscribingGreyLight)));

        var period = TimeSpan.FromSeconds(TranscribingPeriodSeconds);

        var startAnim = compositor.CreateVector2KeyFrameAnimation();
        startAnim.InsertKeyFrame(0.00f, new Vector2(1f, 0.5f));
        startAnim.InsertKeyFrame(0.25f, new Vector2(0.5f, 1f));
        startAnim.InsertKeyFrame(0.50f, new Vector2(0f, 0.5f));
        startAnim.InsertKeyFrame(0.75f, new Vector2(0.5f, 0f));
        startAnim.InsertKeyFrame(1.00f, new Vector2(1f, 0.5f));
        startAnim.Duration          = period;
        startAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        gradient.StartAnimation("StartPoint", startAnim);

        var endAnim = compositor.CreateVector2KeyFrameAnimation();
        endAnim.InsertKeyFrame(0.00f, new Vector2(0f, 0.5f));
        endAnim.InsertKeyFrame(0.25f, new Vector2(0.5f, 0f));
        endAnim.InsertKeyFrame(0.50f, new Vector2(1f, 0.5f));
        endAnim.InsertKeyFrame(0.75f, new Vector2(0.5f, 1f));
        endAnim.InsertKeyFrame(1.00f, new Vector2(0f, 0.5f));
        endAnim.Duration          = period;
        endAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        gradient.StartAnimation("EndPoint", endAnim);

        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = innerSize;
        geometry.CornerRadius = new Vector2(CornerRadiusDip, CornerRadiusDip);

        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = gradient;
        shape.StrokeThickness = StrokeThickness;

        var strokeVisual = compositor.CreateShapeVisual();
        strokeVisual.Size   = innerSize;
        strokeVisual.Offset = new Vector3(InsetDip, InsetDip, 0f);
        strokeVisual.Shapes.Add(shape);

        container.Children.InsertAtTop(strokeVisual);
        return container;
    }

    // Rotating rainbow stroke for Rewriting. Composition has no conic-gradient
    // brush and CompositionLinearGradientBrush paints in bounding-box
    // coordinates (colour varies along a fixed screen axis, not along the
    // rounded-rect perimeter), so a true rainbow that walks around the stroke
    // needs Win2D.
    //
    // SpriteShape.StrokeBrush refuses any brush other than Color / Linear /
    // Radial gradient — no SurfaceBrush, MaskBrush, etc. So we don't stroke a
    // shape: we render the stroke silhouette into a mask surface, render the
    // conic rainbow into a source surface, and composite the two via a
    // CompositionMaskBrush on a plain SpriteVisual. The mask's alpha carves
    // out the stroke; the source fills it with colour.
    //
    // The conic gradient is painted as 360 pie wedges fanning out from the
    // surface centre. Each wedge's colour is HSV(hue, 1, 1) with
    // hue = angle / 2π — a continuous spectrum sweep whose derivative is
    // continuous at the 0/2π wrap, so no seam is visible as the rainbow
    // rotates. Palette-tuned variant (brand colours, uneven hue weighting)
    // is a follow-up once the base rendering is stable.
    //
    // The source surface is SQUARE and sized so its inscribed circle
    // contains the visual at every rotation: pxSquare = ceil(√(pxW² + pxH²))
    // = visual diagonal. Smaller squares (e.g. max(pxW, pxH)) leave the
    // visual corners outside the source at intermediate angles — those
    // pixels sample out of bounds and go transparent, producing gaps that
    // sweep around with rotation. The inscribed-circle criterion is the
    // exact minimum. The brush uses CompositionStretch.None so the source
    // is drawn 1:1 pixel centred in the visual (alignment ratios default
    // to 0.5), preserving its oversized brush-space footprint; any other
    // stretch mode rescales the source back down to the visual's extent
    // and defeats the coverage guarantee.
    //
    // Rotation uses ExpressionAnimation because TransformMatrix is a
    // Matrix3x2 with no built-in KeyFrameAnimation type. A scalar Angle on a
    // CompositionPropertySet drives a standard 0 → 2π keyframe animation, and
    // the matrix is rebuilt every frame by an expression that rotates around
    // the VISUAL centre (innerSize.X/2, innerSize.Y/2) — not the source
    // centre. CompositionSurfaceBrush.TransformMatrix is evaluated in the
    // coordinate space of the SpriteVisual the brush paints onto, AFTER
    // Stretch/alignment have placed the source. Stretch.None centres a
    // pxSquare×pxSquare source on the visual centre, so rotating around the
    // visual centre spins the centred source in place. Rotating around the
    // source centre (pxSquare/2, pxSquare/2) instead would orbit the source
    // around a point well outside the visual — exactly the "half the stroke
    // missing at most phases" symptom we hit.
    internal static ContainerVisual CreateRewritingStroke(
        Compositor compositor, Vector2 hostSize)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        int pxW = Math.Max(1, (int)MathF.Ceiling(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Ceiling(innerSize.Y));
        // Source side must be ≥ the visual diagonal so that the source's
        // inscribed circle contains all four corners of the visual at every
        // rotation. Using max(pxW, pxH) is insufficient: the visual's
        // half-diagonal (≈141.5 px for 272×78) exceeds that square's
        // half-side (136), so corners sample outside the source at
        // intermediate angles → transparent gaps that sweep with rotation.
        int pxSquare = (int)Math.Ceiling(Math.Sqrt((double)pxW * pxW + (double)pxH * pxH));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);
        var surface        = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxSquare, pxSquare),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            var centre = new Vector2(pxSquare / 2f, pxSquare / 2f);
            float radius = pxSquare * MathF.Sqrt(2f) * 0.5f;
            int wedges = Math.Max(3, RewritingWedgeCount);
            float step = MathF.Tau / wedges;

            for (int i = 0; i < wedges; i++)
            {
                float a0  = i * step;
                float a1  = a0 + step;
                float mid = a0 + step * 0.5f;

                float hue = RewritingHueStart + (mid / MathF.Tau) * RewritingHueRange;
                var color = HsvToRgb(hue, RewritingHsvSaturation, RewritingHsvValue);

                var p0 = centre;
                var p1 = centre + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
                var p2 = centre + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;

                using var wedge = CanvasGeometry.CreatePolygon(canvasDevice, new[] { p0, p1, p2 });
                ds.FillGeometry(wedge, color);
            }
        }

        // Stretch = None draws the source 1:1 pixel, centred in the visual
        // (HorizontalAlignmentRatio/VerticalAlignmentRatio default to 0.5).
        // UniformToFill would rescale the square so its long side matches the
        // visual's long side, collapsing a 283-px source back to 272 px in
        // brush space — which negates the whole point of oversizing it for
        // rotation coverage.
        var sourceBrush = compositor.CreateSurfaceBrush(surface);
        sourceBrush.Stretch = CompositionStretch.None;

        // Mask surface — a rounded-rect stroke painted opaque white on a
        // transparent background. Inset by 0.5 dip so the 1-dip stroke is
        // centred on the same path the ShapeVisual used to walk, keeping the
        // pixel-centre alignment with the DWM frame.
        var maskSurface = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxW, pxH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(maskSurface))
        {
            ds.Clear(Colors.Transparent);
            var rect = new Windows.Foundation.Rect(
                StrokeThickness / 2f,
                StrokeThickness / 2f,
                pxW - StrokeThickness,
                pxH - StrokeThickness);
            ds.DrawRoundedRectangle(rect, CornerRadiusDip, CornerRadiusDip, Colors.White, StrokeThickness);
        }

        var maskBrush = compositor.CreateSurfaceBrush(maskSurface);
        maskBrush.Stretch = CompositionStretch.Fill;

        // Rotation keyframe animation. Start angle = phase offset, end angle
        // = start + one full turn in the configured direction. Cubic bezier
        // easing on the end keyframe shapes the intra-period speed curve
        // (default linear). The animation loops forever; keep start and end
        // speeds matched (symmetric bezier) to avoid a visible jolt at the
        // loop boundary.
        float startAngle = MathF.Tau * RewritingRotationPhaseTurns;
        float endAngle   = startAngle + MathF.Tau * RewritingRotationDirection;

        var rotationProps = compositor.CreatePropertySet();
        rotationProps.InsertScalar("Angle", startAngle);

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(RewritingRotationEaseP1X, RewritingRotationEaseP1Y),
            new Vector2(RewritingRotationEaseP2X, RewritingRotationEaseP2Y));

        var angleAnim = compositor.CreateScalarKeyFrameAnimation();
        angleAnim.InsertKeyFrame(0f, startAngle);
        angleAnim.InsertKeyFrame(1f, endAngle, easing);
        angleAnim.Duration          = TimeSpan.FromSeconds(RewritingPeriodSeconds);
        angleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        rotationProps.StartAnimation("Angle", angleAnim);

        // Rotation around the VISUAL centre via the
        //   T(-c) · R(θ) · T(+c)
        // composite, expressed in Composition's Matrix3x2 helpers.
        // CreateRotation takes radians; row-vector convention means
        // translations flank the rotation symmetrically.
        // c = innerSize/2 because TransformMatrix is in SpriteVisual space
        // (post-Stretch/alignment), not in source pixel space.
        var visualCentre = new Vector2(innerSize.X / 2f, innerSize.Y / 2f);

        var matrixExpr = compositor.CreateExpressionAnimation(
            "Matrix3x2.CreateTranslation(negCentre) * " +
            "Matrix3x2.CreateRotation(props.Angle) * " +
            "Matrix3x2.CreateTranslation(posCentre)");
        matrixExpr.SetReferenceParameter("props", rotationProps);
        matrixExpr.SetVector2Parameter("negCentre", -visualCentre);
        matrixExpr.SetVector2Parameter("posCentre",  visualCentre);
        sourceBrush.StartAnimation("TransformMatrix", matrixExpr);

        var compositeBrush = compositor.CreateMaskBrush();
        compositeBrush.Mask   = maskBrush;
        compositeBrush.Source = sourceBrush;

        var strokeVisual = compositor.CreateSpriteVisual();
        strokeVisual.Size   = innerSize;
        strokeVisual.Offset = new Vector3(InsetDip, InsetDip, 0f);
        strokeVisual.Brush  = compositeBrush;

        container.Children.InsertAtTop(strokeVisual);
        return container;
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
}
