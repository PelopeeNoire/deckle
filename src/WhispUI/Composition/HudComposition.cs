using System.Numerics;
using Microsoft.UI.Composition;
using Windows.UI;

namespace WhispUI.Composition;

// HUD Composition pipeline — strokes and shadows for the chrono and message
// surfaces. Pure Microsoft.UI.Composition (no Win2D) so the dependency graph
// stays minimal and the surface remains reusable for the upcoming progressive
// transcription preview window.
//
// Cardinal rule, applies to every shadow this helper produces: shadows are
// always composite multi-layer. A single DropShadow reads as flat. The Win11
// look comes from stacking at least two layers playing distinct roles —
// "halo" (small Y offset, large blur, ambient color around the surface) and
// "drop" (large Y offset, large blur, perceived height / "grand recul" off
// the background). Never ship a one-layer shadow.
//
// Silhouette pattern for shape-conformant DropShadows:
// DropShadow on a Visual takes the visual's alpha as silhouette. To obtain
// a rounded-rect shadow without Win2D, host a near-invisible (alpha=0x01)
// filled ShapeVisual inside a LayerVisual and attach the shadow on the
// layer — the layer rasterizes the rounded fill which then drives the
// shadow shape. The fill itself is invisible to the eye.
internal static class HudComposition
{
    private const float CornerRadiusDip = 8f;

