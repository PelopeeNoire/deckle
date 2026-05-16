using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckle.Logging;

namespace Deckle.Lighting.Hue;

// Per-bridge REST client. One instance is bound to one HueBridge for
// the entire pairing + control lifecycle ; consumers that need to
// talk to several bridges (rare on a home network) instantiate one
// client per bridge.
//
// The Hue bridge serves HTTPS on port 443 with a self-signed
// certificate rooted on its own private CA. We accept any certificate
// presented by the bridge IP we explicitly asked for — MITM risk
// exists in theory on the LAN but is comparable to any other LAN
// service the user trusts implicitly. The alternative (importing the
// bridge CA into the system store, or pinning the SubjectPublicKeyInfo
// at first pair) is a polish item for a later milestone.
//
// All endpoints used at J2 are CLIP v1 — the older REST surface that
// still works on every v2 bridge, takes the username in the URL path,
// and is simpler to drive than CLIP v2's resource graph. Migration to
// CLIP v2 (request via `hue-application-key` header, resource-oriented
// `/clip/v2/resource/*` paths) can land later behind the same
// HueBridgeClient API.
public sealed class HueBridgeClient : IDisposable
{
    // Hue caps the `devicetype` string at 40 chars total. "deckle#" is
    // 7 chars, so we have 33 left for the machine name suffix.
    private const string DeviceTypePrefix = "deckle#";
    private const int    DeviceTypeMaxSuffixLength = 33;

    private static readonly LogService _log = LogService.Instance;

    private readonly HueBridge _bridge;
    private readonly HttpClient _http;
    private HueCredentials? _credentials;
    private bool _disposed;

    public HueBridgeClient(HueBridge bridge)
    {
        _bridge = bridge;
        _http = CreateBridgeHttpClient(bridge.InternalIpAddress, bridge.Port);
    }

    /// <summary>
    /// Restores a previously-paired client from persisted credentials.
    /// Used at app start to skip the link-button dance when the user has
    /// already paired in a previous session — the bridge keeps the
    /// username valid until manually revoked from the Hue app. The
    /// ClientKey field can be left empty when restoring from persisted
    /// state : the REST CLIP v1 path only uses Username, the PSK is
    /// reserved for Entertainment v2 DTLS (not in scope J3).
    /// </summary>
    public HueBridgeClient(HueBridge bridge, HueCredentials credentials)
        : this(bridge)
    {
        _credentials = credentials;
    }

    /// <summary>The bridge this client targets.</summary>
    public HueBridge Bridge => _bridge;

    /// <summary>Credentials obtained from a successful pairing, or
    /// null if pairing has not run yet (or has failed).</summary>
    public HueCredentials? Credentials => _credentials;

    /// <summary>True once pairing has succeeded and the bridge accepts
    /// authenticated calls.</summary>
    public bool IsPaired => _credentials is not null;

