namespace Deckle.Logging;

// ── LoggingSettings ─────────────────────────────────────────────────────────
//
// Module-local POCO for the Logging section that lives in DiagnosticsPage
// alongside (and above) Telemetry. The two sections look similar in the
// UI but they cover orthogonal concerns and own separate stores :
//
//   Telemetry  → disk persistence opt-ins (privacy-loaded, consent
//                dialogs, gated paths in JsonlFileSink). Toggles answer
//                "what may land on the filesystem ?".
//   Logging    → runtime emission filters that drop events at the
//                TelemetryService source, before any sink (LogWindow,
//                JSONL pipeline) sees them. Toggles answer "how noisy
//                is the in-app log ?".
//
// Initial scope (J4 polish) is one toggle, VerboseLoggingEnabled. The
// section is structured so future filters — per-source mute, minimum
// level overrides, narrative-only quiet mode — sit alongside without
// restructuring either the page or this POCO. Add a new bool, wire it
// in DiagnosticsViewModel + DiagnosticsPage.xaml + Resources.resw,
// done : the LoggingSettingsService twin auto-persists.
public sealed class LoggingSettings
{
    /// <summary>When false (the default), <see cref="LogLevel.Verbose"/>
    /// emissions are dropped at the source — neither the LogWindow nor
    /// the app.jsonl sink see them, and the rolling history buffer
    /// doesn't grow with them either. Keeps the log quiet during normal
    /// use ; flip to true to inspect per-tick pipeline activity (ambient
    /// push lines, screen capture heartbeats, settings store writes …).</summary>
    public bool VerboseLoggingEnabled { get; set; } = false;
}