    // Linear two-stop diagonal stroke for the Transcribing state.
    // Stops: #757575 → #BFBFBF, StartPoint (0,0) → EndPoint (1,1).
    // Plus mandatory 2-layer composite shadow:
    //   halo: offset (-2, 2), blur 21, color #66666638
    //   drop: offset (-12, 12), blur 64, color #66666647
    internal static ContainerVisual CreateTranscribingStroke(Compositor compositor, Vector2 size)
    {
        var container = compositor.CreateContainerVisual();
        container.Size = size;

        // Drop layer first (deepest). Halo on top so it occludes drop in the
        // overlap zone — closer-to-surface ambient reads cleaner.
        container.Children.InsertAtTop(CreateShadowedSilhouette(
            compositor, size,
            offset: new Vector3(-12f, 12f, 0f),
            blur: 64f,
            color: Color.FromArgb(0x47, 0x66, 0x66, 0x66)));
        container.Children.InsertAtTop(CreateShadowedSilhouette(
            compositor, size,
            offset: new Vector3(-2f, 2f, 0f),
            blur: 21f,
            color: Color.FromArgb(0x38, 0x66, 0x66, 0x66)));

        // Visible stroke on top.
        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = new Vector2(0f, 0f);
        gradient.EndPoint   = new Vector2(1f, 1f);
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0xFF, 0x75, 0x75, 0x75)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF)));

        var strokeGeometry = compositor.CreateRoundedRectangleGeometry();
        strokeGeometry.Size = size;
        strokeGeometry.CornerRadius = new Vector2(CornerRadiusDip, CornerRadiusDip);

        var strokeShape = compositor.CreateSpriteShape(strokeGeometry);
        strokeShape.StrokeBrush = gradient;
        strokeShape.StrokeThickness = 1f;

        var strokeVisual = compositor.CreateShapeVisual();
        strokeVisual.Size = size;
        strokeVisual.Shapes.Add(strokeShape);

        container.Children.InsertAtTop(strokeVisual);

        return container;
    }

    // 8 colored arcs distributed around the rounded rectangle perimeter via
    // CompositionRoundedRectangleGeometry.TrimStart/TrimEnd. Each arc gets
    // its own geometry instance — Trim is a property of the geometry, not
    // the shape, so geometries are NOT shared. Order: red → amber → lime →
    // green → cyan → blue → violet → magenta. Plus mandatory 8-layer
    // composite colored shadow at 45° increments (radius 32, alpha 0x40
    // in matching colors).
    internal static ContainerVisual CreateRewritingStroke(Compositor compositor, Vector2 size)
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
        container.Size = size;

        // 8 colored shadows first, distributed around the card at 45° steps.
        // Each shadow takes its arc's color so the diffused halo reads as a
        // rainbow ring around the surface.
        const float spread = 8f;
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4.0;
            float dx = (float)(Math.Cos(angle) * spread);
            float dy = (float)(Math.Sin(angle) * spread);
            container.Children.InsertAtTop(CreateShadowedSilhouette(
                compositor, size,
                offset: new Vector3(dx, dy, 0f),
                blur: 32f,
                color: WithAlpha(arcColors[i], 0x40)));
        }

        // 8 colored stroke arcs on top. epsilon overlap masks the seam between
        // adjacent trims — without it a 1-pixel gap shows on some DPI scales.
        const float epsilon = 0.005f;
        var strokeVisual = compositor.CreateShapeVisual();
        strokeVisual.Size = size;
        for (int i = 0; i < 8; i++)
        {
            float t0 = i / 8f;
            float t1 = MathF.Min((i + 1) / 8f + epsilon, 1f);

            var arcGeometry = compositor.CreateRoundedRectangleGeometry();
            arcGeometry.Size = size;
            arcGeometry.CornerRadius = new Vector2(CornerRadiusDip, CornerRadiusDip);
            arcGeometry.TrimStart = t0;
            arcGeometry.TrimEnd   = t1;

            var arcShape = compositor.CreateSpriteShape(arcGeometry);
            arcShape.StrokeBrush = compositor.CreateColorBrush(arcColors[i]);
            arcShape.StrokeThickness = 1f;
            strokeVisual.Shapes.Add(arcShape);
        }
        container.Children.InsertAtTop(strokeVisual);

        return container;
    }

    // Mandatory 2-layer composite shadow for message cards.
    //   halo: offset (0, 2),  blur 21 — semantic color around the card
    //   drop: offset (0, 32), blur 64 — perceived height off the background
    // Both layers share the same lerped color driven by the returned
    // PropertySet's Saturation scalar (1.0 = full, 0.0 = attenuated). The
    // ExpressionAnimation does the lerp at the Composition layer so the
    // animation runs off the UI thread.
    internal static (ContainerVisual host, CompositionPropertySet anim) CreateMessageShadow(
        Compositor compositor,
        Vector2 size,
        Color full,
        Color attenuated)
    {
        var props = compositor.CreatePropertySet();
        props.InsertScalar("Saturation", 1.0f);
        props.InsertColor("FullColor", full);
        props.InsertColor("AttenuatedColor", attenuated);

        var container = compositor.CreateContainerVisual();
        container.Size = size;

        // Drop layer first.
        var dropLayer  = CreateShadowedSilhouette(compositor, size,
            offset: new Vector3(0f, 32f, 0f), blur: 64f, color: full);
        BindShadowColorToProps(compositor, dropLayer, props);
        container.Children.InsertAtTop(dropLayer);

        // Halo on top.
        var haloLayer = CreateShadowedSilhouette(compositor, size,
            offset: new Vector3(0f, 2f, 0f), blur: 21f, color: full);
        BindShadowColorToProps(compositor, haloLayer, props);
        container.Children.InsertAtTop(haloLayer);

        return (container, props);
    }

    // ScalarKeyFrameAnimation on Saturation, 1.0 → 0.0 over the given duration
    // (default 650 ms when called from HudMessage). Easing CubicBezier(
    // {0.05, 0.95}, {0.2, 1.0}) — sharp peak then plateau, mimics the log-
    // decay phase requested in the Figma spec.
    internal static void AnimateShadowToAttenuated(
        Compositor compositor,
        CompositionPropertySet props,
        TimeSpan duration)
    {
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.05f, 0.95f),
            new Vector2(0.2f,  1.0f));

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1.0f, 0.0f, easing);
        anim.Duration = duration;

        props.StartAnimation("Saturation", anim);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns a LayerVisual whose silhouette is a rounded rectangle and whose
    // Shadow is the requested DropShadow. The "silhouette source" is a near-
    // invisible (alpha = 0x01) filled rounded rect — visually a no-op but
    // sufficient to drive the DropShadow shape.
    private static LayerVisual CreateShadowedSilhouette(
        Compositor compositor,
        Vector2 size,
        Vector3 offset,
        float blur,
        Color color)
    {
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = size;
        geometry.CornerRadius = new Vector2(CornerRadiusDip, CornerRadiusDip);

        var silhouetteShape = compositor.CreateSpriteShape(geometry);
        silhouetteShape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00));

        var silhouetteVisual = compositor.CreateShapeVisual();
        silhouetteVisual.Size = size;
        silhouetteVisual.Shapes.Add(silhouetteShape);

        var layer = compositor.CreateLayerVisual();
        layer.Size = size;
        layer.Children.InsertAtTop(silhouetteVisual);

        var shadow = compositor.CreateDropShadow();
        shadow.Color = color;
        shadow.Offset = offset;
        shadow.BlurRadius = blur;
        layer.Shadow = shadow;

        return layer;
    }

    private static void BindShadowColorToProps(
        Compositor compositor,
        LayerVisual layer,
        CompositionPropertySet props)
    {
        if (layer.Shadow is not DropShadow shadow) return;
        var expr = compositor.CreateExpressionAnimation(
            "ColorLerp(props.AttenuatedColor, props.FullColor, props.Saturation)");
        expr.SetReferenceParameter("props", props);
        shadow.StartAnimation("Color", expr);
    }

    private static Color WithAlpha(Color c, byte alpha)
        => Color.FromArgb(alpha, c.R, c.G, c.B);
}
