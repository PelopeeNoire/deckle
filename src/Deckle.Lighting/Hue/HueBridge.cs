using System.Text.Json.Serialization;

namespace Deckle.Lighting.Hue;

// Passive description of a Philips Hue bridge on the local network.
// Identification data only — no credentials. The pairing exchange
// returns a separate HueCredentials record so the IP can be logged
// freely while the username / clientkey stay quarantined.
//
// JSON shape matches the response from `discovery.meethue.com` :
//   [{"id":"001788FFFE3A2C18","internalipaddress":"192.168.1.5","port":443}]
// — note the all-lowercase `internalipaddress`, not snake_case.
public sealed record HueBridge(
    [property: JsonPropertyName("id")]                string Id,
    [property: JsonPropertyName("internalipaddress")] string InternalIpAddress,
    [property: JsonPropertyName("port")]              int Port);

// Credentials returned by the bridge after a successful pairing
// (link-button pressed). Held separately from HueBridge to keep the
// secret-bearing fields away from the logging happy-path.
//
// `Username` is the CLIP application key — sent on every REST call as
// `?username=` or in the `hue-application-key` header for v2. Sensitive
// but not catastrophic if exposed (bridge owner can revoke from the
// Hue app).
//
// `ClientKey` is the PSK reserved for Entertainment v2 DTLS handshake.
// Never used by the REST path J2 takes, but the bridge issues it
// regardless when `generateclientkey:true` is set on pairing. Treated
// as a true secret : never logged in clear, never persisted without
// explicit user consent.
public sealed record HueCredentials(string Username, string ClientKey)
{
    /// <summary>First 8 characters of the username followed by `...`,
    /// for safe display in logs and diagnostic UI. Length is fixed so
    /// the value never accidentally reveals the full token through a
    /// formatter that truncates differently.</summary>
    public string UsernameHead => Username.Length <= 8
        ? Username
        : string.Concat(Username.AsSpan(0, 8), "...");
}
