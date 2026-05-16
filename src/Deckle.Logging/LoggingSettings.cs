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
//                JSONL pipeline) sees them. Toggles answer "which
//                subsystem may emit in the log ?".
//
// Initial scope (J4 polish) : one toggle per Deckle module for the
// Verbose-level traffic of that module. Starting with the ambient
// lighting family (AMBIENT / SCREEN / HUE), the same pattern will host
// Verbose toggles for Whisp / Audio / LLM / Settings as each becomes
// worth silencing on its own. The intent is "show me the milestones
// and warnings of every module, but hide the per-tick chatter of the
// ones I'm not actively investigating". Add a new bool, wire it in
// DiagnosticsViewModel + DiagnosticsPage.xaml + Resources.resw, extend
// TelemetryService.Log()'s source-set match for that module, done.
public sealed class LoggingSettings
{
    /// <summary>When false (the default), <see cref="LogLevel.Verbose"/>
    /// emissions tagged with one of the ambient-pipeline sources
    /// (<c>AMBIENT</c>, <c>SCREEN</c>, <c>HUE</c>) are dropped at the
    /// <see cref="TelemetryService"/> source. Info / Success / Warning /
    /// Error / Narrative events from those same sources pass through
    /// untouched — the workflow milestones (pipeline started, group
    /// selected, bridge unreachable…) remain visible in the LogWindow.
    /// The toggle exists to silence the high-frequency per-tick noise
    /// (push lines, heartbeats, sampler diagnostics) that drown out
    /// everything else while a game is running, without losing the
    /// useful events that bracket the activity.</summary>
    public bool VerboseAmbientLighting { get; set; } = false;
}
