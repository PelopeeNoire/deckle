namespace Deckle.Lighting.Hue;

// ILightOutput implementation on top of a paired HueBridgeClient.
// Binds the abstraction to a specific group on a specific bridge ;
// constructing one is cheap (just wraps the client + a group id),
// the network work happens at ConnectAsync / SetColorAsync time.
//
// Ownership. The HueBridgeClient is passed in from outside (typically
// the Playground or, later, AmbientEngine) and is NOT disposed by the
// HueRestLightOutput. The caller is the one who paired the bridge in
// the first place ; it knows when the credentials should be released.
// Multiple HueRestLightOutput instances pointed at different groups
// can share a single HueBridgeClient without contention — Hue's REST
// API tolerates concurrent writes per group, and the HttpClient
// underneath is thread-safe for parallel requests.
//
// Lifecycle.
//   - Construct with a paired client + group id. If the client isn't
//     paired yet, ConnectAsync throws InvalidOperationException — we
//     refuse to "lazy pair" because the link-button UX is interactive
//     and shouldn't surface through this abstraction.
//   - ConnectAsync is a cheap re-check + a no-op for now (no per-
//     output session to open). It's where future Entertainment v2
//     would start the DTLS handshake transparently.
//   - SetColorAsync delegates straight to the bridge client.
//   - DisposeAsync is a no-op for ownership reasons (see above).
public sealed class HueRestLightOutput : ILightOutput
{
    private readonly HueBridgeClient _client;
    private readonly string _groupId;
    private bool _connected;

    public HueRestLightOutput(HueBridgeClient client, string groupId)
    {
        _client = client;
        _groupId = groupId;
    }

    public bool IsConnected => _connected && _client.IsPaired;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!_client.IsPaired)
        {
            throw new InvalidOperationException(
                "HueBridgeClient is not paired ; pair it before constructing a HueRestLightOutput.");
        }
        _connected = true;
        return Task.CompletedTask;
    }

    public Task SetColorAsync(LightColor color, CancellationToken ct = default)
        => _client.SetGroupColorAsync(_groupId, color, ct);

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
