namespace Deckle.Capture;

// Microphone capture settings. AudioInputDeviceId = waveIn device index;
// -1 = WAVE_MAPPER (system default device).
//
// Renamed from RecordingSettings on 2026-05-02 when the capture subsystem
// was extracted into its own project. The page Settings ▸ Recording label
// stays unchanged on the UI side — only the C# property and the JSON key
// change. Migration from the legacy "Recording" JSON key is handled
// silently in SettingsService.Load.
public sealed class CaptureSettings
{
    public int AudioInputDeviceId { get; set; } = -1;

    // Hard cap on a single recording's duration, in seconds. When the capture
    // loop crosses this threshold, recording auto-stops as if the user had
    // pressed Stop — the captured audio still goes through the full
    // VAD → Whisper → (LLM) → paste pipeline. Prevents a forgotten hotkey
    // from running for hours and hitting a Whisper hallucination loop or
    // running out of RAM. 0 = no cap (legacy behaviour).
    public int MaxRecordingDurationSeconds { get; set; } = 20 * 60;

    public LevelWindowSettings LevelWindow { get; set; } = new();
}

// Persisted dBFS window the HUD chrono uses to map raw microphone RMS
// onto the [0, 1] perceptual level driving the Recording stroke. Exposed
// as Settings so the user can calibrate against their own mic+DSP chain
// without rebuilding — the values land in HudChrono.Min/MaxDbfs +
// DbfsCurveExponent statics at app startup (and on every change).
//
// Defaults match the shipping calibration:
//   Min  -55 dBFS — below typical p25 silence band, well above the
//                   -97 dBFS digital floor / DSP gate.
//   Max  -32 dBFS — measured peak ceiling for normal voice.
//   Exp   1.0    — linear response. Higher = compress low end / expand
//                  high end; lower = the opposite. The HUD reads "soit
//                  là, soit pas là" with a linear ramp.
//
// AutoCalibration runs a rolling heuristic over the last N
// `microphone.jsonl` rows: median(p10) → MinDbfs, median(p90 + 2 dB
// headroom) → MaxDbfs. Off by default — the user has to opt-in to a
// recurring tweak of their visual feedback. Triggers on every Recording
// once SamplesNeeded rows are available; the very first run after enable
// happens silently — the HUD just snaps to the new window on the next
// Recording. Manual sliders override the auto values until auto runs
// again.
public sealed class LevelWindowSettings
{
    public float MinDbfs                = -55f;
    public float MaxDbfs                = -32f;
    public float DbfsCurveExponent      = 1.0f;
    public bool  AutoCalibrationEnabled = false;
    public int   AutoCalibrationSamples = 5;
}
