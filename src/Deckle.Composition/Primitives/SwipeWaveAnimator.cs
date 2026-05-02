using System.Numerics;

namespace Deckle.Composition;

// ── SwipeWaveAnimator ─────────────────────────────────────────────────────────
//
// Reusable left→right "wave" animator. Drives a row of N elements (digits,
// glyphs, characters) with a head that walks across the row over a fixed
// cycle, lifting each element's *heat* in [0, 1] as the head passes. Heat
// rises fast (SwipeRiseAlpha) when the head is on a marked element and
// decays slowly (SwipeDecayAlpha) once the head moves on, producing a
// trailing-comet read instead of a single moving pixel.
//
// Two parallel per-element flags are tracked:
//   - `Changed[i]` — caller-maintained: true means this element is
//     eligible for the heat rise. Unchanged elements stay at heat=0
//     (their target is pinned to 0 every frame, so any inherited heat
//     simply decays).
//   - `Heat[i]`    — animator-maintained: current heat value, advanced
//     by Tick() every frame.
//
// Extracted from Controls/HudChrono.xaml.cs on 2026-05-02. Future reuse
// target: Ask-Ollama text reveal — same algorithm, applied to a row of
// glyphs being unveiled left→right as the model streams.
//
// ── Tunables ──────────────────────────────────────────────────────────────────
// `public static` (not const / readonly) so HudPlayground can tune the
// cadence, easing, and rise/decay alphas live. Process-wide — every
// SwipeWaveAnimator instance reads the same values, just like the
// pre-extraction statics on HudChrono did.
public sealed class SwipeWaveAnimator
{
    // Default element count — kept at 6 to mirror the pre-extraction
    // HudChrono digit row (Min1 Min2 Sec1 Sec2 Cs1 Cs2). Other consumers
    // pass their own count via the constructor.
    public const int DefaultElementCount = 6;

    // SwipeCycleSeconds — full head-sweep duration (seconds).
    // Lower = faster wave, higher = contemplative.
    public static float SwipeCycleSeconds = 2.0f;

    // SwipeEaseP1/P2 — cubic-bezier control points for the head's
    // progress curve. Defaults (0.25, 0) → (0.25, 1) for a hard ease-out
    // — the head lingers near the start, then snaps through the elements.
    public static Vector2 SwipeEaseP1 = new(0.25f, 0f);
    public static Vector2 SwipeEaseP2 = new(0.25f, 1f);

    // SwipeRiseAlpha — per-frame lerp factor toward the active target
    // (head on element = 1). 0.05 ≈ ~45 frames to reach 90 % at 60 Hz →
    // ~750 ms ramp-up; soft enough that the pic never feels punchy, slow
    // enough that the element is still "catching up" when the head has
    // already moved on.
    public static float SwipeRiseAlpha = 0.05f;

    // SwipeDecayAlpha — per-frame lerp factor back to 0 when the head
    // is elsewhere. 0.025 ≈ ~90 frames to drop below 10 % at 60 Hz →
    // ~1.5 s trail — much longer than rise so heat accumulates into a
    // long, soft comet that bleeds across cycles. Keep
    // DecayAlpha < RiseAlpha / 3 for the wave character; otherwise the
    // trail reads as "tremblement" rather than "vague".
    public static float SwipeDecayAlpha = 0.025f;

    // SwipeHeadDomain — virtual head domain. The head walks
    // SwipeHeadDomain slots per cycle; only slots [0, ElementCount) map
    // to a real element, the rest are silent — target=0 everywhere — so
    // the easing's tail decelerates "into the void" instead of pinning
    // the last element while progress crawls toward the seam. Tune
    // > ElementCount to gain breathing room before the next cycle's
    // snap; equal to ElementCount reproduces the pre-fix stall.
    public static int SwipeHeadDomain = 8;

    private readonly bool[] _changed;
    private readonly float[] _heat;

    // Element count is fixed at construction — _changed and _heat are
    // sized once and the per-frame loop never re-allocates.
    public int ElementCount => _heat.Length;

    public SwipeWaveAnimator(int elementCount = DefaultElementCount)
    {
        if (elementCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(elementCount),
                "Element count must be positive.");
        _changed = new bool[elementCount];
        _heat    = new float[elementCount];
    }

