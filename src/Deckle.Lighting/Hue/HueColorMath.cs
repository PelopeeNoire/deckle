namespace Deckle.Lighting.Hue;

// Conversion sRGB → Hue's preferred colour representation : CIE 1931
// xy chromaticity + a 1..254 brightness byte. The math is taken from
// the Philips Hue developer guide (Wide Gamut RGB D65 transform) and
// is reproducible by anyone reading this file — no NuGet dependency,
// no opaque conversion table.
//
// Pipeline.
//   1. Normalise R/G/B from 0..255 to 0..1.
//   2. Undo the sRGB gamma curve to get linear-light RGB.
//   3. Multiply by the Wide Gamut RGB→XYZ matrix (D65 illuminant).
//      Philips publishes this matrix specifically for the Hue colour
//      space ; it differs from the BT.709 / sRGB→XYZ matrix that
//      most C# libraries default to, which would land the colour
//      outside the Hue gamut and trigger the bridge's clamping.
//   4. Project (X, Y, Z) to (x, y) chromaticity via
//      x = X / (X+Y+Z), y = Y / (X+Y+Z).
//   5. Brightness = round(Y * 254), clamped to 1..254 (0 would be
//      indistinguishable from off, the API uses on=false for that).
//
// Edge case : pure black (0, 0, 0) collapses to the origin of the xy
// plane (division by zero on the sum). We return (0, 0) for x/y and
// brightness 0 so the caller knows to send `on:false` instead of
// pushing a degenerate xy value.
internal static class HueColorMath
{
    public static (HueXy Xy, byte Brightness) RgbToHueXyBri(LightColor c)
    {
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
        if (sum <= 0)
            return (new HueXy(0, 0), 0);

        double xc = X / sum;
        double yc = Y / sum;

        // Y is the luminance component in the [0..1] range after
        // gamma decoding ; map to the bridge's 1..254 byte. The
        // brightness floor of 1 isn't reachable when the colour is
        // black — that path is handled by the early-out above, where
        // we return brightness=0 to signal "send on:false".
        byte brightness = (byte)Math.Clamp((int)Math.Round(Y * 254), 1, 254);

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
