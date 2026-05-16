namespace Deckle.Lighting.Ambient;

// Bridge that lets AmbientEngine read its settings without touching
// the App's SettingsService. Same posture as IWhispEngineHost — the
// App-side implementation forwards each access to
// AmbientSettingsService.Instance.Current so live edits made in the
// AmbientPage (or in any other surface) take effect on the next read
// without any event subscription.
//
// The engine reads its settings on every tick : the HDR tuning
// sliders (ExposureEv, SaturationBoost, MinBrightness), the zone
// assignments and the per-light brightness, the multi-light master
// flag, and the Hue bridge credentials. Writes are rarer — only the
// LightZoneSuggester pre-populates LightZones at first connection,
// and the host owns the Save() call.
public interface IAmbientEngineHost
{
    AmbientSettings Ambient { get; }

    // Persist the current AmbientSettings to disk. Called by the
    // engine after auto-populating LightZones from the entertainment
    // area at first connection — the user shouldn't lose those
    // suggestions on the next start.
    void SaveSettings();
}
