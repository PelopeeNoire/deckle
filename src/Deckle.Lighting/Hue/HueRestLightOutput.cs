namespace Deckle.Lighting.Hue;

// ILightOutput / IMultiLightOutput implementation on top of a paired
// HueBridgeClient. Binds the abstraction to a specific group on a
// specific bridge ; constructing one is cheap (just wraps the client +
// a group id), the network work happens at ConnectAsync /
// SetColorAsync / SetLightColorsAsync time.
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
//   - SetColorAsync delegates straight to the bridge client
//     (/groups/{id}/action).
//   - ListLightsAsync caches the bridge response for the lifetime of
//     the connection (caller reconnects to refresh).
//   - SetLightColorsAsync fans out one PUT /lights/{id}/state per
//     entry via Task.WhenAll. Hue's REST stack tolerates parallel
//     writes per light ; we cap effective per-tick traffic via the
//     consumer-side cadence + the dictionary size.
//   - DisposeAsync is a no-op for ownership reasons (see above).
public sealed class HueRestLightOutput : IMultiLightOutput
{
    private readonly HueBridgeClient _client;
    private readonly string _groupId;
    private bool _connected;
    private IReadOnlyList<LightDescriptor>? _cachedLights;

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
        // Clear any cached list from a previous connection cycle — a
        // fresh ConnectAsync semantically restarts the session.
        _cachedLights = null;
        return Task.CompletedTask;
    }

    public Task SetColorAsync(LightColor color, CancellationToken ct = default)
        => _client.SetGroupColorAsync(_groupId, color, ct);

    public async Task<IReadOnlyList<LightDescriptor>> ListLightsAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "HueRestLightOutput is not connected ; call ConnectAsync first.");
        }

        if (_cachedLights is not null) return _cachedLights;

        var hueLights = await _client.ListLightsInGroupAsync(_groupId, ct).ConfigureAwait(false);

        // Project Hue-specific light records onto the driver-neutral
        // LightDescriptor. The Id stays as-is (Hue's integer-as-string)
        // since AmbientEngine and the placement layer treat it opaquely.
        var descriptors = new LightDescriptor[hueLights.Count];
        for (int i = 0; i < hueLights.Count; i++)
        {
            var l = hueLights[i];
            descriptors[i] = new LightDescriptor(l.Id, l.Name, l.Reachable);
        }
        _cachedLights = descriptors;
        return _cachedLights;
    }

    public Task SetLightColorsAsync(IReadOnlyDictionary<string, LightColor> colorsByLightId, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "HueRestLightOutput is not connected ; call ConnectAsync first.");
        }
        if (colorsByLightId.Count == 0) return Task.CompletedTask;

        // Fan out per-light pushes. HueBridgeClient.SetLightColorAsync
        // returns a Task that completes on the bridge's 200 response ;
        // Task.WhenAll surfaces the first exception via AggregateException
        // (we let AmbientEngine catch + log it as a transient warning,
        // same policy as the single-colour path).
        var tasks = new Task[colorsByLightId.Count];
        int i = 0;
        foreach (var (lightId, color) in colorsByLightId)
        {
            tasks[i++] = _client.SetLightColorAsync(lightId, color, ct);
        }
        return Task.WhenAll(tasks);
    }

    public Task IdentifyLightAsync(string lightId, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "HueRestLightOutput is not connected ; call ConnectAsync first.");
        }
        return _client.IdentifyLightAsync(lightId, ct);
    }

    public Task StopIdentifyAsync(string lightId, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "HueRestLightOutput is not connected ; call ConnectAsync first.");
        }
        return _client.StopIdentifyAsync(lightId, ct);
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _cachedLights = null;
        return ValueTask.CompletedTask;
    }
}
