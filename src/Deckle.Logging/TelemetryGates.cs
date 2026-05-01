using System;

namespace Deckle.Logging;

// ── TelemetryGates ──────────────────────────────────────────────────────────
//
// Static configurator the host calls once at startup to wire up the
// concrete ITelemetryGates that backs the Logging pipeline (typically a
// thin adapter over the host's settings store).
//
// Default posture (no Configure call yet) is closed: every toggle returns
// false, no override path. Sinks short-circuit to "disabled" until the
// host wires up real gates — preserves the legacy fail-safe behaviour
// JsonlFileSink relied on when AppSettings.Instance was not yet
// constructed. Modules that emit before Configure happens won't crash
// the pipeline; their events simply don't land on disk.
public static class TelemetryGates
{
    private static ITelemetryGates _current = ClosedGates.Instance;

    public static void Configure(ITelemetryGates gates)
    {
        if (gates is null) throw new ArgumentNullException(nameof(gates));
        _current = gates;
    }

    public static ITelemetryGates Current => _current;

    private sealed class ClosedGates : ITelemetryGates
    {
        public static readonly ClosedGates Instance = new();

        public bool ApplicationLogToDisk        => false;
        public bool LatencyEnabled              => false;
        public bool CorpusEnabled               => false;
        public bool MicrophoneTelemetry         => false;
        public string? StorageDirectoryOverride => null;
    }
}
