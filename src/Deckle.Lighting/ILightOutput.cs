namespace Deckle.Lighting;

// Driver-facing abstraction for the "downstream" half of the ambient
// lighting pipeline. The contract is intentionally small : connect to
// whatever sink the user picked (Hue bridge via REST, future Home
// Assistant instance over HTTP/WebSocket, WLED node over DDP, DMX
// universe via Art-Net, …), push a single sRGB colour at whatever
// cadence the consumer chooses, disconnect cleanly. The single-colour
// surface is the lowest-common-denominator path — every driver supports
// it, every consumer can use it. Drivers that can address individual
// lights independently within their connected scope also implement
// <see cref="IMultiLightOutput"/> ; consumers query for the capability
// with a runtime cast and fall back to the single-colour push if the
// driver doesn't expose it.
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

// Capability sub-interface : exposed by drivers that can address
// individual lights independently within the connected scope. AmbientEngine
// queries with `output is IMultiLightOutput` at start ; if yes, the
// multi-light pipeline runs (one colour per light, per-light placement
// drives the screen sampling), otherwise it falls back to the
// <see cref="ILightOutput.SetColorAsync"/> path with a single average
// colour.
//
// Why a separate interface vs. an optional method on ILightOutput :
//   - Some drivers truly can't address individual fixtures (a single
//     DMX universe driving one parallel-wired strip ; an Art-Net node
//     in single-zone mode). Forcing them to implement a stub would
//     either lie (no-op SetLightColorsAsync) or throw at runtime
//     (NotSupportedException pollutes the consumer code). A capability
//     interface lets the consumer make the right decision up front.
//   - The list of addressable lights is a property of the connected
//     scope (one group, one HA area, one WLED segment range) — it's
//     resolved at ConnectAsync time, then cached for the session. The
//     consumer uses it once to drive placement UI ; it doesn't poll.
public interface IMultiLightOutput : ILightOutput
{
    /// <summary>Lists the lights addressable within the connected scope,
    /// in the driver's own order. Returns an empty list when the driver
    /// is connected but the underlying scope has no lights. Throws
    /// <see cref="InvalidOperationException"/> when called before
    /// <see cref="ILightOutput.ConnectAsync"/> has completed. Drivers
    /// MAY cache the result for the lifetime of the connection ; callers
    /// that need fresh data should reconnect.</summary>
    Task<IReadOnlyList<LightDescriptor>> ListLightsAsync(CancellationToken ct = default);

    /// <summary>Pushes one sRGB colour per light in a single logical
    /// batch. Drivers are free to parallelise the underlying transport
    /// calls. Cadence-pacing rules from <see cref="ILightOutput.SetColorAsync"/>
    /// apply per-light : drivers that hit a rate limit drop intermediate
    /// pushes (last-write-wins) rather than block. Light ids not present
    /// in <see cref="ListLightsAsync"/> are ignored silently — the
    /// driver may have re-cached an updated scope while the consumer
    /// was holding stale placements.</summary>
    Task SetLightColorsAsync(IReadOnlyDictionary<string, LightColor> colorsByLightId, CancellationToken ct = default);

    /// <summary>Asks the driver to make the addressed light briefly
    /// identifiable so the user can spot it physically — typically a
    /// short visible flash, controlled by the driver. The call returns
    /// once the driver has accepted the instruction ; the visible
    /// behaviour may extend past the returned Task (e.g. Hue's
    /// <c>lselect</c> flashes for 15 s after the bridge ACKs the PUT).
    /// Use <see cref="StopIdentifyAsync"/> to cut the flash short when
    /// the user has spotted the lamp. Drivers that have no equivalent
    /// operation may no-op.</summary>
    Task IdentifyLightAsync(string lightId, CancellationToken ct = default);

    /// <summary>Cancels any ongoing identify flash on the addressed
    /// light. Idempotent — calling on a light that isn't currently
    /// flashing is a no-op. The driver is free to restore the light's
    /// previous state ; Hue restores automatically after a brief
    /// transition.</summary>
    Task StopIdentifyAsync(string lightId, CancellationToken ct = default);
}
