using Deckle.Lighting.Ambient;

namespace Deckle;

// ── AppAmbientEngineHost ──────────────────────────────────────────────────────
//
// App-side implementation of IAmbientEngineHost. The engine reads its
// settings through this bridge so Deckle.Lighting.Ambient can stay
// free of any reference to the App project or to the shell
// SettingsService.
//
// Reads happen on every access (no caching), same posture as
// AppWhispEngineHost : a setting flipped through the AmbientPage takes
// effect on the next read with no event subscription needed.
internal sealed class AppAmbientEngineHost : IAmbientEngineHost
{
    public AmbientSettings Ambient => AmbientSettingsService.Instance.Current;
}
