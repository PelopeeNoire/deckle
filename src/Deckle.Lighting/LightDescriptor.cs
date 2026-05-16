namespace Deckle.Lighting;

// Driver-neutral identity for a single light addressable through an
// IMultiLightOutput. The Id is whatever the driver uses to address the
// light back to its protocol (CLIP v1 integer-as-string for Hue, entity
// id like "light.bedroom_strip" for Home Assistant, segment index for
// WLED). Consumers (UI, AmbientEngine) treat it as an opaque string.
//
// Name is the user-facing label set in the source-of-truth app (Hue
// mobile app for Hue, HA frontend for HA, etc.) — never translated,
// never mutated by Deckle.
//
// IsReachable mirrors the driver's most recent view of connectivity ;
// false doesn't prevent pushing colours (drivers queue the latest
// state internally), it's a UI signal so the user understands why a
// lamp isn't visibly responding.
public sealed record LightDescriptor(string Id, string Name, bool IsReachable);
