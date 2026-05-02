namespace Deckle.Capture;

// ── AudioLevelMapper ──────────────────────────────────────────────────────────
//
// Pure mic-RMS-to-perceptual-level mapping. Lives in Deckle.Capture
// because it's signal processing of microphone data — sister concept to
// MicrophoneCapture (the source) and CaptureSettings.LevelWindow (the
// calibration). Any UI module that wants to react to live mic level
// (HUD stroke opacity today, Ask-Ollama text glow tomorrow) reads
// `RmsToPerceptualLevel` and consumes the [0, 1] result however it
// wants.
//
// The four public statics double as Playground tunables — the Playground
// slider page mutates them live to explore the curve shape. Atomic float
// reads: the audio thread reads RmsToPerceptualLevel concurrently with
// the UI thread writing the statics. Single-precision float writes are
// atomic on all .NET platforms; no lock needed.
//
// Auto-calibration (engine-side) and manual sliders (Settings ▸ General
// ▸ Recording) write through `App.ApplyLevelWindow` which forwards into
// these statics. Defaults match the shipping calibration documented in
// CaptureSettings.LevelWindowSettings.
//
// Extracted from Controls/HudChrono.xaml.cs on 2026-05-02 — was on the
// HUD control by historical accident; the math is purely audio-domain.
public static class AudioLevelMapper
{
    // EMA smoothing factor applied AFTER the dBFS remap. EmaAlpha 0.25
    // at 20 Hz source → τ = -T / ln(alpha) ≈ 0.05 / 0.328 ≈ 0.15 s —
    // fast enough to track intonations at the word scale (typical word
    // = 200–500 ms) while still ironing out the sample grid into a
    // continuous ramp. The consumer owns the smoother state (the EMA
    // value to feed back) since per-window state shouldn't be a global.
    public static float EmaAlpha = 0.25f;

    // Linear RMS mapped through a dBFS window, then through a power
    // curve. The window [MinDbfs, MaxDbfs] folds the dBFS range into a
    // linear [0, 1] parameter t; the power curve t^p then reshapes the
    // response so the visual reacts softly in the lower half and
    // aggressively in the upper half of the window.
    //
    // Reference table with MinDbfs = -40, MaxDbfs = -22 (18 dB window)
    // and DbfsCurveExponent = 2.0 (quadratic):
    //   rms ≤ 0.010 (-40 dBFS)  → t=0.00  → y=0.00   silence / gate
    //   rms = 0.018 (-35 dBFS)  → t=0.28  → y=0.08   breath / ambient
    //   rms = 0.032 (-30 dBFS)  → t=0.56  → y=0.31   soft onset
    //   rms = 0.040 (-28 dBFS)  → t=0.67  → y=0.44   conversational
    //   rms = 0.050 (-26 dBFS)  → t=0.78  → y=0.61   louder
    //   rms = 0.063 (-24 dBFS)  → t=0.89  → y=0.79   assertive speech
    //   rms = 0.079 (-22 dBFS)  → t=1.00  → y=1.00   emphatic ceiling
    //
    // Calibration — typical voice peaks around -18 dBFS but the 50 ms
    // RMS average sits 6-10 dB below peak, landing in -28..-24 dBFS for
    // normal speech and brushing -22 dBFS only on emphatic stress.
    // Previous ceiling at -18 dBFS was unreachable in practice:
    // conversational RMS reached y ≈ 0.30 and even loud speech stayed
    // below y=0.55, so the stroke barely lit up during real recordings
    // (the playground's sim pump masked this because its peak value
    // saturated the upper range). The -22 dBFS ceiling puts
    // conversational RMS at y=0.44-0.79 — clearly visible, with real
    // dynamics — and the quadratic curve keeps the low-end soft so
    // ambient noise still fades to zero.
    //
    // MinDbfs -40: matches the engine's noise-gate threshold, so the
    // visual floor coincides with the audible floor. Default −55 here
    // is the auto-calibration starting point; the engine retunes it
    // session-by-session if AutoCalibrationEnabled is on.
    //
    // DbfsCurveExponent 1.0 restores the old linear mapping; values
    // above 1 push the response to the upper end of the window; below
    // 1 pushes it to the low end (only useful for debugging).
    public static float MinDbfs           = -55f;
    public static float MaxDbfs           = -32f;
    public static float DbfsCurveExponent = 1.0f;

    // Pure RMS → [0, 1] perceptual level. Linear RMS in (zero or less
    // returns 0 — silence / gate). Caller owns the EMA smoother.
    public static float RmsToPerceptualLevel(float rms)
    {
        if (rms <= 0f) return 0f;
        float dbfs = 20f * MathF.Log10(rms);
        float t = (dbfs - MinDbfs) / (MaxDbfs - MinDbfs);
        t = Math.Clamp(t, 0f, 1f);
        // Power-curve response. p = 1 is linear; p > 1 compresses the
        // low end and expands the high end. Guarded against p ≤ 0 so
        // the playground can't nuke the mapping by dragging to 0.
        float p = DbfsCurveExponent;
        if (p <= 0f) return t;
        return MathF.Pow(t, p);
    }
}
