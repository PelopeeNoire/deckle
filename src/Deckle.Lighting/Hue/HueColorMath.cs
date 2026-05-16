namespace Deckle.Lighting.Hue;

// Conversion sRGB → Hue's preferred colour representation : CIE 1931
// xy chromaticity + a 1..254 brightness byte. The xy part follows the
// Philips Hue developer guide (Wide Gamut RGB D65 transform) and then
// clips client-side to the lamp's Gamut C triangle to avoid the
// bridge's edge-projection for out-of-gamut points. The brightness
// part deliberately doesn't follow the developer guide.
//
// Why we don't use Y for brightness. The "natural" approach (Y * 254
// from the same RGB→XYZ transform) is photometrically correct but
// counter-intuitive in practice : pure blue (0,0,255) has a luminance
// Y ≈ 0.047, so the bridge would receive bri=12 — barely above off.
// Pure red is bri=72, pure green bri=170, pure white bri=254. The
// user clicking a colour swatch expects "this colour at full power",
// not "the physically faithful luminance of this colour at sRGB
// 100 %". The brightness/colour decoupling is the right model :
// chromaticity says what colour, brightness says how much of it.
// For the ambient pipeline that follows, the brightness will come
// from the source image's average luminance (computed upstream),
// not from the colour push itself.
//
// Pipeline.
//   1. Normalise R/G/B from 0..255 to 0..1.
//   2. Undo the sRGB gamma curve to get linear-light RGB.
//   3. Multiply by the Wide Gamut RGB→XYZ matrix (D65 illuminant)
//      published by Philips for the Hue colour space.
//   4. Project (X, Y, Z) to (x, y) chromaticity via
//      x = X / (X+Y+Z), y = Y / (X+Y+Z).
//   5. Clip (x, y) to the lamp's Gamut C triangle via nearest-edge
//      projection — see ClipToGamutC.
//   6. Brightness = max(R, G, B) / 255 * 254, clamped to 1..254.
//      Saturated colours map to bri=254 regardless of hue.
//
// Edge case : pure black (0, 0, 0) returns (0, 0) for xy and
// brightness 0 so the caller knows to send `on:false` instead of
// pushing a degenerate xy value.
internal static class HueColorMath
{
    public static (HueXy Xy, byte Brightness) RgbToHueXyBri(LightColor c)
    {
        if (c.R == 0 && c.G == 0 && c.B == 0)
            return (new HueXy(0, 0), 0);

        double rs = c.R / 255.0;
        double gs = c.G / 255.0;
        double bs = c.B / 255.0;

        double r = SrgbGammaToLinear(rs);
        double g = SrgbGammaToLinear(gs);
        double b = SrgbGammaToLinear(bs);

        // Wide Gamut RGB → XYZ (D65) — Philips Hue reference matrix.
        // Source : developers.meethue.com/develop/application-design-guidance/
        //          color-conversion-formulas-rgb-to-xy-and-back/
        double X = r * 0.664511 + g * 0.154324 + b * 0.162028;
        double Y = r * 0.283881 + g * 0.668433 + b * 0.047685;
        double Z = r * 0.000088 + g * 0.072310 + b * 0.986039;

        double sum = X + Y + Z;
        double xc = sum > 0 ? X / sum : 0;
        double yc = sum > 0 ? Y / sum : 0;

        HueXy clipped = ClipToGamutC(new HueXy(xc, yc));

        // Brightness from the dominant sRGB channel — see class
        // header for the rationale (Y-luminance would gimp the
        // pure-colour swatches). Floor of 1 keeps the lamp lit ;
        // pure black is handled by the early-out above.
        int maxByte = Math.Max(c.R, Math.Max(c.G, c.B));
        byte brightness = (byte)Math.Clamp(
            (int)Math.Round(maxByte * 254.0 / 255.0), 1, 254);

        return (clipped, brightness);
    }

