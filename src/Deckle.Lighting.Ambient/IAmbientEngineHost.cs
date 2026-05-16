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
// flag, and the Hue bridge credentials. The engine doesn't write
// back yet — a SaveSettings() method will be added here when the
// LightZoneSuggester (or another engine-side consumer) starts
// pre-populating LightZones at first connection.
public interface IAmbientEngineHost
{
    AmbientSettings Ambient { get; }
}
