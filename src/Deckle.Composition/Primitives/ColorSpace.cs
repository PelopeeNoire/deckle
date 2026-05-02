using Windows.UI;

namespace Deckle.Composition;

// ── ColorSpace ────────────────────────────────────────────────────────────────
//
// Pure colour-space primitives reusable across the Deckle family. Lives in
// Deckle.Composition because the canonical rotating conic stroke uses
// perceptually-uniform OKLCh interpolation to avoid the luminance
// asymmetry of HSV (yellows ≈ 0.93 luma, blues ≈ 0.07 luma — visible as
// a top/bottom gradient when a Saturation effect drags the stroke
// toward greyscale).
//
// Math is pure: no Composition runtime, no allocations, no IO. Reusable
// from any Deckle module that needs perceptually-uniform colour output
// (e.g. Ask-Ollama palette, future status badges).
//
// Extracted from HudComposition.cs on 2026-05-02.
public static class ColorSpace
{
    // OKLCh → sRGB (gamma-corrected, 8-bit channels).
    //
    // Pipeline: OKLCh → OKLab → linear sRGB → gamma-corrected sRGB.
    // Linear sRGB values may fall outside [0, 1] for L/C combinations
    // that exit the sRGB gamut (yellows and blues at L=0.75 start
    // clipping around C ≈ 0.18). We clamp to [0, 1] on the gamma
    // output — that reads as a gentle flattening of the out-of-gamut
    // hues rather than a hard stop, which is good enough for a
    // rotating conic wheel where no individual hue lingers.
    //
    // Parameters:
    //   L      — OKLab lightness ∈ [0, 1]; 0.75 is the family default.
    //   C      — OKLab chroma ∈ ~[0, 0.4]; 0.18 stays in-gamut for
    //            most hues at L=0.75, with gentle fall-off in the
    //            yellow / blue extremes.
    //   hTurns — hue in turns ∈ [0, 1]; 0=red, 0.25=yellow, 0.5=green,
    //            0.75=blue (approximate, OKLab hue ordering).
    public static Color OklchToRgb(float L, float C, float hTurns)
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
    public static float LinearToSrgb(float x)
    {
        if (x < 0f) return -LinearToSrgb(-x);
        return x <= 0.0031308f
            ? 12.92f * x
            : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }
}