    // ── Changed flags (caller-maintained) ────────────────────────────────────
    // Element marked as `changed` becomes eligible for heat rise when the
    // head lands on it. Caller flips these from its own state machine
    // (Recording: each digit flip flags itself; Ask-Ollama future: each
    // newly-revealed glyph flags itself).

    public bool IsChanged(int index) => _changed[index];

    public void SetChanged(int index, bool value) => _changed[index] = value;

    public void ClearAllChanged()
    {
        for (int i = 0; i < _changed.Length; i++) _changed[i] = false;
    }

    // ── Heat (animator-maintained, caller-readable) ─────────────────────────
    // Heat is read every frame by the caller to drive its own visual
    // (TextBlock.Opacity, brush alpha, etc.). The animator owns the
    // values; the SetHeat / ClearAllHeat hooks let the caller seed or
    // reset the state without going through Tick — used for the
    // Recording-time "each change flashes red" pattern (caller writes
    // heat=1 on the freshly-changed element so the flash is visible
    // even though Tick is dormant during Recording).

    public float GetHeat(int index) => _heat[index];

    public void SetHeat(int index, float value) => _heat[index] = value;

    public void ClearAllHeat()
    {
        for (int i = 0; i < _heat.Length; i++) _heat[i] = 0f;
    }

    // ── Per-frame advance ────────────────────────────────────────────────────
    // Compute the new heat values given the seconds elapsed since the
    // cycle started. Reads the SwipeCycleSeconds / SwipeEaseP* /
    // SwipeRiseAlpha / SwipeDecayAlpha / SwipeHeadDomain statics. Caller
    // then reads heats via GetHeat(i) and applies them to its visual.
    //
    // Algorithm (preserved verbatim from the pre-extraction HudChrono
    // implementation):
    //   1. t = (elapsed / SwipeCycleSeconds) % 1.0  — wrap into [0, 1)
    //   2. progress = CubicBezier(t, P1, P2)
    //   3. headIndex = floor(progress * domain), with domain = max(SwipeHeadDomain, ElementCount)
    //      headIndex >= ElementCount → "head off-screen" (sentinel -1)
    //   4. for each element i:
    //        target = (i == headIndex && _changed[i]) ? 1f : 0f
    //        alpha  = target > _heat[i] ? SwipeRiseAlpha : SwipeDecayAlpha
    //        _heat[i] += (target - _heat[i]) * alpha
    public void Tick(double elapsedSeconds)
    {
        // Fraction of the cycle that has elapsed, wrapped into [0, 1). The
        // modulo gives us the looping behaviour for free — no keyframe
        // roll-over to worry about.
        float t = (float)((elapsedSeconds / SwipeCycleSeconds) % 1.0);
        float progress = Easing.CubicBezier(t, SwipeEaseP1, SwipeEaseP2);

        // Head slot in [0, SwipeHeadDomain). Only the first ElementCount
        // slots map to a real element; slots >= ElementCount are silent
        // (head is "off-screen"), which lets the eased tail run out before
        // the next cycle's snap — the fix for the last-element stall.
        // Sentinel -1 means "no element is the head this frame", so the
        // i==headIndex test below is false everywhere and every element
        // decays toward 0.
        int domain = SwipeHeadDomain > 0 ? SwipeHeadDomain : _heat.Length;
        int headIndex = (int)MathF.Floor(progress * domain);
        if (headIndex >= _heat.Length) headIndex = -1;
        else if (headIndex < 0)        headIndex = 0;

        for (int i = 0; i < _heat.Length; i++)
        {
            // Target heat: 1 when the head is on this element AND the
            // element was marked changed. Everywhere else, 0. Unchanged
            // elements never receive target = 1, so their heat only ever
            // decays and stays dark after the initial fade-out.
            float target = (i == headIndex && _changed[i]) ? 1f : 0f;

            // Asymmetric lerp: fast rise, slow decay. The asymmetry is
            // what creates the comet trail.
            float alpha = target > _heat[i] ? SwipeRiseAlpha : SwipeDecayAlpha;
            _heat[i] += (target - _heat[i]) * alpha;
        }
    }
}
