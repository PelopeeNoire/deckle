namespace Deckle.Lighting.Ambient;

// Placeholder — module bootstrap (J0b of feat/ambient-lighting).
//
// Deckle.Lighting.Ambient is the gaming-oriented consumer of the
// Lighting + Vision pair : it reads screen frames via Deckle.Vision,
// extracts colours per Hue Entertainment zone, smooths the result
// through interpolators from Deckle.Composition, and pushes the final
// frames through Deckle.Lighting.ILightOutput. Two canonical preset
// modes are exposed to the user (Realistic, Game/Ambilight) — fine
// tuning happens through the internal Playground, not through a slider
// surface. The third nested module Deckle.Lighting.Informative
// (Home Assistant data via LEDs) will land later when that use-case
// is attacked — the namespace is already shaped for it.
//
// Real content arrives in J0c (AmbientPage.xaml + AmbientSettings POCO
// + AmbientSettingsService, page wired into the Settings NavView),
// then J3 (AmbientEngine driving the minimal end-to-end pipeline) and
// onward. This placeholder only exists so the csproj compiles before
// any real type lives in the module.
internal static class Placeholder
{
}
