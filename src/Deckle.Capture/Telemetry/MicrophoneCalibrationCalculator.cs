using Deckle.Logging;

namespace Deckle.Capture.Telemetry;

// Pure compute: derives a fresh (MinDbfs, MaxDbfs) pair from a ring buffer
// of recent MicrophoneTelemetryPayload samples. Returns whether the
// calibration should be applied (false on too-narrow window, on no-change,
// etc.) so the orchestrator (WhispEngine) can short-circuit the
// SaveSettings + ApplyLevelWindow + log emission without re-implementing
// the constants.
//
// Strategy (constants preserved from the original TryAutoCalibrate):
//   - MinDbfs = median(p25) - 5 dB  — p25 (not p10) so a noise gate
//                                     cutting to digital silence
//                                     (-97 dBFS) doesn't drag the
//                                     floor into "anything below
//                                     the gate threshold". Then
//                                     -5 dB of headroom under the
//                                     useful-signal minimum.
//   - MaxDbfs = median(p90) + 5 dB  — voice ceiling with breathing
//                                     room above routine peaks.
//   - Floor clamp at -75 dBFS to guarantee we never sit on the gate
//     even if p25 itself is in the noise floor.
//   - Refuse to write if the resulting window collapses to < 10 dB
//     (pathological case — e.g. all-silence sessions).
//   - Clamp to slider domains so the persisted values stay editable.
//   - Idempotent: returns ShouldUpdate=false when the new window is
//     within 0.5 dB of the current one (avoids log spam on stable mics).
public static class MicrophoneCalibrationCalculator
{
    // Result of the heuristic. When ShouldUpdate is false, RejectReason
    // explains why — useful for diagnosis traces, not currently logged.
    // NewMinDbfs / NewMaxDbfs are only meaningful when ShouldUpdate is true.
    public readonly record struct CalibrationResult(
        bool    ShouldUpdate,
        float   NewMinDbfs,
        float   NewMaxDbfs,
        string? RejectReason);

    // Compute the new window. Caller is responsible for the ring buffer
    // and for the SamplesNeeded count check (we don't get involved in
    // ring-buffer management — the orchestrator owns it).
    //
    // `samples` must contain exactly `needed` (= LevelWindow.AutoCalibrationSamples)
    // entries — caller checks this before calling. Behaviour with a smaller
    // buffer is defined (median picks the middle entry of however many we
    // get) but matches the legacy contract: callers wait until the buffer
    // is full.
    public static CalibrationResult Compute(
        System.Collections.Generic.IReadOnlyCollection<MicrophoneTelemetryPayload> samples,
        float currentMinDbfs,
        float currentMaxDbfs)
    {
        if (samples.Count == 0)
            return new CalibrationResult(false, 0, 0, "empty buffer");

        // Median across the buffer — avoids one rogue session pulling the
        // window in either direction.
        var p25s = System.Linq.Enumerable.OrderBy(
            System.Linq.Enumerable.Select(samples, p => p.P25Dbfs), v => v).ToArray();
        var p90s = System.Linq.Enumerable.OrderBy(
            System.Linq.Enumerable.Select(samples, p => p.P90Dbfs), v => v).ToArray();
        double medianP25 = p25s[p25s.Length / 2];
        double medianP90 = p90s[p90s.Length / 2];

        // -5 dB / +5 dB margins keep the HUD from sitting flush against
        // the user's measured percentiles — peaks above the median p90
        // still saturate cleanly, and the floor doesn't trigger on the
        // very edge of the silence band.
        double newMin = System.Math.Round(medianP25 - 5.0);
        double newMax = System.Math.Round(medianP90 + 5.0);

        // Floor guard — even with p25, a session dominated by gate-induced
        // silence can drag the median into the digital floor zone. Clamp
        // at -75 dBFS so we never calibrate the HUD to react to gated
        // silence. The user can still go lower manually via the slider
        // if they want to capture a quieter mic.
        if (newMin < -75) newMin = -75;

        // Sanity: dBFS window must span at least 10 dB to give the HUD a
        // visible dynamic range. A pathological all-silence buffer would
        // produce a near-flat window — skip and wait for richer sessions.
        if (newMax - newMin < 10)
            return new CalibrationResult(false, 0, 0, "window spread < 10 dB");

        // Clamp to the slider domains so the persisted values stay editable.
        newMin = System.Math.Clamp(newMin, -90, -10);
        newMax = System.Math.Clamp(newMax, -60, -10);
        if (newMax <= newMin)
            return new CalibrationResult(false, 0, 0, "clamped window collapsed");

        // Check whether anything changed — avoid log spam on stable mics.
        bool changed = System.Math.Abs(currentMinDbfs - newMin) >= 0.5f
                    || System.Math.Abs(currentMaxDbfs - newMax) >= 0.5f;
        if (!changed)
            return new CalibrationResult(false, (float)newMin, (float)newMax, "no change");

        return new CalibrationResult(true, (float)newMin, (float)newMax, null);
    }
}
