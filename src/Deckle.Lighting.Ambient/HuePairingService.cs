using Deckle.Lighting.Hue;
using Deckle.Logging;

namespace Deckle.Lighting.Ambient;

// Process-wide owner of the active Hue bridge pairing. Wraps
// HueDiscovery + HueBridgeClient + AmbientSettings persistence so the
// Playground and the Settings AmbientPage share one source of truth :
// the Bridge they observe is the same instance, persistence is done
// in one place, and re-pairing from one surface is reflected
// immediately in the other via the BridgeChanged event.
//
// Why here and not under Deckle.Lighting.Hue. The service has to
// persist the bridge ip/id/username, which lives in AmbientSettings
// (the only configured consumer of Hue today). Lighting can't
// reference Lighting.Ambient (would create a cycle), so the service
// is anchored on the consumer side. If a future module needs Hue
// without ambient, we extract a thin Lighting-level state interface
// and keep the persistence here.
//
// Ownership and disposal. The service owns the HueBridgeClient
// instance after a successful pair or restore. Forget() and
// re-pair both dispose the previous client before replacing it.
// Dispose() is for shutdown — the service itself is a process
// singleton so it rarely fires in production code paths.
//
// Threading. Pair / Restore / Forget mutate _bridgeClient under a
// lock so concurrent UI clicks from Playground + Settings don't
// produce torn state. The Bridge property reads outside the lock
// (snapshot of an immutable reference) — readers see a consistent
// HueBridgeClient or null, never a half-constructed instance.
public sealed class HuePairingService : IDisposable
{
    private static readonly Lazy<HuePairingService> _instance =
        new(() => new HuePairingService());
    public static HuePairingService Instance => _instance.Value;

    private static readonly LogService _log = LogService.Instance;

    private readonly object _gate = new();
    private HueBridgeClient? _bridgeClient;
    private HueBridge?       _pairedBridge;
    private bool _disposed;

    // Default pairing window — matches the visible countdown the
    // Playground UI shows. Callers can override per-call if they need
    // a shorter window for testing.
    public static TimeSpan DefaultPairingTimeout => TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultPollInterval   => TimeSpan.FromSeconds(2);

