namespace Deckle.Logging;

// Diagnostics / telemetry: four independent opt-in streams, all off by
// default — confidentiality first. The user explicitly authorizes each
// data class through its own consent dialog before anything lands on disk.
//
// LatencyEnabled controls the per-transcription latency JSONL (vad/whisper/
// llm/clipboard/paste timings, outcome). Lightweight, timings only, no user
// text — the closest equivalent to the legacy telemetry.csv.
//
// CorpusEnabled controls the raw Whisper text capture — one JSONL per
// rewrite profile, with the audio section metadata and the exact raw
// transcription. Stronger privacy posture (the user's words land on disk).
//
// RecordAudioCorpus is a nested opt-in that additionally saves the raw
// 16 kHz mono PCM audio as a .wav per transcription, alongside the text
// JSONL. Meaningless unless CorpusEnabled is also true. Audio carries the
// strongest posture (biometric-adjacent).
//
// ApplicationLogToDisk mirrors the in-process LogService stream to a JSONL
// file on disk. All log levels (Verbose → Error), all subsystems. Useful to
// diagnose a specific issue across a restart, noisy in steady-state — so
// opt-in with line-based rotation to cap disk footprint.
//
// MicrophoneTelemetry adds a per-Recording RMS distribution summary
// (min / p10 / p25 / p50 / p75 / p90 / max in dBFS + linear mean RMS)
// to the LogWindow AND to a dedicated <telemetry>/microphone.jsonl file.
// Calibration aid for tuning the HUD level window (MinDbfs / MaxDbfs /
// DbfsCurveExponent) against the user's actual mic+DSP chain instead of
// textbook conversational levels. Off by default — the line is dense
// enough to clutter the All filter for users who aren't calibrating.
//
// StorageDirectory is the common root for latency.jsonl / app.jsonl /
// microphone.jsonl / <profile-slug>/corpus.jsonl. Empty = default resolver
// (<repo>/benchmark/telemetry/ when running from the dev tree).
public sealed class TelemetrySettings
{
    public bool   LatencyEnabled       { get; set; } = false;
    public bool   CorpusEnabled        { get; set; } = false;
    public bool   RecordAudioCorpus    { get; set; } = false;
    public bool   ApplicationLogToDisk { get; set; } = false;
    public bool   MicrophoneTelemetry  { get; set; } = false;
    public string StorageDirectory     { get; set; } = "";
}