    private static double SrgbGammaToLinear(double v)
        => v > 0.04045
            ? Math.Pow((v + 0.055) / 1.055, 2.4)
            : v / 12.92;

    // Gamut C corners (Philips Hue Play / Iris / E14 / Lightstrip Plus
    // gen 2+). Published in the Hue developer docs as the chromaticity
    // primaries the lamp can physically reach. Points outside this
    // triangle in xy CIE 1931 get projected by the bridge onto the
    // nearest edge — for deep blues just outside the blue corner (VS
    // Code Night Owl #011627 lands at xy ≈ 0.150, 0.203), the bridge's
    // projection ends up on the B-G edge where x ≈ 0.15 mixes high
    // green / low blue, rendered as turquoise. Doing the clip
    // client-side via nearest-edge projection keeps the deep blue
    // saturated at the blue corner instead.
    //
    // See docs/architecture--color-science-pipeline--0.1.md axis 1
    // decision A for the full math and the rejected alternatives
    // (projection-toward-D65, sigmoid hull compression).
    private const double GamutCRedX   = 0.6915, GamutCRedY   = 0.3083;
    private const double GamutCGreenX = 0.17,   GamutCGreenY = 0.7;
    private const double GamutCBlueX  = 0.1532, GamutCBlueY  = 0.0475;

    // Nearest-point-on-triangle projection in xy CIE 1931. If the
    // input lies inside the Gamut C triangle, identity. Otherwise
    // project onto each of the three edges with parametric clamp
    // t ∈ [0, 1] and return the closest of the three projections by
    // euclidean distance squared.
    internal static HueXy ClipToGamutC(HueXy xy)
    {
        double px = xy.X, py = xy.Y;

        if (IsInsideGamutC(px, py))
            return xy;

        var p1 = ClosestPointOnSegment(px, py, GamutCRedX,   GamutCRedY,   GamutCGreenX, GamutCGreenY);
        var p2 = ClosestPointOnSegment(px, py, GamutCGreenX, GamutCGreenY, GamutCBlueX,  GamutCBlueY);
        var p3 = ClosestPointOnSegment(px, py, GamutCBlueX,  GamutCBlueY,  GamutCRedX,   GamutCRedY);

        double d1 = DistanceSquared(px, py, p1.x, p1.y);
        double d2 = DistanceSquared(px, py, p2.x, p2.y);
        double d3 = DistanceSquared(px, py, p3.x, p3.y);

        if (d1 <= d2 && d1 <= d3) return new HueXy(p1.x, p1.y);
        if (d2 <= d3)              return new HueXy(p2.x, p2.y);
        return new HueXy(p3.x, p3.y);
    }

    // Inside-triangle test via the sign of the three edge cross
    // products. If the point sits on the same side of every edge
    // (all signs ≥ 0 or all ≤ 0), it's inside. Edges and degenerate
    // colinear points (sign == 0) are treated as inside, which is the
    // identity-preserving choice on the boundary.
    private static bool IsInsideGamutC(double px, double py)
    {
        double d1 = EdgeSign(px, py, GamutCRedX,   GamutCRedY,   GamutCGreenX, GamutCGreenY);
        double d2 = EdgeSign(px, py, GamutCGreenX, GamutCGreenY, GamutCBlueX,  GamutCBlueY);
        double d3 = EdgeSign(px, py, GamutCBlueX,  GamutCBlueY,  GamutCRedX,   GamutCRedY);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double EdgeSign(double px, double py, double x1, double y1, double x2, double y2)
        => (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2);

    private static (double x, double y) ClosestPointOnSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return (ax, ay);
        double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0;
        return (ax + t * dx, ay + t * dy);
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return dx * dx + dy * dy;
    }
}

// CIE 1931 xy chromaticity coordinates. Internal-only — the Hue API
// is the only consumer for now ; downstream drivers stick to sRGB.
internal readonly record struct HueXy(double X, double Y);
