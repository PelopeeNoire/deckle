namespace Deckle.Lighting.Hue;

// Hue "light" — a single addressable bulb / strip / fixture inside a
// group. CLIP v1 exposes lights by integer-string id (e.g. "1", "5") ;
// the name is set by the user in the Hue mobile app. Type carries the
// device class ("Extended color light", "Color temperature light", …)
// — informational, the colour pipeline doesn't branch on it at V0 but
// the UI can use it to dim / hide entries that can't render RGB.
//
// Reachable is the bridge's view of whether the light has acknowledged
// a recent ping. Unreachable lights still accept PUT /state calls (the
// bridge queues the latest state), so we don't skip pushes — we just
// surface the flag in the UI so the user sees why the lamp isn't
// responding.
public sealed record HueLight(string Id, string Name, string Type, bool Reachable)
{
    /// <summary>Display label for a list item : "Sofa lamp" or "Sofa
    /// lamp (offline)" when the bridge reports the light unreachable.</summary>
    public string DisplayLabel => Reachable ? Name : $"{Name} (offline)";
}
