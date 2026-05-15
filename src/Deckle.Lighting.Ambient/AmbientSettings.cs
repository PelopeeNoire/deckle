namespace Deckle.Lighting.Ambient;

// Container POCO grouping every Ambient-Light-scoped section under a
// single node persisted at <UserDataRoot>/modules/ambient/settings.json.
// Each module owns its own settings POCO; the consumer code reads from
// AmbientSettingsService.Instance.Current.
//
// J0c shipped only the master Enabled toggle. J3 step 2 adds the
// minimal Hue bridge state we need to skip the link-button dance on
// every app start : the bridge IP / id, the application username, and
// the last selected light group. The Entertainment v2 DTLS pre-shared
// key (clientkey) is NOT persisted — the REST path doesn't need it,
// and storing it would land a PSK in a JSON file we'd rather keep
// unsensitive. Subsequent milestones add the active mode (Realistic /
// Game), the game-specific profiles, and the monitor source override.
public sealed class AmbientSettings
{
    // Master toggle for the Ambient Light module. When false, the
    // AmbientEngine never starts — no screen capture, no Hue traffic,
    // zero CPU cost. Wiring of the runtime toggle happens in J3
    // (minimal end-to-end pipeline) ; until then the field is persisted
    // but has no consumer.
    public bool Enabled { get; set; } = false;

    // ── Hue bridge persistence (J3 step 2) ─────────────────────────
    //
    // Populated by the Playground after a successful Pair + group
    // selection. On subsequent app starts the Playground (and later
    // the AmbientPage) restores the HueBridgeClient from these values
    // without prompting the user to press the bridge's link button
    // again — the bridge keeps the username valid until manually
    // revoked from the Hue mobile app.
    //
    // All four properties default to null ; null means "not paired".
    // Cleared together when the user explicitly re-pairs (the new pair
    // overwrites them).

    /// <summary>LAN address of the paired bridge (e.g. 192.168.1.11).</summary>
    public string? HueBridgeIp { get; set; }

    /// <summary>Bridge serial / identifier (e.g. 001788FFFE3A2C18).</summary>
    public string? HueBridgeId { get; set; }

    /// <summary>Application username (CLIP API key) issued by the bridge
    /// at pairing. Sent on every REST call as the auth segment of the
    /// URL. Sensitive but recoverable — the user can revoke from the Hue
    /// mobile app.</summary>
    public string? HueUsername { get; set; }

    /// <summary>CLIP v1 id of the last group the user selected (e.g.
    /// "1", "5"). Used to pre-select the group in the Playground combo
    /// after restoring from settings.</summary>
    public string? HueLastGroupId { get; set; }
}
