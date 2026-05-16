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
// Filter shape : (capture-active window) × Verbose × source set.
// The flag <see cref="TelemetryService.SetCaptureActive"/> delimits
// the temporal window — only AmbientEngine sets it, only around its
// push loop, and the central filter in TelemetryService.Log consults
// it on every emission. A level-only filter cannot tell a Verbose
// push line from a Verbose user-action mirror (same source, same
// level), but the temporal scope can : a user action emitted before
// the engine starts, after it stops, or during an idle session is
// outside the window and passes through. A Verbose user action that
// happens to land inside the window IS filtered, accepted trade-off
// because the matching Info Capital sentence stays visible in
// Activity (cf. Verbose/Info doctrine elsewhere in this section).
//
// Initial scope (J4 polish) : one toggle for the ambient capture
// loop. The same shape will host Whisp transcription loop, Audio
// capture loop, etc. as each becomes worth silencing on its own.
public sealed class LoggingSettings
{
    /// <summary>When false (the default), every Verbose-level log
    /// emitted from <c>AMBIENT</c>, <c>SCREEN</c> or <c>HUE</c> while
    /// the AmbientEngine capture loop is active is dropped at
    /// <see cref="TelemetryService.Log"/>. The capture-active window
    /// is delimited by <see cref="TelemetryService.SetCaptureActive"/>
    /// which the engine flips on right before launching its push loop
    /// and off as the very first step of <c>Stop</c> — so the
    /// pipeline milestones (<c>Ambient pipeline started/stopped</c>)
    /// and their Verbose diagnostic mirrors (<c>start | …</c> /
    /// <c>stop | …</c>) sit outside the window and pass through.
    ///
    /// Anything emitted before the loop starts (group resolution,
    /// lights listing, sampler init, zone suggestions) or after it
    /// stops (final stop mirror) is also outside the window and
    /// unaffected. Non-Verbose levels (Info / Success / Warning /
    /// Error / Narrative) pass through unconditionally — milestones
    /// and faults always reach the LogWindow regardless of the
    /// toggle. User actions from the Playground (zone assign Verbose
    /// mirror, settings update Verbose mirror) emitted while the
    /// engine happens to be running ARE filtered ; the matching Info
    /// Capital sentence still reaches Activity, so the user sees
    /// what they did either way.
    ///
    /// Default <c>false</c> : the ambient pipeline emits a steady
    /// cadence of per-tick traffic from three modules (engine, screen
    /// capture, Hue REST client) that drowns out everything else
    /// during play. Flip to <c>true</c> only when investigating
    /// engine behaviour or calibrating a new tunable.</summary>
    public bool LogAmbientCaptureActivity { get; set; } = false;
}
