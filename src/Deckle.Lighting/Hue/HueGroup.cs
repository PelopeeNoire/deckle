namespace Deckle.Lighting.Hue;

// Hue "group" — the unit of control for the REST API. Maps to a
// Room (e.g. "Salon"), a Zone, or an Entertainment area in the user's
// Hue app. CLIP v1 doesn't distinguish at the API surface — they're
// all addressable by group_id and accept the same `/action` payload.
//
// LightsCount is the cached length of the `lights` array from the
// bridge response, kept here so the UI can format "Salon (3 lights)"
// without re-fetching. Type is the bridge's own classification —
// "Room", "Zone", "Entertainment", "LightGroup", etc. — passed
// through verbatim so a future filter (e.g. only Entertainment zones
// for the J7 HDR path) can branch on it without round-tripping the
// bridge.
public sealed record HueGroup(string Id, string Name, string Type, int LightsCount)
{
    /// <summary>Display string suitable for a ComboBox item :
    /// "Salon (3 lights)" or "Salon (Room — 3 lights)" depending on
    /// whether the type is informative for the caller.</summary>
    public string DisplayLabel => LightsCount switch
    {
        0 => $"{Name} ({Type}, no lights)",
        1 => $"{Name} ({Type}, 1 light)",
        _ => $"{Name} ({Type}, {LightsCount} lights)",
    };
}
