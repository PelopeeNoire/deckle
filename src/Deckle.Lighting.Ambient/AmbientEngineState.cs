namespace Deckle.Lighting.Ambient;

// State machine for AmbientEngine, exposed via the StateChanged event.
// Five values let consumers (tray status text, AmbientPage ProgressRing,
// Playground button) distinguish a transient "in flight" state from a
// settled Off / Running, and surface failures explicitly.
//
// Canonical transitions :
//   Off  → Starting  → Running         (successful StartAsync)
//   Off  → Starting  → Error → Off     (StartAsync threw or refused)
//   Running → Stopping → Off            (Stop)
//
// Error is a transient signal — the engine collapses back to Off
// immediately after raising it so consumers don't sit in a sticky
// failure state ; the actual error message is surfaced via the
// LogService (Error level) and via the Enabled-revert in App.
public enum AmbientEngineState
{
    /// <summary>Engine idle — no capture, no Hue traffic, no owned deps.</summary>
    Off,

    /// <summary>Engine is constructing its dependencies (capture init,
    /// Hue bridge connect, ListLights). Typically 300–800 ms.</summary>
    Starting,

    /// <summary>Engine is running its push loop and reacting to frames.</summary>
    Running,

    /// <summary>Engine is tearing down (cancel token + FrameArrived
    /// unsubscribe). Typically &lt; 50 ms.</summary>
    Stopping,

    /// <summary>Transient — the last StartAsync failed (pair invalid,
    /// network down, bridge unreachable). State collapses to Off on the
    /// next event tick ; consumers should display the error briefly
    /// then return to the Off rendering.</summary>
    Error,
}
