namespace Deckle.Capture;

// Contract injected by the orchestrator (WhispEngine, future Ask-Ollama)
// to expose its live capture-related settings to MicrophoneCapture without
// the latter depending on the host's full settings shape.
//
// Concrete adapters live in the host project (e.g. RecordingHostAdapter
// inside WhispEngine). Each property is read on every Record() entry —
// the host is free to forward to a settings service that may have changed
// since the previous call.
public interface IRecordingHost
{
    // waveIn device index. -1 = WAVE_MAPPER (system default device).
    int AudioInputDeviceId { get; }

    // Hard cap on a single recording's duration, in seconds. Snapshotted at
    // the start of each recording. 0 = no cap.
    int MaxRecordingDurationSeconds { get; }

    // Settings ▸ Telemetry ▸ Log microphone toggle. Gates the emission of
    // the per-recording MicrophoneTelemetryPayload event to TelemetryService.
    // The payload is always *computed* (auto-calibration depends on it);
    // this flag only decides whether it's broadcast.
    bool MicrophoneTelemetryEnabled { get; }
}
