namespace Deckle.Lighting;

// Generic 24-bit sRGB colour used as the in-app currency between the
// ambient-lighting pipeline and the light-output drivers. Kept deliberately
// thin : R/G/B bytes in 0..255, no alpha, no colour-space metadata, no
// gamma flag. Conversion to driver-native representations (CIE xy +
// brightness for Hue, RGB float for Home Assistant, packed bytes for
// WLED DDP, etc.) is the responsibility of the driver — the rest of the
// pipeline never has to think about it.
//
// Perceptual work (oklch interpolation, luminance weighting, etc.) lives
// upstream in Deckle.Composition / Deckle.Vision and produces sRGB at the
// boundary. Drivers receive sRGB only.
public readonly record struct LightColor(byte R, byte G, byte B)
{
    public static LightColor Black => new(0, 0, 0);
    public static LightColor White => new(255, 255, 255);
    public static LightColor Red   => new(255, 0,   0);
    public static LightColor Green => new(0,   255, 0);
    public static LightColor Blue  => new(0,   0,   255);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
