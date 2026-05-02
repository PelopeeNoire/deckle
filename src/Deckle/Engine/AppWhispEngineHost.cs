using Deckle.Capture;
using Deckle.Llm;
using Deckle.Logging;
using Deckle.Whisp;

namespace Deckle;

// ── AppWhispEngineHost ────────────────────────────────────────────────────────
//
// App-side implementation of IWhispEngineHost. The engine reads its
// settings through this bridge so Deckle.Whisp can stay free of any
// reference to the App project or to the shell SettingsService.
//
// After slice C2b each module owns its own settings file
// (modules/<id>/settings.json) and its own static service singleton —
// so this bridge just forwards to the relevant XxxSettingsService.Instance.
// Reads happen on every access (no caching), same posture as
// AppTelemetryGates: a setting flipped through the Settings UI takes
// effect on the next read with no event subscription needed.
internal sealed class AppWhispEngineHost : IWhispEngineHost
{
    public WhispSettings     Whisp     => WhispSettingsService.Instance.Current;
    public CaptureSettings   Capture   => CaptureSettingsService.Instance.Current;
    public TelemetrySettings Telemetry => TelemetrySettingsService.Instance.Current;
    public LlmSettings       Llm       => LlmSettingsService.Instance.Current;

    public string ResolveModelsDirectory() => WhispSettingsService.Instance.ResolveModelsDirectory();

    // The single engine-side caller is the auto-calibration path which
    // mutates Capture.LevelWindow in place. So saving Capture is the
    // only Save the engine drives. If a future engine path needs to
    // save a different module, add a typed hook on IWhispEngineHost
    // rather than overloading this one.
    public void SaveSettings() => CaptureSettingsService.Instance.Save();

    public void ApplyLevelWindow(LevelWindowSettings lw) => App.ApplyLevelWindow(lw);
}
