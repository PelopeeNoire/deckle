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
// Initial scope (J4 polish) : one toggle per Deckle module, starting
// with the ambient-lighting pipeline. The same pattern will host the
// Whisp / Audio / LLM / Settings toggles as the user calls them out —
// the user wanted one knob per family, not one global verbosity
// switch. Add a new bool, wire it in DiagnosticsViewModel +
// DiagnosticsPage.xaml + Resources.resw, extend
// TelemetryService.Log()'s source-set match, done.
public sealed class LoggingSettings
{
    /// <summary>When false (the default), log emissions tagged with
    /// one of the ambient-pipeline sources (<c>AMBIENT</c>,
    /// <c>SCREEN</c>, <c>HUE</c>) are dropped at the
    /// <see cref="TelemetryService"/> source — neither the LogWindow
    /// nor the app.jsonl sink see them, and the rolling history buffer
    /// doesn't grow with them either. Off by default because the
    /// ambient pipeline emits a steady cadence of routine traffic that
    /// drowns out the events worth reading ; flip on only when
    /// investigating an ambient-specific issue.</summary>
    public bool LogAmbientLighting { get; set; } = false;
}