    /// <summary>
    /// Polls the bridge for a successful pairing, expecting the user
    /// to physically press the link button on top of the bridge within
    /// the given timeout. Returns the credentials on success, throws
    /// TimeoutException if the timeout elapses with no press, throws
    /// for any other bridge-side failure. Pairing is retried every
    /// <see cref="pollInterval"/> ; the loop is bounded by
    /// <see cref="overallTimeout"/>.
    /// </summary>
    public async Task<HueCredentials> PairAsync(
        TimeSpan overallTimeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow + overallTimeout;
        var deviceType = BuildDeviceType(Environment.MachineName);

        _log.Info(LogSource.Hue,
            "Pairing started — press the link button on the bridge");
        _log.Verbose(LogSource.Hue,
            $"pair start | bridge_ip={_bridge.InternalIpAddress} | timeout_sec={(int)overallTimeout.TotalSeconds} | devicetype={deviceType}");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await PairAttemptAsync(deviceType, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case PairOutcome.Success success:
                    _credentials = success.Credentials;
                    _log.Success(LogSource.Hue,
                        $"Bridge paired ({_bridge.Id})");
                    _log.Verbose(LogSource.Hue,
                        $"pair result | bridge_id={_bridge.Id} | username={_credentials.UsernameHead} | clientkey=[redacted]");
                    return _credentials;

                case PairOutcome.LinkButtonNotPressed:
                    // Verbose only — this is the expected state during
                    // the wait window, the user has not pressed the
                    // button yet. UI surfaces a separate countdown.
                    _log.Verbose(LogSource.Hue,
                        $"pair waiting | error_type=101 | next_attempt_in_ms={(int)interval.TotalMilliseconds}");
                    break;

                case PairOutcome.OtherError otherError:
                    _log.Error(LogSource.Hue,
                        $"Pairing rejected by bridge — type={otherError.Type}: {otherError.Description}");
                    throw new HuePairingException(
                        $"Bridge refused pairing (type {otherError.Type}): {otherError.Description}");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        _log.Warning(LogSource.Hue,
            "Pairing timed out — the link button was not pressed in time");
        throw new TimeoutException(
            "Hue bridge pairing timed out. Press the link button on the bridge and try again.");
    }

    /// <summary>
    /// Fetches the list of groups (rooms, zones, entertainment areas)
    /// configured on the bridge. The CLIP v1 endpoint returns a flat
    /// dictionary keyed by group id ; we project it to a list of
    /// public <see cref="HueGroup"/> records so the caller doesn't see
    /// the wire DTO. Pairing must have completed first ;
    /// <see cref="InvalidOperationException"/> is thrown otherwise.
    /// </summary>
    public async Task<IReadOnlyList<HueGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePaired();

        _log.Info(LogSource.Hue, "Listing groups");

        var dict = await _http.GetFromJsonAsync<Dictionary<string, HueGroupDto>>(
            $"api/{_credentials!.Username}/groups", _jsonOptions, ct)
            .ConfigureAwait(false);

        if (dict is null)
        {
            _log.Warning(LogSource.Hue, "Bridge returned no groups payload");
            return [];
        }

        var groups = new List<HueGroup>(dict.Count);
        foreach (var (id, dto) in dict)
        {
            int lightsCount = dto.Lights?.Length ?? 0;
            groups.Add(new HueGroup(id, dto.Name ?? "", dto.Type ?? "Unknown", lightsCount));
        }

        _log.Verbose(LogSource.Hue,
            $"groups list | bridge_id={_bridge.Id} | count={groups.Count}");
        foreach (var g in groups)
        {
            _log.Verbose(LogSource.Hue,
                $"group | id={g.Id} | name={g.Name} | type={g.Type} | lights={g.LightsCount}");
        }
        return groups;
    }

    // Hue's `transitiontime` is the fade duration the bridge interpolates
    // toward the new state, expressed in deciseconds (1/10 s). The
    // factory default is 4 (= 400 ms), which feels sluggish on a fast
    // dark→light transition (the lamp lags visibly behind the screen).
    // For an ambient-light driver we want near-instant push and let the
    // smoothing happen client-side (J5). 1 (= 100 ms) is a sweet spot for
    // V0 : fast enough to feel responsive, slow enough that the per-tick
    // 15 Hz updates don't read as strobing on the lamp.
    private const int AmbientTransitionDeciseconds = 1;

    /// <summary>
    /// Pushes a single sRGB colour to the given group. RGB is
    /// converted to Hue's xy + brightness representation in-house
    /// (see <see cref="HueColorMath"/>) ; pure black is mapped to
    /// `on:false` so the lamp goes off instead of jumping to the
    /// nearest in-gamut colour. The bridge `transitiontime` is forced
    /// to <see cref="AmbientTransitionDeciseconds"/> so the lamp
    /// doesn't lag the screen by the 400 ms factory default. Pairing
    /// must have completed.
    /// </summary>
    public Task SetGroupColorAsync(string groupId, LightColor color, CancellationToken ct = default)
        => PutStateAsync(
            $"api/{_credentials!.Username}/groups/{groupId}/action",
            color,
            target: $"group_id={groupId}",
            ct);

