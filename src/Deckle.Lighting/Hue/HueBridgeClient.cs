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
