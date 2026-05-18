namespace Deckle.Lighting.Ambient;

// Per-mode preset values. Applied onto an AmbientSettings instance
// when the user picks a preset in the Settings page or the Playground
// dropdown. Custom is intentionally absent — picking Custom doesn't
// reset anything, it just labels the current values as user-owned so
// a later preset switch can detect "the user has a custom tuning I'm
// about to overwrite".
//
// The values below are starting points : Louis will calibrate them
// once the underlying knobs (smoothing, curves) feel right. Until
// then they're plausible defaults that differ enough between modes
// to be visibly distinct on real content.

internal static class AmbientModePresets
{
    public static void Apply(AmbientMode mode, AmbientSettings target)
    {
        switch (mode)
        {
            case AmbientMode.Game:
                // Vivid, quick — matches the original V0 feel.
                target.ExposureEv            = 0.5;
                target.SaturationBoost       = 1.3;
                target.MinBrightness         = 100;
                target.BrightnessCurveType   = BrightnessCurveType.Linear;
                target.BrightnessCurveParam  = 1.0;
                target.SmoothingAlpha        = 0.40;
                target.ChangeThreshold       = 6;
                break;

            case AmbientMode.Movie:
                // Softened, long damping — cinematic mood lighting.
                target.ExposureEv            = 0.0;
                target.SaturationBoost       = 0.9;
                target.MinBrightness         = 60;
                target.BrightnessCurveType   = BrightnessCurveType.SCurve;
                target.BrightnessCurveParam  = 2.0;
                target.SmoothingAlpha        = 0.15;
                target.ChangeThreshold       = 8;
                break;

            case AmbientMode.Ambient:
                // Very smooth, low saturation — never feels like the
                // room competes with the screen.
                target.ExposureEv            = -0.5;
                target.SaturationBoost       = 0.7;
                target.MinBrightness         = 40;
                target.BrightnessCurveType   = BrightnessCurveType.Logarithmic;
                target.BrightnessCurveParam  = 1.0;
                target.SmoothingAlpha        = 0.08;
                target.ChangeThreshold       = 10;
                break;

            case AmbientMode.Custom:
            default:
                // Custom carries no preset — current tunings stay put.
                return;
        }
    }
}