    /// <summary>
    /// Pushes a single sRGB colour to one individual light. Same
    /// conversion + <c>transitiontime</c> semantics as
    /// <see cref="SetGroupColorAsync"/> ; the difference is the endpoint
    /// (<c>/lights/{id}/state</c> vs. <c>/groups/{id}/action</c>) and
    /// the addressing granularity. Used by the multi-light pipeline
    /// where each light gets its own colour derived from a screen zone.
    /// </summary>
    public Task SetLightColorAsync(string lightId, LightColor color, CancellationToken ct = default)
        => PutStateAsync(
            $"api/{_credentials!.Username}/lights/{lightId}/state",
            color,
            target: $"light_id={lightId}",
            ct);

    // Shared body for the two PUT-state endpoints. CLIP v1 accepts the
    // exact same payload shape on /groups/{id}/action and
    // /lights/{id}/state — `on`, `bri`, `xy`, `transitiontime` — so the
    // conversion + body building + log line live here and the public
    // entry points just pick the URL and a log-friendly target tag.
    private async Task PutStateAsync(string url, LightColor color, string target, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePaired();

        var (xy, bri) = HueColorMath.RgbToHueXyBri(color);
        var body = new HueStateRequest
        {
            TransitionTime = AmbientTransitionDeciseconds,
        };

        if (bri == 0)
        {
            // Black → turn the light off. Sending on:false alone is
            // enough ; xy and bri are ignored by the bridge when
            // on:false is present. transitiontime still applies and
            // gives a 100 ms fade-out instead of a hard cut.
            body.On = false;
        }
        else
        {
            body.On = true;
            body.Brightness = bri;
            body.Xy = new[] { xy.X, xy.Y };
        }

        var response = await _http.PutAsJsonAsync(url, body, _jsonOptions, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warning(LogSource.Hue,
                $"Set colour failed | {target} | hr={(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
        }

        if (bri == 0)
        {
            _log.Verbose(LogSource.Hue,
                $"push colour | {target} | rgb={color.R},{color.G},{color.B} | on=false | tt_ds={AmbientTransitionDeciseconds}");
        }
        else
        {
            _log.Verbose(LogSource.Hue,
                $"push colour | {target} | rgb={color.R},{color.G},{color.B} | xy={xy.X:F4},{xy.Y:F4} | bri={bri} | tt_ds={AmbientTransitionDeciseconds}");
        }
    }

    /// <summary>
    /// Lists every entertainment area configured on the bridge with
    /// the per-light spatial positions Hue stores for each. The bridge
    /// exposes this only on the CLIP v2 resource graph
    /// (<c>/clip/v2/resource/entertainment_configuration</c>) ; the
    /// light references inside use v2 ids (rid), so we also fetch
    /// <c>/clip/v2/resource/light</c> to map v2 ids back to the v1
    /// integer-as-string ids the rest of the codebase uses
    /// (<see cref="ListLightsInGroupAsync"/>, <see cref="SetLightColorAsync"/>,
    /// etc.). The returned positions are normalised in [-1, 1] on
    /// each axis ; coordinate convention is Hue's :
    /// <list type="bullet">
    ///   <item>X : -1 = left of TV, +1 = right of TV.</item>
    ///   <item>Y : -1 = behind viewer, +1 = behind TV (front of room).</item>
    ///   <item>Z : -1 = floor, +1 = ceiling.</item>
    /// </list>
    /// Returns an empty list if the user hasn't configured any
    /// entertainment area in the Hue app.
    /// </summary>
    public async Task<IReadOnlyList<HueEntertainmentArea>> ListEntertainmentConfigurationsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePaired();

        _log.Info(LogSource.Hue, "Listing entertainment configurations");

        var entResponse = await GetV2Async<HueV2Response<HueV2EntertainmentConfigDto>>(
            "resource/entertainment_configuration", ct).ConfigureAwait(false);

        if (entResponse?.Data is null || entResponse.Data.Count == 0)
        {
            _log.Verbose(LogSource.Hue, "entertainment list | count=0");
            return [];
        }

        // Build the maps we need to resolve service_locations entries
        // into a usable (v1 light id, name) pair.
        //
        // /resource/light has `id_v1` ("/lights/3") + `metadata.name`.
        //   → v1Names :          v1-light-id → user-facing name
        //
        // /resource/entertainment is the per-lamp streaming endpoint
        // attached to each colour-capable Hue light. Its `id` is what
        // `service_locations[].service.rid` actually references in the
        // entertainment_configuration response (rtype=entertainment),
        // and its `id_v1` is the SAME "/lights/3" path as the
        // underlying light — so we use it to bridge the v2 ent uuid
        // back to the v1 light id used by the rest of the codebase.
        //   → entUuidToV1 :      v2-entertainment-uuid → v1-light-id
        //
        // This is the fix for "entertainment | lights=0" : without
        // /resource/entertainment, the rid lookup ran against the
        // /resource/light map and missed every entry.
        var v1Names = new Dictionary<string, string>();
        var lightsResponse = await GetV2Async<HueV2Response<HueV2LightDto>>(
            "resource/light", ct).ConfigureAwait(false);
        if (lightsResponse?.Data is not null)
        {
            foreach (var lt in lightsResponse.Data)
            {
                if (string.IsNullOrEmpty(lt.IdV1)) continue;
                int slash = lt.IdV1.LastIndexOf('/');
                if (slash < 0 || slash >= lt.IdV1.Length - 1) continue;
                var v1Id = lt.IdV1[(slash + 1)..];
                if (!string.IsNullOrEmpty(lt.Metadata?.Name))
                    v1Names[v1Id] = lt.Metadata.Name;
            }
        }

        var entUuidToV1 = new Dictionary<string, string>();
        var entServiceResponse = await GetV2Async<HueV2Response<HueV2EntertainmentServiceDto>>(
            "resource/entertainment", ct).ConfigureAwait(false);
        if (entServiceResponse?.Data is not null)
        {
            foreach (var es in entServiceResponse.Data)
            {
                if (string.IsNullOrEmpty(es.Id) || string.IsNullOrEmpty(es.IdV1)) continue;
                int slash = es.IdV1.LastIndexOf('/');
                if (slash < 0 || slash >= es.IdV1.Length - 1) continue;
                entUuidToV1[es.Id] = es.IdV1[(slash + 1)..];
            }
        }
        _log.Verbose(LogSource.Hue,
            $"entertainment v2 catalog | services={entUuidToV1.Count} | lights={v1Names.Count}");

        var areas = new List<HueEntertainmentArea>(entResponse.Data.Count);
        foreach (var ent in entResponse.Data)
        {
            var placements = new List<HueLightPlacement>();
            if (ent.Locations?.ServiceLocations is not null)
            {
                foreach (var loc in ent.Locations.ServiceLocations)
                {
                    if (loc.Service?.Rid is null) continue;
                    if (!entUuidToV1.TryGetValue(loc.Service.Rid, out var v1Id)) continue;

                    // Gather the position(s). Hue exposes both `position`
                    // (single point, legacy) and `positions` (multi-
                    // point, used for LED strips). Either may be filled.
                    // We average them so a horizontal strip behind the
                    // TV lands at its centroid instead of leaning on
                    // one corner.
                    double sumX = 0, sumY = 0, sumZ = 0;
                    int n = 0;
                    if (loc.Positions is not null)
                    {
                        foreach (var p in loc.Positions) { sumX += p.X; sumY += p.Y; sumZ += p.Z; n++; }
                    }
                    if (n == 0 && loc.Position is not null)
                    {
                        sumX = loc.Position.X; sumY = loc.Position.Y; sumZ = loc.Position.Z; n = 1;
                    }
                    if (n == 0) continue;

                    var name = v1Names.TryGetValue(v1Id, out var nm) ? nm : $"Light {v1Id}";
                    placements.Add(new HueLightPlacement(v1Id, name, sumX / n, sumY / n, sumZ / n));
                }
            }
            areas.Add(new HueEntertainmentArea(
                ent.Id ?? "",
                ent.Metadata?.Name ?? "",
                placements));
        }

        _log.Verbose(LogSource.Hue, $"entertainment list | count={areas.Count}");
        foreach (var area in areas)
        {
            _log.Verbose(LogSource.Hue,
                $"entertainment | id={area.Id} | name={area.Name} | lights={area.LightPlacements.Count}");
            foreach (var p in area.LightPlacements)
            {
                _log.Verbose(LogSource.Hue,
                    $"placement | ent_id={area.Id} | light_id={p.LightId} | name={p.Name} | x={p.X:F3} | y={p.Y:F3} | z={p.Z:F3}");
            }
        }
        return areas;
    }

