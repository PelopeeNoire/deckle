using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
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
    // ── Tunables ──────────────────────────────────────────────────────────
    // Every value in this block drives visible rendering. Edit and rebuild.
    // The rest of the file is structural: wedge count, brush wiring,
    // composition math — change those only when revising the pipeline.

    // Shared stroke geometry.
    private const float  StrokeThickness              = 1f;    // dip, stroke width
    private const float  InsetDip                     = 1f;    // dip, inset from HUD edge
    private const float  CornerRadiusDip              = 6.5f;  // dip, rounded-rect corner radius

    // Transcribing shimmer — diagonal grey gradient rotating around the card.
    private const double TranscribingPeriodSeconds    = 3.0;   // seconds for one full turn
    private const byte   TranscribingGreyDark         = 0x75;  // low gradient stop (0x00..0xFF)
    private const byte   TranscribingGreyLight        = 0xBF;  // high gradient stop (0x00..0xFF)

    // Rewriting rainbow — conic stroke rotating around the card.
    private const double RewritingPeriodSeconds       = 4.0;   // seconds for one full turn
    private const float  RewritingHsvSaturation       = 1f;    // 0..1, HSV S (drop for pastel)
    private const float  RewritingHsvValue            = 1f;    // 0..1, HSV V (drop for darker)
    // Alpha fade of the source disc — the conic is fully opaque inside the
    // core, then ramps to RewritingEdgeAlpha at the outer radius.
    //   RewritingAlphaCorePct   — 0..1, core radius as fraction of the
    //                             fade radius below. Lower → fade starts
    //                             sooner (tighter colour concentration).
    //   RewritingFadeRadiusPct  — fade outer radius as fraction of
    //                             pxSquare/2 (= 136 px at the 272-dip HUD).
    //                             <1 tightens the coloured region,
    //                             >1 softens it.
    //   RewritingEdgeAlpha      — 0..255, alpha at the outer radius. 0 is
    //                             fully transparent; raise to keep some
    //                             colour at the extremities.
    private const float  RewritingAlphaCorePct        = 0.6f;
    private const float  RewritingFadeRadiusPct       = 1f;
    private const byte   RewritingEdgeAlpha           = 0;

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
    // The source surface is SQUARE (pxSquare × pxSquare with
    // pxSquare = max(pxW, pxH)) rather than matching the visual rect. Two
    // reasons:
    //   1. Under rotation, a rectangular source that matches the elongated
    //      visual can't cover the visual at all angles — at 90° the rotated
    //      source's short dimension leaves horizontal tails untouched and
    //      those pixels sample outside the source bounds → transparent
    //      gaps that breathe as the source rotates.
    //   2. The radial alpha fade becomes a true circle (RadiusX = RadiusY),
    //      which is rotation-invariant: the visible alpha distribution
    //      stays identical at every angle. No more perceived acceleration
    //      from a rotating elliptical mask.
    // The brush uses CompositionStretch.UniformToFill so the square source
    // is centred in the visual and its short dimension scales to cover the
    // visual width — the visible rect is a horizontal slice through the
    // full square, within which the circular fade only touches the
    // horizontal edges.
    //
    // Rotation uses ExpressionAnimation because TransformMatrix is a
    // Matrix3x2 with no built-in KeyFrameAnimation type. A scalar Angle on a
    // CompositionPropertySet drives a standard 0 → 2π keyframe animation, and
    // the matrix is rebuilt every frame by an expression that rotates around
    // the source centre (pxSquare/2, pxSquare/2).
    internal static ContainerVisual CreateRewritingStroke(
        Compositor compositor, Vector2 hostSize)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        int pxW = Math.Max(1, (int)MathF.Ceiling(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Ceiling(innerSize.Y));
        int pxSquare = Math.Max(pxW, pxH);

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
            const int wedges = 360;
            float step = MathF.Tau / wedges;

            // Circular opacity mask — RadiusX == RadiusY, so the alpha
            // distribution is rotation-invariant. Opaque inside the inner
            // core (RewritingAlphaCorePct of the fade radius), linear ramp
            // to RewritingEdgeAlpha at the outer radius
            // (RewritingFadeRadiusPct × pxSquare/2).
            //
            // Win2D's CanvasBlend enum has no DestinationIn, so the mask is
            // applied upstream via CreateLayer(ICanvasBrush) — every draw
            // call inside the layer scope is multiplied by the brush alpha.
            float fadeRadius = pxSquare / 2f * RewritingFadeRadiusPct;
            var fadeStops = new[]
            {
                new CanvasGradientStop { Position = 0f,                    Color = Colors.White },
                new CanvasGradientStop { Position = RewritingAlphaCorePct, Color = Colors.White },
                new CanvasGradientStop { Position = 1f,                    Color = Color.FromArgb(RewritingEdgeAlpha, 0xFF, 0xFF, 0xFF) },
            };
            using var radial = new CanvasRadialGradientBrush(canvasDevice, fadeStops)
            {
                Center  = centre,
                RadiusX = fadeRadius,
                RadiusY = fadeRadius,
            };

            using (ds.CreateLayer(radial))
            {
                for (int i = 0; i < wedges; i++)
                {
                    float a0  = i * step;
                    float a1  = a0 + step;
                    float mid = a0 + step * 0.5f;

                    var color = HsvToRgb(mid / MathF.Tau, RewritingHsvSaturation, RewritingHsvValue);

                    var p0 = centre;
                    var p1 = centre + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
                    var p2 = centre + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;

                    using var wedge = CanvasGeometry.CreatePolygon(canvasDevice, new[] { p0, p1, p2 });
                    ds.FillGeometry(wedge, color);
                }
            }
        }

        var sourceBrush = compositor.CreateSurfaceBrush(surface);
        sourceBrush.Stretch = CompositionStretch.UniformToFill;

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

        var rotationProps = compositor.CreatePropertySet();
        rotationProps.InsertScalar("Angle", 0f);

        var angleAnim = compositor.CreateScalarKeyFrameAnimation();
        angleAnim.InsertKeyFrame(0f, 0f);
        angleAnim.InsertKeyFrame(1f, MathF.Tau);
        angleAnim.Duration          = TimeSpan.FromSeconds(RewritingPeriodSeconds);
        angleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        rotationProps.StartAnimation("Angle", angleAnim);

        // Rotation around the source centre via the
        //   T(-c) · R(θ) · T(+c)
        // composite, expressed in Composition's Matrix3x2 helpers. The
        // square source already covers every rotation angle, so no scale
        // factor is needed. CreateRotation takes radians; row-vector
        // convention means translations flank the rotation symmetrically.
        float halfSquare = pxSquare / 2f;

        var matrixExpr = compositor.CreateExpressionAnimation(
            "Matrix3x2.CreateTranslation(negCentre) * " +
            "Matrix3x2.CreateRotation(props.Angle) * " +
            "Matrix3x2.CreateTranslation(posCentre)");
        matrixExpr.SetReferenceParameter("props", rotationProps);
        matrixExpr.SetVector2Parameter("negCentre", new Vector2(-halfSquare, -halfSquare));
        matrixExpr.SetVector2Parameter("posCentre", new Vector2( halfSquare,  halfSquare));
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
