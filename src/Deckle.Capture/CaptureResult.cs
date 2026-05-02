using Deckle.Logging;

namespace Deckle.Capture;

// Outcome of a Record() call — closed enum, no localized strings here.
//   Completed  — normal user-driven Stop OR cancellation token fired.
//                Audio is in Pcm.
//   CapHit     — the host's MaxRecordingDurationSeconds was reached. Audio
//                captured up to the cap is in Pcm; the orchestrator is
//                expected to emit the "hit the N min cap" narrative and
//                continue the pipeline normally.
//   MicError   — waveInOpen failed. Pcm is empty. MmsysErr carries the raw
//                MMSYSERR value; the orchestrator maps it to user-visible
//                strings via Loc.Get on its side.
//   Cancelled  — the cancellation token fired before any audio was usable
//                (kept distinct from Completed for orchestrators that want
//                to short-circuit Transcribe). MicrophoneCapture itself
//                does not currently produce this — Stop via CT today flushes
//                whatever audio was recorded and returns Completed. Reserved
//                for future use (Dispose-style hard cancel).
public enum CaptureOutcome
{
    Completed,
    CapHit,
    MicError,
    Cancelled,
}

// Returned from MicrophoneCapture.Record. Pcm is float[-1, 1] mono 16 kHz
// (PCM16 normalized). Telemetry is null only when the recording was too
// short to produce any RMS sample (or on MicError).
//
// DrainDuration brackets the post-Stop drain phase — exposed so the
// caller's LatencyPayload builder can isolate it from the rest of the
// stop-to-pipeline cost (matches the legacy _recordDrainSw semantics).
//
// MmsysErr is 0 unless Outcome == MicError.
public readonly record struct CaptureResult(
    float[] Pcm,
    MicrophoneTelemetryPayload? Telemetry,
    CaptureOutcome Outcome,
    System.TimeSpan DrainDuration,
    uint MmsysErr);
