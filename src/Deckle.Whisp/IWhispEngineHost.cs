using Deckle.Llm;
using Deckle.Logging;

namespace Deckle.Whisp;

// ── IWhispEngineHost ──────────────────────────────────────────────────────────
//
// Bridge that lets the engine read its dependencies without touching App's
// SettingsService. The App-side implementation reads from SettingsService
// on each access; this keeps the engine free of any reference to the App
// project or to the root AppSettings POCO.
public interface IWhispEngineHost
{
    WhispSettings     Whisp     { get; }
    RecordingSettings Recording { get; }
    TelemetrySettings Telemetry { get; }
    LlmSettings       Llm       { get; }

    // Used by ResolveModelPath fallback. Returns the directory where Whisper
    // model .bin files live (typically <UserDataRoot>\modules\whisp\models\
    // or the legacy <UserDataRoot>\models\ during migration).
    string ResolveModelsDirectory();

    // Auto-calibration writes back to LevelWindow then asks the host to
    // persist. The host owns the SettingsService.Save() call.
    void SaveSettings();

    // Notify the host that LevelWindow values changed so it can push them
    // into HudChrono statics (App.ApplyLevelWindow). Called from the engine
    // after auto-calibration. Pass the live LevelWindowSettings.
    void ApplyLevelWindow(LevelWindowSettings lw);
}
