namespace Deckle.Logging;

// ── ITelemetryGates ─────────────────────────────────────────────────────────
//
// Hub through which Deckle.Logging asks the host application about its
// runtime gates: which JSONL files are allowed to land on disk, and where
// they should land. Decouples Logging from any concrete settings system —
// the host plugs in its own implementation at startup via
// TelemetryGates.Configure().
//
// Properties are read on every emit, not snapshot at Configure time, so
// flipping a toggle off in the host's settings stops new writes
// immediately without rebuilding the sink graph. Implementations should
// be cheap and fail-safe (return false / null on any internal error)
// since they run inside JSONL write paths that must never throw.
public interface ITelemetryGates
{
    bool ApplicationLogToDisk { get; }
    bool LatencyEnabled       { get; }
    bool CorpusEnabled        { get; }
    bool MicrophoneTelemetry  { get; }

    // Absolute path that overrides the default <UserDataRoot>\telemetry\
    // root for every JSONL sink. Null or empty → use the default.
    string? StorageDirectoryOverride { get; }
}