    // Lazy singleton init runs RestoreFromSettings as a side-effect on
    // first access so any caller (Playground, AmbientPage, AmbientEngine)
    // sees a restored Bridge without having to coordinate a boot-time
    // call. RestoreFromSettings is idempotent — calling it again later
    // (e.g. from a UI Refresh) re-builds the client from the current
    // persisted state, which is what the user expects after editing
    // settings.json by hand. The BridgeChanged event fires here but
    // typically has no subscribers yet — UI surfaces subscribe later
    // and re-read Bridge on their own when they open.
    private HuePairingService()
    {
        try
        {
            RestoreFromSettings();
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Hue,
                $"Bridge auto-restore at boot failed — {ex.GetType().Name}: {ex.Message} (user will need to re-pair)");
        }
    }

    /// <summary>Active bridge client when paired, null otherwise.
    /// Use this for control-path calls (SetGroupColorAsync,
    /// SetLightColorAsync, IdentifyLightAsync …) — they are intentionally
    /// not wrapped here to avoid mirroring the entire HueBridgeClient
    /// surface for no added value.</summary>
    public HueBridgeClient? Bridge
    {
        get { lock (_gate) { return _bridgeClient; } }
    }

    /// <summary>Identification triple of the paired bridge (ip/id/port)
    /// without exposing the credentials. Null when not paired. Useful
    /// for the UI to display "Paired (192.168.1.5)".</summary>
    public HueBridge? PairedBridge
    {
        get { lock (_gate) { return _pairedBridge; } }
    }

    /// <summary>True when a bridge is currently paired and ready to
    /// receive REST calls. Mirrors <c>Bridge?.IsPaired</c>.</summary>
    public bool IsPaired
    {
        get { lock (_gate) { return _bridgeClient is { IsPaired: true }; } }
    }

    /// <summary>Raised after any state-changing operation (Pair,
    /// Restore, Forget). Subscribers should re-read <see cref="Bridge"/>
    /// inside the handler — the event carries no payload by design,
    /// the property is the source of truth.</summary>
    public event Action? BridgeChanged;

    /// <summary>
    /// Looks up Hue bridges on the local network via the cloud
    /// discovery endpoint. Pure wrapper around
    /// <see cref="HueDiscovery.DiscoverViaCloudAsync"/> — surfaced on
    /// the service so callers don't need to import the lower-level
    /// type.
    /// </summary>
    public Task<IReadOnlyList<HueBridge>> DiscoverAsync(CancellationToken ct = default)
        => HueDiscovery.DiscoverViaCloudAsync(ct);

    /// <summary>
    /// Pairs with the given bridge, persists the credentials to
    /// AmbientSettings on success, replaces the active bridge client
    /// and fires <see cref="BridgeChanged"/>. Returns the credentials
    /// on success ; throws TimeoutException if the link button is not
    /// pressed within <paramref name="timeout"/>, or HuePairingException
    /// / HueBridgeUnreachableException on bridge-side / transport-side
    /// failures. The previous client (if any) is disposed before the
    /// new one takes over — re-pairing the same bridge is the same code
    /// path as pairing a fresh one.
    /// </summary>
    public async Task<HueCredentials> PairAsync(
        HueBridge bridge,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveTimeout = timeout ?? DefaultPairingTimeout;
        var effectivePoll    = pollInterval ?? DefaultPollInterval;

        // Build a fresh client outside the lock — pairing does HTTP
        // and may take up to `timeout`. Holding the lock that long
        // would block every reader of Bridge / IsPaired.
        var candidate = new HueBridgeClient(bridge);
        HueCredentials creds;
        try
        {
            creds = await candidate.PairAsync(effectiveTimeout, effectivePoll, ct)
                                    .ConfigureAwait(false);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }

        // Swap the active client under the lock. Dispose the previous
        // one outside the lock to avoid holding it across a Dispose
        // call (cheap but disciplined).
        HueBridgeClient? previous;
        lock (_gate)
        {
            previous       = _bridgeClient;
            _bridgeClient  = candidate;
            _pairedBridge  = bridge;
        }
        previous?.Dispose();

        // Persist the durable identifiers : ip, id, username. The
        // client key (Entertainment v2 PSK) is intentionally NOT
        // persisted — by doctrine and because the REST path doesn't
        // need it.
        var settings = AmbientSettingsService.Instance.Current;
        settings.HueBridgeIp = bridge.InternalIpAddress;
        settings.HueBridgeId = bridge.Id;
        settings.HueUsername = creds.Username;
        AmbientSettingsService.Instance.Save();

        _log.Info(LogSource.Hue,
            $"Bridge pairing stored | bridge_id={bridge.Id} | username_head={creds.UsernameHead}");

        BridgeChanged?.Invoke();
        return creds;
    }

    /// <summary>
    /// Rebuilds the active bridge client from values already persisted
    /// in <see cref="AmbientSettings"/> (ip / id / username). Called
    /// once at app start so the user doesn't have to re-press the link
    /// button on every boot. No-op if any of the three fields are
    /// missing or empty (treated as "not paired yet"). Fires
    /// <see cref="BridgeChanged"/> on success so any UI surface
    /// subscribed since process boot picks up the restored state.
    /// </summary>
    public void RestoreFromSettings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var settings = AmbientSettingsService.Instance.Current;
        if (string.IsNullOrWhiteSpace(settings.HueBridgeIp) ||
            string.IsNullOrWhiteSpace(settings.HueBridgeId) ||
            string.IsNullOrWhiteSpace(settings.HueUsername))
        {
            _log.Verbose(LogSource.Hue,
                "restore | skipped — no persisted bridge identity");
            return;
        }

        // Default port 443 — every bridge currently in the field
        // (Hue Bridge v2, firmware-versioned 1948086000+) listens on
        // 443. v1 bridges that needed port 80 are out of scope ;
        // discovery never returns them in the cloud lookup any more.
        var bridge = new HueBridge(settings.HueBridgeId!, settings.HueBridgeIp!, 443);
        var creds  = new HueCredentials(settings.HueUsername!, "");
        var client = new HueBridgeClient(bridge, creds);

        HueBridgeClient? previous;
        lock (_gate)
        {
            previous       = _bridgeClient;
            _bridgeClient  = client;
            _pairedBridge  = bridge;
        }
        previous?.Dispose();

        _log.Info(LogSource.Hue,
            $"Bridge restored from settings | bridge_id={bridge.Id} | bridge_ip={bridge.InternalIpAddress}");

        BridgeChanged?.Invoke();
    }

    /// <summary>
    /// Lists the groups (rooms, zones, entertainment areas) configured
    /// on the active bridge. Throws InvalidOperationException if no
    /// bridge is paired.
    /// </summary>
    public Task<IReadOnlyList<HueGroup>> ListGroupsAsync(CancellationToken ct = default)
        => GetRequiredBridge().ListGroupsAsync(ct);

    /// <summary>
    /// Lists the lights inside the given group on the active bridge.
    /// Throws InvalidOperationException if no bridge is paired.
    /// </summary>
    public Task<IReadOnlyList<HueLight>> ListLightsInGroupAsync(string groupId, CancellationToken ct = default)
        => GetRequiredBridge().ListLightsInGroupAsync(groupId, ct);

    /// <summary>
    /// Lists every entertainment area configured on the active bridge
    /// with per-light positions. Throws InvalidOperationException if
    /// no bridge is paired.
    /// </summary>
    public Task<IReadOnlyList<HueEntertainmentArea>> ListEntertainmentConfigurationsAsync(CancellationToken ct = default)
        => GetRequiredBridge().ListEntertainmentConfigurationsAsync(ct);

    /// <summary>
    /// Forgets the active pairing. Disposes the bridge client, clears
    /// the persisted credentials from AmbientSettings, fires
    /// <see cref="BridgeChanged"/>. The username on the bridge itself
    /// is NOT revoked — the bridge keeps it valid until the user
    /// explicitly removes it from the Hue mobile app. This is a local
    /// "forget" — pairing again later still works without a re-press
    /// of the link button if the user does it before the bridge times
    /// the username out.
    /// </summary>
    public void Forget()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        HueBridgeClient? previous;
        lock (_gate)
        {
            previous       = _bridgeClient;
            _bridgeClient  = null;
            _pairedBridge  = null;
        }
        previous?.Dispose();

        var settings = AmbientSettingsService.Instance.Current;
        settings.HueBridgeIp   = null;
        settings.HueBridgeId   = null;
        settings.HueUsername   = null;
        settings.HueLastGroupId = null;
        AmbientSettingsService.Instance.Save();

        _log.Info(LogSource.Hue, "Bridge forgotten — persisted credentials cleared");

        BridgeChanged?.Invoke();
    }

    private HueBridgeClient GetRequiredBridge()
    {
        var client = Bridge;
        if (client is null || !client.IsPaired)
        {
            throw new InvalidOperationException(
                "No bridge is currently paired. Call PairAsync or RestoreFromSettings first.");
        }
        return client;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        HueBridgeClient? previous;
        lock (_gate)
        {
            previous       = _bridgeClient;
            _bridgeClient  = null;
            _pairedBridge  = null;
        }
        previous?.Dispose();
    }
}
