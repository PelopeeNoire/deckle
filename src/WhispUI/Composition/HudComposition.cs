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
// clipping at the rounded corners. CornerRadius follows the inset
// (OverlayCornerRadius = 8 dip → 7 dip for the geometry).
internal static class HudComposition
{
    private const float  StrokeThickness           = 1f;
    private const float  InsetDip                  = 1f;
    private const float  CornerRadiusDip           = 7f;
    private const double TranscribingPeriodSeconds = 3.0;
    private const double RewritingPeriodSeconds    = 4.0;

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
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0xFF, 0x75, 0x75, 0x75)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF)));

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
    // surface centre. Each wedge's colour is a linear RGB lerp between two
    // adjacent brand colours (8 stops, 45° each), which preserves the exact
    // hardcoded palette instead of falling back to an HSV sweep.
    //
    // Rotation uses ExpressionAnimation because TransformMatrix is a
    // Matrix3x2 with no built-in KeyFrameAnimation type. A scalar Angle on a
    // CompositionPropertySet drives a standard 0 → 2π keyframe animation, and
    // the matrix is rebuilt every frame by an expression that rotates around
    // (halfW, halfH) of the source surface.
    internal static ContainerVisual CreateRewritingStroke(
        Compositor compositor, Vector2 hostSize)
    {
        var arcColors = new[]
        {
            Color.FromArgb(0xFF, 0xFF, 0x00, 0x00), // red
            Color.FromArgb(0xFF, 0xFF, 0xBF, 0x00), // amber
            Color.FromArgb(0xFF, 0x80, 0xFF, 0x00), // lime
            Color.FromArgb(0xFF, 0x00, 0xFF, 0x40), // green
            Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF), // cyan
            Color.FromArgb(0xFF, 0x00, 0x40, 0xFF), // blue
            Color.FromArgb(0xFF, 0x80, 0x00, 0xFF), // violet
            Color.FromArgb(0xFF, 0xFF, 0x00, 0xBF), // magenta
        };

        var container = compositor.CreateContainerVisual();
        container.Size = hostSize;

        var innerSize = new Vector2(hostSize.X - 2f * InsetDip, hostSize.Y - 2f * InsetDip);
        int pxW = Math.Max(1, (int)MathF.Ceiling(innerSize.X));
        int pxH = Math.Max(1, (int)MathF.Ceiling(innerSize.Y));

        var canvasDevice   = CanvasDevice.GetSharedDevice();
        var graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);
        var surface        = graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(pxW, pxH),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            var centre = new Vector2(pxW / 2f, pxH / 2f);
            float radius = new Vector2(pxW, pxH).Length();
            const int wedges = 360;
            float step = MathF.Tau / wedges;

            for (int i = 0; i < wedges; i++)
            {
                float a0  = i * step;
                float a1  = a0 + step;
                float mid = a0 + step * 0.5f;

                float hueSeg = mid / MathF.Tau * arcColors.Length;
                int idx      = (int)MathF.Floor(hueSeg) % arcColors.Length;
                float t      = hueSeg - MathF.Floor(hueSeg);
                var color    = LerpColor(arcColors[idx], arcColors[(idx + 1) % arcColors.Length], t);

                var p0 = centre;
                var p1 = centre + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
                var p2 = centre + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;

                using var wedge = CanvasGeometry.CreatePolygon(canvasDevice, new[] { p0, p1, p2 });
                ds.FillGeometry(wedge, color);
            }
        }

        var sourceBrush = compositor.CreateSurfaceBrush(surface);
        sourceBrush.Stretch = CompositionStretch.Fill;

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

        // Rotation around the surface centre via the composite
        //   T(-c) · R(θ) · T(+c)
        // expressed in Composition's Matrix3x2 helpers. CreateRotation takes
        // radians; row-vector convention means translations flank the rotation
        // symmetrically.
        float halfW = pxW / 2f;
        float halfH = pxH / 2f;

        var matrixExpr = compositor.CreateExpressionAnimation(
            "Matrix3x2.CreateTranslation(negCentre) * " +
            "Matrix3x2.CreateRotation(props.Angle) * " +
            "Matrix3x2.CreateTranslation(posCentre)");
        matrixExpr.SetReferenceParameter("props", rotationProps);
        matrixExpr.SetVector2Parameter("negCentre", new Vector2(-halfW, -halfH));
        matrixExpr.SetVector2Parameter("posCentre", new Vector2( halfW,  halfH));
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

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
