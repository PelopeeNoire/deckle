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
//                emitting module (gated at the call site, not in
//                TelemetryService). Toggles answer "which slice of
//                runtime activity should land in the LogWindow ?".
//
// Distinction from a hypothetical "Verbose × source" filter : the
// dimension is **temporal scope**, not log level. The user wants to
// silence the routine traffic of a runtime LOOP (push-per-tick,
// heartbeat, frame stats) while keeping milestones and user actions
// fully visible — even at Verbose level. A level-based filter cannot
// distinguish "Verbose mirror of a user action" from "Verbose noise
// of the push loop" because both share source + level. The toggle is
// therefore consumed AT THE CALL SITE inside the runtime loop, not in
// a central filter.
//
// Initial scope (J4 polish) : one toggle for the ambient capture
// loop. The same shape will host Whisp transcription loop, Audio
// capture loop, etc. as each becomes worth silencing on its own.
public sealed class LoggingSettings
{
    /// <summary>When false, the high-frequency log lines emitted by
    /// the <see cref="Deckle.Lighting.Ambient.AmbientEngine"/> push
    /// loop (per-tick <c>push</c> lines, periodic <c>heartbeat</c>)
    /// are suppressed AT THE CALL SITE — the engine reads this flag
    /// before each emission and skips when off. The pipeline
    /// milestones (<c>Ambient pipeline started/stopped</c>, their
    /// Verbose <c>start | …</c> / <c>stop | …</c> diagnostic mirrors)
    /// and every user action triggered from the Playground or
    /// AmbientPage (zone assign, mode change, group selection) stay
    /// visible regardless — those don't fire from the runtime loop.
    /// Warnings and errors from the loop also pass through, since
    /// they're emitted via <see cref="LogLevel.Warning"/> / Error
    /// and are unconditional.
    ///
    /// Default <c>true</c> : the user explicitly called these logs
    /// "very important" — they describe what the engine is actually
    /// doing tick by tick and are the first thing to consult when
    /// the rendered output looks wrong. Flip to false only while
    /// playing, to keep the LogWindow readable.</summary>
    public bool LogAmbientCaptureActivity { get; set; } = true;
}
