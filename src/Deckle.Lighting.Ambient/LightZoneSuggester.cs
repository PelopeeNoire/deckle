using Deckle.Lighting.Hue;

namespace Deckle.Lighting.Ambient;

// Maps a Hue entertainment-area position (X, Y, Z ∈ [-1, 1]) to the
// best-fit <see cref="LightZone"/> for the ambient pipeline. The
// mapping is a small heuristic, not a physical model — it captures
// the "ambilight around the screen" mental model :
//
//   X dominates  → Left (X<0) / Right (X>0).
//   Z dominates  → Top  (Z>0) / Bottom (Z<0).
//   Both small   → no clear edge dominance → None (user picks manually).
//
// Y (depth, back-of-room ↔ front-of-room) is intentionally ignored.
// All ambilight setups place lamps near the TV plane (Y ≈ 1) ; using
// it would only introduce noise from rear-room speakers / Hue Play
// units the user set up further back.
//
// Why not assign every light somewhere. A light placed dead-centre
// behind the TV ((0, 1, 0) on the Hue convention) doesn't have a
// natural border zone — defaulting it to "Top" or "Left" would feel
// arbitrary. We surface "no clear suggestion" instead, the user picks
// from the ComboBox explicitly.
//
// Why public. The Playground in Deckle.csproj consumes Suggest()
// directly to pre-fill the per-light ComboBoxes — and Deckle.csproj
// lives in a different assembly from Deckle.Lighting.Ambient.csproj,
// so internal would hide it. AmbientPage (same module) will reuse
// the same helper once J9 brings the runtime UI out of the dev-only
// Playground.
public static class LightZoneSuggester
{
    // Threshold below which an axis is considered "too close to zero
    // to matter". A light at (0.05, …, 0.10) reads as "centred" — we
    // don't want either edge winning by a hair-thin margin. 0.15
    // gives a clear neutral zone around the origin without eating
    // legitimate corner placements.
    private const double NeutralDeadband = 0.15;

    public static LightZone Suggest(HueLightPlacement placement)
    {
        double ax = Math.Abs(placement.X);
        double az = Math.Abs(placement.Z);

        // Both axes inside the dead-band → centred light, no suggestion.
        if (ax < NeutralDeadband && az < NeutralDeadband) return LightZone.None;

        // Pick the dominant axis. Ties favour the horizontal split
        // since Hue setups commonly have a single Play strip above the
        // TV (Z ≈ 1) and two satellites left / right (X ≈ ±0.5 with a
        // small Z component) — when X and Z are close, the user's
        // mental layout is "side", not "top".
        if (ax >= az)
        {
            return placement.X < 0 ? LightZone.Left : LightZone.Right;
        }
        else
        {
            return placement.Z > 0 ? LightZone.Top : LightZone.Bottom;
        }
    }
}
