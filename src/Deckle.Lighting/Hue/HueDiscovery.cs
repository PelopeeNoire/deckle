using System.Net.Http.Json;
using Deckle.Logging;

namespace Deckle.Lighting.Hue;

// Bridge discovery on the local network. J2 only ships the cloud
// lookup path : a GET to discovery.meethue.com returns the IPs of the
// bridges that have phoned home from this WAN egress IP (typically
// everything on the user's local network). It's a Philips-hosted
// service but contains no auth-bearing data and works without an
// account — a thin convenience over the LAN-scan alternatives (mDNS
// `_hue._tcp.local.`, SSDP `IpBridge`).
//
// The mDNS path is the offline-friendly alternative ; it lands as a
// follow-up once the REST happy path is validated, since it requires
// either ~200 lines of DNS-over-UDP custom or a P/Invoke through
// `windns` / `DnsServiceBrowse`. For J2 first, cloud + manual IP fall-
// back covers the realistic cases (corporate firewall blocking the
// cloud lookup is rare for home users).
//
// The static HttpClient is intentionally process-wide. HttpClient is
// designed to be reused — instantiating one per call exhausts socket
// handles. The 10 s timeout matches the SLA Philips publishes for the
// discovery endpoint.
public static class HueDiscovery
{
    private const string CloudDiscoveryUrl = "https://discovery.meethue.com/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly LogService _log = LogService.Instance;

    /// <summary>
    /// Looks up Hue bridges reachable from the current WAN egress via
    /// the Philips-hosted discovery endpoint. Returns an empty list if
    /// no bridges are paired, or if the cloud service is unreachable
    /// — the latter case logs a Warning and surfaces nothing as an
    /// error so the caller can fall back to manual IP entry.
    /// </summary>
    public static async Task<IReadOnlyList<HueBridge>> DiscoverViaCloudAsync(CancellationToken ct = default)
    {
        _log.Info(LogSource.Hue, "Looking up Hue bridges");
        _log.Verbose(LogSource.Hue,
            $"discover start | source=cloud | url={CloudDiscoveryUrl}");

        try
        {
            var bridges = await _http.GetFromJsonAsync<HueBridge[]>(CloudDiscoveryUrl, ct)
                          ?? [];

            _log.Success(LogSource.Hue,
                $"Found {bridges.Length} Hue bridge{(bridges.Length == 1 ? "" : "s")}");
            foreach (var b in bridges)
            {
                _log.Verbose(LogSource.Hue,
                    $"discover result | bridge_id={b.Id} | bridge_ip={b.InternalIpAddress}");
            }
            return bridges;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Cloud lookup is a convenience, not a requirement — log
            // Warning and return empty so the UI can prompt for manual
            // IP entry. TaskCanceledException covers both the explicit
            // CancellationToken path and the HttpClient.Timeout firing.
            _log.Warning(LogSource.Hue,
                $"Cloud discovery failed — {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }
}
