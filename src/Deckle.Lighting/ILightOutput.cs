namespace Deckle.Lighting;

// Driver-facing abstraction for the "downstream" half of the ambient
// lighting pipeline. The contract is intentionally small : connect to
// whatever sink the user picked (Hue bridge via REST, future Home
// Assistant instance over HTTP/WebSocket, WLED node over DDP, DMX
// universe via Art-Net, …), push a single sRGB colour at whatever
// cadence the consumer chooses, disconnect cleanly. Multi-zone push
// arrives later (J4+) when the analysis layer starts producing per-
// region colours — for now one ILightOutput == one zone == one colour
// at a time.
//
// Lifecycle.
//   - Construction is cheap and synchronous ; the driver carries
//     configuration (bridge IP + app key for Hue, server URL + token
//     for HA, etc.) but doesn't touch the network.
//   - ConnectAsync performs any handshake / probe / session start
//     required by the protocol ; it must succeed before SetColorAsync
//     is called.
//   - SetColorAsync is non-allocating-friendly (the LightColor struct
//     is value-typed) and must tolerate being called at the cadence
//     the protocol can sustain. Drivers that can't keep up should drop
//     intermediate pushes (last-write-wins) rather than queue.
//   - DisposeAsync stops the session and releases the network
//     resources ; idempotent.
//
// Threading. Methods are not required to be thread-safe ; the
// consumer is expected to serialise calls (typical pattern : push from
// a single background task that polls the analysis output). Drivers
// that need to coordinate background work (DTLS keep-alive, WebSocket
// reconnect, …) own their own synchronisation internally.
public interface ILightOutput : IAsyncDisposable
{
    /// <summary>True once <see cref="ConnectAsync"/> has completed
    /// successfully and the driver is ready to accept colour pushes.</summary>
    bool IsConnected { get; }

    /// <summary>Open the session with the configured sink. Throws if
    /// the sink is unreachable, the credentials are rejected, or any
    /// protocol-level handshake fails. Idempotent — calling twice on
    /// an already-connected driver is a no-op.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Push a single sRGB colour to the currently selected
    /// target (group / zone / universe / etc., scoped by driver
    /// configuration). Caller is responsible for cadence pacing ;
    /// drivers that hit a rate limit drop intermediate pushes rather
    /// than block.</summary>
    Task SetColorAsync(LightColor color, CancellationToken ct = default);
}
