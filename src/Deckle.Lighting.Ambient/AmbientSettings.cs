namespace Deckle.Lighting.Ambient;

// Container POCO grouping every Ambient-Light-scoped section under a
// single node persisted at <UserDataRoot>/modules/ambient/settings.json.
// Each module owns its own settings POCO; the consumer code reads from
// AmbientSettingsService.Instance.Current.
//
// Minimal at J0c — only the master Enabled toggle exists, defaulted to
// off so the module stays inert until the user opts in. Subsequent
// milestones add the Hue bridge connection state, the active mode
// (Realistic / Game), the game-specific profiles, and the monitor
// source override.
public sealed class AmbientSettings
{
    // Master toggle for the Ambient Light module. When false, the
    // AmbientEngine never starts — no screen capture, no Hue traffic,
    // zero CPU cost. Wiring of the runtime toggle happens in J3
    // (minimal end-to-end pipeline) ; until then the field is persisted
    // but has no consumer.
    public bool Enabled { get; set; } = false;
}
