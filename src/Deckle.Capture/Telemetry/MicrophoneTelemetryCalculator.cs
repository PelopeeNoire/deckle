using Deckle.Capture.Internal;
using Deckle.Logging;

namespace Deckle.Capture.Telemetry;

// Pure compute: distribution payload + Tail-600 ms diagnostic from a
// per-recording RMS series (50 ms sub-window cadence, ~20 Hz). No side
// effects, no logging — the orchestrator (WhispEngine) is responsible
// for emitting both the user-facing tail headline and the structured
// MicrophoneTelemetryPayload via TelemetryService.
//
// Linear RMS → dBFS via 20·log10(rms); guarded against rms ≤ 0
// (returns -120 dBFS, the conventional "digital silence" floor — the
// -96.7 dBFS we used to see corresponded to a single-LSB residual
// from a zero-initialised buffer, indistinguishable from true silence
// and historically misleading).
public static class MicrophoneTelemetryCalculator
{
    // Result of the Tail-600 ms diagnostic. The orchestrator consumes the
    // headline + dBFS to print the user-facing log line; TailRms / TailDbfs
    // also flow into the MicrophoneTelemetryPayload for the JSONL row.
    public readonly record struct TailDiagnostic(
        double TailRms,
        double TailDbfs,
        int    TailMs,
        bool   TailActive,
        string TailState,
        string TailHeadline);

    // Compute the Tail-600 ms diagnostic from the RMS series. Returns null
    // when the series is empty — the caller should log the "no RMS samples
    // captured" warning in that case (the message is user-facing in the
    // Activity selector).
    public static TailDiagnostic? ComputeTail(System.Collections.Generic.IReadOnlyList<float> rmsLog)
    {
        int n = rmsLog.Count;
        if (n == 0) return null;

        // ── Tail-600 ms diagnostic (always on) ─────────────────────────────
        //
        // Root-mean-square of the last 12 sub-windows. Sums the sub-window
        // squared RMS values and re-roots, which is the mathematically
        // correct way to combine RMS samples (NOT a plain mean of RMS).
        // -50 dBFS keeps the active/silent threshold from the previous
        // diagnostic so existing log readers stay calibrated.
        //
        // The line is user-facing in the Activity selector: it tells you
        // whether you stopped after a silence (the natural case) or while
        // still speaking (often a hotkey hit too early — last words may be
        // clipped). The dBFS measurement stays in the line as a check for
        // anyone calibrating the gate, but the leading clause speaks plain
        // English.
        int tailCount = System.Math.Min(12, n);
        double tailSumSq = 0;
        for (int i = n - tailCount; i < n; i++)
        {
            double v = rmsLog[i];
            tailSumSq += v * v;
        }
        double tailRms = System.Math.Sqrt(tailSumSq / tailCount);
        double tailDbfs = PcmConversion.ToDbfs((float)tailRms);
        int tailMs = tailCount * 50;
        bool tailActive = tailDbfs > -50;
        string tailState = tailActive ? "active" : "silent";
        string tailHeadline = tailActive
            ? "You were still speaking at Stop — the last words may be clipped."
            : "You stopped after a silence — capture ends cleanly.";

        return new TailDiagnostic(tailRms, tailDbfs, tailMs, tailActive, tailState, tailHeadline);
    }

    // Build the full distribution payload (percentiles + mean + tail) from
    // the RMS series. Returns null when the series is empty. The caller
    // should pair this with ComputeTail() to surface the user-facing
    // headline + dBFS log.
    //
    // The percentile computation uses nearest-rank, clamped to [0, n-1] —
    // good enough for human-readable telemetry; we're not feeding a stats
    // engine.
    //
    // MeanDbfs is derived from the linear mean RMS (log of the mean), NOT
    // the arithmetic mean of dBFS values — the latter gets pulled too low
    // by the silence floor.
    public static MicrophoneTelemetryPayload? Compute(
        System.Collections.Generic.IReadOnlyList<float> rmsLog,
        TailDiagnostic                                  tail)
    {
        int n = rmsLog.Count;
        if (n == 0) return null;

        // ── Distribution payload (always computed) ─────────────────────────
        //
        // Builds the per-Recording percentile + mean payload regardless of
        // the log/disk toggles, because auto-calibration consumes it
        // independently. Computing this is cheap — sort of ~few thousand
        // floats — so we don't gate it.
        var sorted = new float[n];
        for (int i = 0; i < n; i++) sorted[i] = rmsLog[i];
        System.Array.Sort(sorted);

        // Percentile picker: nearest-rank, clamped to [0, n-1]. Good enough
        // for human-readable telemetry; we're not feeding a stats engine.
        float Pick(double frac) => sorted[System.Math.Clamp((int)(n * frac), 0, n - 1)];

        float min = sorted[0];
        float max = sorted[n - 1];
        float p10 = Pick(0.10), p25 = Pick(0.25), p50 = Pick(0.50);
        float p75 = Pick(0.75), p90 = Pick(0.90);

        // Mean of linear RMS — the number to compare against MaxDbfs window
        // when calibrating the HUD response. NOT the mean of dBFS values
        // (logs of small numbers skew that mean).
        double meanLinear = 0;
        for (int i = 0; i < n; i++) meanLinear += sorted[i];
        meanLinear /= n;
        double meanDbfs = PcmConversion.ToDbfs((float)meanLinear);

        double durSec = n * 0.05; // 50 ms per sub-window

        return new MicrophoneTelemetryPayload(
            DurationSeconds: durSec,
            Samples:         n,
            MinDbfs:         PcmConversion.ToDbfs(min),
            P10Dbfs:         PcmConversion.ToDbfs(p10),
            P25Dbfs:         PcmConversion.ToDbfs(p25),
            P50Dbfs:         PcmConversion.ToDbfs(p50),
            P75Dbfs:         PcmConversion.ToDbfs(p75),
            P90Dbfs:         PcmConversion.ToDbfs(p90),
            MaxDbfs:         PcmConversion.ToDbfs(max),
            MeanRms:         meanLinear,
            MeanDbfs:        meanDbfs,
            TailRms:         tail.TailRms,
            TailDbfs:        tail.TailDbfs,
            TailState:       tail.TailState);
    }
}
