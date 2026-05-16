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

    // sRGB EOTF — the reciprocal of LinearToSrgb. Same piecewise toe
    // around the 0.04045 seam. Used by the HDR tone-mapping path in
    // FrameSampler to convert FP16 scRGB samples back to linear sRGB
    // before averaging in linear light.
    //
    // Negative inputs are mirrored through the curve, matching the
    // LinearToSrgb convention so that round-trips (LinearToSrgb →
    // SrgbToLinear or the reverse) are numerically stable for the
    // small-magnitude negatives that scRGB allows.
    public static float SrgbToLinear(float x)
    {
        if (x < 0f) return -SrgbToLinear(-x);
        return x <= 0.04045f
            ? x / 12.92f
            : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    // scRGB FP16 → sRGB 8-bit, content-relative Hable filmic tone-map.
    //
    // scRGB (the colour space `Direct3D11CaptureFramePool` returns when
    // the OS is in HDR mode) is a linear-light, floating-point space
    // where (1, 1, 1) maps to the SDR reference white (≈ 80 nits) and
    // values above 1 represent HDR highlights up to the display's
    // peakWhite nits. Out-of-gamut negatives (Rec.2020 / DCI-P3
    // primaries beyond sRGB) are allowed ; they're clipped below.
    //
    // The previous V0 implementation normalised on the display's peak
    // (peakWhite / 80, e.g. 12.5 on HDR1000). That left a typical HDR
    // scene — which peaks at 200–400 nits, i.e. scRGB 2.5–5 — squashed
    // in the lower 20–40 % of the output, producing dim lamps even on
    // a bright sky. The branch's tone-map normalises on an *observed*
    // `contentPeak` instead, supplied by the caller via a rolling max
    // over the recent frames. On the same HDR1000 display, a sky-
    // bright scene now gets contentPeak ≈ 3–5 and the output uses the
    // full [0, 1] range. The caller is responsible for capping the
    // rolling max at the display's hard ceiling (peakWhite / 80) so a
    // freak sun-glint pixel can't crush the whole scene below it.
    //
    // The curve itself is Hable's Uncharted-2 filmic operator —
    // smoothly compresses highlights without crushing the mid-tones,
    // unlike a simple clip. F(0) = 0 algebraically, F is monotonic ;
    // the normalisation F(rgb) / F(W) maps scRGB=0 → 0 and scRGB=W →
    // 1 with a gentle shoulder above mid-tones.
    //
    // exposureEv is a linear-light EV-stop bias applied before the
    // tone-map. +1 doubles brightness, -1 halves it. 0 = neutral.
    // Combined with the content-relative normalisation, the user can
    // dial in "Hue Sync presence" with a single value when the
    // automatic mapping leaves the lights too dim or too aggressive.
    //
    // Parameters :
    //   r, g, b     — scRGB linear-light channels (can be negative or
    //                 above 1 ; clipping happens after the tone-map).
    //   contentPeak — caller-maintained rolling max of recent frames'
    //                 max-channel values, in scRGB units. Floored at
    //                 1.0 internally so a fully-dark frame still maps
    //                 SDR white to ~1.0 instead of amplifying noise.
    //   exposureEv  — additional EV bias (default 0 = no bias).
    public static Color ScRgbToSrgb(float r, float g, float b, float contentPeak, double exposureEv = 0.0)
    {
        // Linear-light exposure compensation (one EV stop = ×2).
        float gain = MathF.Pow(2f, (float)exposureEv);
        float rE = r * gain;
        float gE = g * gain;
        float bE = b * gain;

        // SDR floor on the normalisation peak. On a fully-dark frame
        // the caller's rolling max collapses toward 0 ; we never want
        // to compress below scRGB=1.0 (SDR white) because that would
        // amplify noise, and a near-zero contentPeak would zero-divide
        // through Hable.
        float W = MathF.Max(contentPeak, 1f);
        float invHableW = 1f / Hable(W);

        float rN = Hable(rE) * invHableW;
        float gN = Hable(gE) * invHableW;
        float bN = Hable(bE) * invHableW;

        return Color.FromArgb(
            0xFF,
            (byte)MathF.Round(LinearToSrgb(Math.Clamp(rN, 0f, 1f)) * 255f),
            (byte)MathF.Round(LinearToSrgb(Math.Clamp(gN, 0f, 1f)) * 255f),
            (byte)MathF.Round(LinearToSrgb(Math.Clamp(bN, 0f, 1f)) * 255f));
    }

    // Hable's Uncharted-2 filmic tone-mapper (presented at GDC 2010).
    // The constants are the original publication's "shoulder strength"
    // / "linear strength" / "linear angle" / "toe strength" / "toe
    // numerator" / "toe denominator" — widely accepted defaults for
    // HDR-to-LDR mapping. F(0) = 0 algebraically (the E/F bias cancels
    // the toe at the origin).
    public static float Hable(float x)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
        return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
    }
}