    // GET against the CLIP v2 resource graph. CLIP v2 requires the
    // bearer key in a header (`hue-application-key`) instead of the
    // URL segment that v1 uses, and returns `{"data":[...], "errors":[...]}`
    // wrappers. The HttpClient base address is shared with v1 — only
    // the path prefix + header differs, so we set them per request.
    private async Task<T?> GetV2Async<T>(string path, CancellationToken ct) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"clip/v2/{path}");
        request.Headers.Add("hue-application-key", _credentials!.Username);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.Warning(LogSource.Hue,
                $"CLIP v2 GET failed | path={path} | hr={(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
        }
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the bridge to flash the addressed light so the user can
    /// spot it physically among other fixtures in the room. CLIP v1
    /// exposes the operation through the same /state endpoint with
    /// the <c>alert</c> field set to <c>lselect</c> (loop select :
    /// the bulb breathes / pulses for ~15 s and then auto-restores
    /// its previous state — we don't need to clean up). The bridge
    /// ACKs the PUT immediately, the visible flash continues in the
    /// background. Pair with <see cref="StopIdentifyAsync"/> when the
    /// caller wants to cut the flash short (typical : ~3 s in the
    /// Playground UI so the user isn't subjected to a 15 s strobe).
    /// </summary>
    public Task IdentifyLightAsync(string lightId, CancellationToken ct = default)
        => SendAlertAsync(lightId, "lselect", "start", ct);

    /// <summary>
    /// Stops an ongoing identify flash on the addressed light by
    /// setting <c>alert=none</c>. Idempotent — calling on a light
    /// that isn't currently flashing is harmless ; the bridge just
    /// acknowledges. The bridge restores the light's pre-flash state
    /// with a soft transition.
    /// </summary>
    public Task StopIdentifyAsync(string lightId, CancellationToken ct = default)
        => SendAlertAsync(lightId, "none", "stop", ct);

    private async Task SendAlertAsync(string lightId, string alert, string phase, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePaired();

        var body = new HueAlertRequest { Alert = alert };

        var response = await _http.PutAsJsonAsync(
            $"api/{_credentials!.Username}/lights/{lightId}/state",
            body, _jsonOptions, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warning(LogSource.Hue,
                $"Identify {phase} failed | light_id={lightId} | hr={(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
        }

        _log.Verbose(LogSource.Hue,
            $"light identify | light_id={lightId} | alert={alert} | phase={phase}");
    }

    /// <summary>
    /// Lists the lights that belong to the given group, in the bridge's
    /// own order. CLIP v1 doesn't return light metadata on the group
    /// endpoint — only the array of light ids — so we issue two GETs :
    /// one for the group (to get the id list) and one for <c>/lights</c>
    /// (to map each id to its human name + reachability flag). Two
    /// round-trips at "open settings" time is fine ; the multi-light
    /// push loop caches the result and doesn't re-query per tick.
    /// </summary>
    public async Task<IReadOnlyList<HueLight>> ListLightsInGroupAsync(string groupId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePaired();

        _log.Info(LogSource.Hue, $"Listing lights in group {groupId}");

        var groupDto = await _http.GetFromJsonAsync<HueGroupDto>(
            $"api/{_credentials!.Username}/groups/{groupId}", _jsonOptions, ct)
            .ConfigureAwait(false);

        if (groupDto?.Lights is null || groupDto.Lights.Length == 0)
        {
            _log.Verbose(LogSource.Hue,
                $"lights list | group_id={groupId} | count=0");
            return [];
        }

        var lightsDict = await _http.GetFromJsonAsync<Dictionary<string, HueLightDto>>(
            $"api/{_credentials!.Username}/lights", _jsonOptions, ct)
            .ConfigureAwait(false);

        if (lightsDict is null)
        {
            _log.Warning(LogSource.Hue,
                $"Bridge returned no lights payload | group_id={groupId}");
            return [];
        }

        var result = new List<HueLight>(groupDto.Lights.Length);
        foreach (var id in groupDto.Lights)
        {
            if (lightsDict.TryGetValue(id, out var dto))
            {
                result.Add(new HueLight(
                    id,
                    dto.Name ?? $"Light {id}",
                    dto.Type ?? "",
                    dto.State?.Reachable ?? true));
            }
            else
            {
                // The group references a light id that isn't in the
                // /lights dictionary — shouldn't happen in practice
                // (the bridge maintains the invariant), but we keep
                // the entry with a synthetic name rather than dropping
                // it silently so the UI surfaces the discrepancy.
                result.Add(new HueLight(id, $"Light {id}", "", false));
            }
        }

        _log.Verbose(LogSource.Hue,
            $"lights list | group_id={groupId} | count={result.Count}");
        foreach (var l in result)
        {
            _log.Verbose(LogSource.Hue,
                $"light | id={l.Id} | name={l.Name} | type={l.Type} | reachable={l.Reachable}");
        }
        return result;
    }

    private void EnsurePaired()
    {
        if (_credentials is null)
        {
            throw new InvalidOperationException(
                "Bridge is not paired. Call PairAsync first.");
        }
    }

    private async Task<PairOutcome> PairAttemptAsync(string deviceType, CancellationToken ct)
    {
        // CLIP v1 pairing endpoint. The response is an array containing
        // one element with either `success` (creds present) or `error`
        // (type 101 = link button not pressed, anything else is a hard
        // failure). The bridge ignores extra fields, so we ship exactly
        // the two it requires.
        var body = new HuePairRequest
        {
            DeviceType = deviceType,
            GenerateClientKey = true,
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("api", body, _jsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.Error(LogSource.Hue,
                $"Bridge unreachable during pairing — {ex.GetType().Name}: {ex.Message}");
            throw new HueBridgeUnreachableException(
                $"Bridge at {_bridge.InternalIpAddress} is unreachable.", ex);
        }

        // The bridge returns 200 even for the "link button not pressed"
        // path — the differentiator is in the JSON body, not the HTTP
        // status code. 4xx / 5xx are real protocol failures.
        if (!response.IsSuccessStatusCode)
        {
            _log.Error(LogSource.Hue,
                $"Pairing HTTP error | hr={(int)response.StatusCode} | reason={response.ReasonPhrase}");
            throw new HttpRequestException(
                $"Hue bridge returned {(int)response.StatusCode} on pairing.");
        }

        var elements = await response.Content
            .ReadFromJsonAsync<HueApiResponseElement[]>(_jsonOptions, ct)
            .ConfigureAwait(false);

        if (elements is null || elements.Length == 0)
            return new PairOutcome.OtherError(-1, "Empty response from bridge.");

        var element = elements[0];
        if (element.Success is { Username.Length: > 0 } success)
        {
            return new PairOutcome.Success(
                new HueCredentials(success.Username, success.ClientKey));
        }

        if (element.Error is { } error)
        {
            return error.Type == 101
                ? new PairOutcome.LinkButtonNotPressed()
                : new PairOutcome.OtherError(error.Type, error.Description);
        }

        return new PairOutcome.OtherError(-1, "Unrecognised response shape.");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static HttpClient CreateBridgeHttpClient(string ip, int port)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Bridge presents a self-signed cert ; we trust whoever
                // answers at the IP we explicitly chose. See class
                // header for the trade-off rationale.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        // Default port for the bridge is 443. The discovery endpoint
        // returns 443 explicitly ; older bridges that still expose
        // plain HTTP on 80 are out of scope for J2 (v2 firmware
        // forces HTTPS).
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{ip}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static string BuildDeviceType(string machineName)
    {
        // Sanitize : Hue rejects spaces and many punctuation chars in
        // the suffix. Keep alphanumeric + dash, fold the rest to dash,
        // cap at 33 chars to fit the 40-char total limit.
        Span<char> buffer = stackalloc char[DeviceTypeMaxSuffixLength];
        int length = 0;
        foreach (char c in machineName)
        {
            if (length >= buffer.Length) break;
            buffer[length++] = char.IsLetterOrDigit(c) || c == '-' ? c : '-';
        }
        var suffix = length == 0 ? "host" : new string(buffer[..length]);
        return DeviceTypePrefix + suffix;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    // ── DTOs and outcome types ──────────────────────────────────────

    private sealed class HuePairRequest
    {
        [JsonPropertyName("devicetype")]        public string DeviceType { get; set; } = "";
        [JsonPropertyName("generateclientkey")] public bool   GenerateClientKey { get; set; }
    }

    private sealed class HueApiResponseElement
    {
        [JsonPropertyName("success")] public HueSuccessPayload? Success { get; set; }
        [JsonPropertyName("error")]   public HueErrorPayload?   Error   { get; set; }
    }

    private sealed class HueSuccessPayload
    {
        [JsonPropertyName("username")]  public string Username  { get; set; } = "";
        [JsonPropertyName("clientkey")] public string ClientKey { get; set; } = "";
    }

    private sealed class HueErrorPayload
    {
        [JsonPropertyName("type")]        public int    Type        { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }

    private sealed class HueGroupDto
    {
        [JsonPropertyName("name")]   public string?   Name   { get; set; }
        [JsonPropertyName("lights")] public string[]? Lights { get; set; }
        [JsonPropertyName("type")]   public string?   Type   { get; set; }
    }

    // Body schema for PUT /groups/{id}/action and PUT /lights/{id}/state.
    // CLIP v1 accepts the exact same field set on both endpoints, so we
    // share a single DTO.
    private sealed class HueStateRequest
    {
        // Nullable on purpose — only the fields we set get serialised,
        // thanks to JsonIgnoreCondition.WhenWritingNull in _jsonOptions.
        // For black we send {"on":false,"transitiontime":1}, for a
        // colour {"on":true,"bri":...,"xy":[...],"transitiontime":1}.
        [JsonPropertyName("on")]             public bool?     On             { get; set; }
        [JsonPropertyName("bri")]            public byte?     Brightness     { get; set; }
        [JsonPropertyName("xy")]             public double[]? Xy             { get; set; }
        [JsonPropertyName("transitiontime")] public int?      TransitionTime { get; set; }
    }

    private sealed class HueLightDto
    {
        [JsonPropertyName("name")]  public string?            Name  { get; set; }
        [JsonPropertyName("type")]  public string?            Type  { get; set; }
        [JsonPropertyName("state")] public HueLightStateDto?  State { get; set; }
    }

    private sealed class HueLightStateDto
    {
        // The bridge returns many fields here (on, bri, xy, ct, alert, …).
        // We only project the reachability flag for now — the colour
        // pipeline pushes state, it doesn't read it back.
        [JsonPropertyName("reachable")] public bool? Reachable { get; set; }
    }

    private sealed class HueAlertRequest
    {
        // Hue CLIP v1 alert values : "none" (clear), "select" (one
        // breathe cycle), "lselect" (loop for ~15 s then auto-revert).
        // We only ever send "lselect" for the Identify pattern.
        [JsonPropertyName("alert")] public string Alert { get; set; } = "lselect";
    }

    // ── CLIP v2 DTOs ────────────────────────────────────────────────
    //
    // CLIP v2 wraps every collection response in `{"data":[...], "errors":[...]}`.
    // We only project the fields we consume here — the resources expose
    // many more (services, capabilities, …) we ignore.

    private sealed class HueV2Response<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
    }

    private sealed class HueV2LightDto
    {
        [JsonPropertyName("id")]       public string?         Id       { get; set; }
        // id_v1 is the legacy CLIP v1 path for this resource — e.g.
        // "/lights/3". We strip the prefix to recover the integer-as-
        // string id the rest of the codebase manipulates.
        [JsonPropertyName("id_v1")]    public string?         IdV1     { get; set; }
        [JsonPropertyName("metadata")] public HueV2Metadata?  Metadata { get; set; }
    }

    // /clip/v2/resource/entertainment exposes one entry per colour-capable
    // Hue light : the streaming endpoint attached to that light. Its
    // `id_v1` mirrors the underlying light's v1 path (e.g. "/lights/3"),
    // which is exactly what we need to map an entertainment_configuration
    // service_location's `rid` (always an entertainment uuid, never a
    // light uuid) back to the v1 light id.
    private sealed class HueV2EntertainmentServiceDto
    {
        [JsonPropertyName("id")]    public string? Id   { get; set; }
        [JsonPropertyName("id_v1")] public string? IdV1 { get; set; }
    }

    private sealed class HueV2EntertainmentConfigDto
    {
        [JsonPropertyName("id")]        public string?          Id        { get; set; }
        [JsonPropertyName("metadata")]  public HueV2Metadata?   Metadata  { get; set; }
        [JsonPropertyName("locations")] public HueV2Locations?  Locations { get; set; }
    }

    private sealed class HueV2Metadata
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class HueV2Locations
    {
        [JsonPropertyName("service_locations")] public List<HueV2ServiceLocation>? ServiceLocations { get; set; }
    }

    private sealed class HueV2ServiceLocation
    {
        [JsonPropertyName("service")]   public HueV2ResourceRef?    Service   { get; set; }
        // `position` (singular) is the legacy single-point form ;
        // `positions` is the newer multi-point form used for LED
        // strips. The bridge fills either or both ; the consumer
        // collapses them to a centroid.
        [JsonPropertyName("position")]  public HueV2Position?       Position  { get; set; }
        [JsonPropertyName("positions")] public List<HueV2Position>? Positions { get; set; }
    }

    private sealed class HueV2ResourceRef
    {
        [JsonPropertyName("rid")]   public string? Rid  { get; set; }
        [JsonPropertyName("rtype")] public string? Type { get; set; }
    }

    private sealed class HueV2Position
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
    }

    private abstract record PairOutcome
    {
        public sealed record Success(HueCredentials Credentials) : PairOutcome;
        public sealed record LinkButtonNotPressed : PairOutcome;
        public sealed record OtherError(int Type, string Description) : PairOutcome;
    }
}

// Bridge-side rejection of a pairing attempt for a reason other than
// "link button not pressed". Wraps the bridge's own error type code
// so callers can branch on known cases (e.g. type 7 = invalid value).
public sealed class HuePairingException : Exception
{
    public HuePairingException(string message) : base(message) { }
    public HuePairingException(string message, Exception inner) : base(message, inner) { }
}

// Transport-level failure : the bridge IP doesn't answer on TCP, the
// TLS handshake fails, or the DNS lookup fails. Distinct from
// HuePairingException so the UI can surface "check that the bridge is
// powered on and reachable" instead of "the bridge refused".
public sealed class HueBridgeUnreachableException : Exception
{
    public HueBridgeUnreachableException(string message) : base(message) { }
    public HueBridgeUnreachableException(string message, Exception inner) : base(message, inner) { }
}
