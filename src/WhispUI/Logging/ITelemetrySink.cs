namespace WhispUI.Logging;

// Sink contract for TelemetryService. A sink is anything that wants to
// observe every emitted event. Sinks decide on their own which kinds they
// care about — the service dispatches all events uniformly.
//
// Sinks run on the caller's thread. UI sinks (LogWindow, HUD) are
// responsible for marshaling onto the UI thread before touching controls.
// File sinks serialize writes with their own lock.
public interface ITelemetrySink
{
    void Write(TelemetryEvent ev);
}
