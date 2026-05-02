using Deckle.Capture;
using Deckle.Llm;
using Deckle.Logging;
using Deckle.Settings;
using Deckle.Whisp;

namespace Deckle;

// ── AppWhispEngineHost ────────────────────────────────────────────────────────
//
// App-side implementation of IWhispEngineHost. The engine reads its
// settings through this bridge instead of touching SettingsService directly,
// so Deckle.Whisp stays free of any reference to the App project or to the
// root AppSettings POCO.
//
// Each property reads from SettingsService on every access — same posture
// as AppTelemetryGates: a setting flipped through the Settings UI takes
// effect on the next read, no caching, no event subscription needed.
internal sealed class AppWhispEngineHost : IWhispEngineHost
{
    public WhispSettings     Whisp     => SettingsService.Instance.Current.Whisp;
    public CaptureSettings   Capture   => SettingsService.Instance.Current.Capture;
    public TelemetrySettings Telemetry => SettingsService.Instance.Current.Telemetry;
    public LlmSettings       Llm       => SettingsService.Instance.Current.Llm;

    public string ResolveModelsDirectory() => SettingsService.Instance.ResolveModelsDirectory();

    public void SaveSettings() => SettingsService.Instance.Save();

    public void ApplyLevelWindow(LevelWindowSettings lw) => App.ApplyLevelWindow(lw);
}
