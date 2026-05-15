namespace Deckle.Lighting.Hue;

// Conversion sRGB → Hue's preferred colour representation : CIE 1931
// xy chromaticity + a 1..254 brightness byte. The xy part follows the
// Philips Hue developer guide (Wide Gamut RGB D65 transform). The
// brightness part deliberately doesn't follow it.
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
//      x = X / (X+Y+Z), y = Y / (X+Y+Z) — that's our colour output.
//   5. Brightness = max(R, G, B) / 255 * 254, clamped to 1..254.
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

        // Brightness from the dominant sRGB channel — see class
        // header for the rationale (Y-luminance would gimp the
        // pure-colour swatches). Floor of 1 keeps the lamp lit ;
        // pure black is handled by the early-out above.
        int maxByte = Math.Max(c.R, Math.Max(c.G, c.B));
        byte brightness = (byte)Math.Clamp(
            (int)Math.Round(maxByte * 254.0 / 255.0), 1, 254);

        return (new HueXy(xc, yc), brightness);
    }

    private static double SrgbGammaToLinear(double v)
        => v > 0.04045
            ? Math.Pow((v + 0.055) / 1.055, 2.4)
            : v / 12.92;
}

// CIE 1931 xy chromaticity coordinates. Internal-only — the Hue API
// is the only consumer for now ; downstream drivers stick to sRGB.
internal readonly record struct HueXy(double X, double Y);
