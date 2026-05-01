namespace Deckle.Logging;

// Severity for user-facing feedback surfaced in the HUD.
// Distinct from LogLevel: a Verbose/Info/Warning/Error entry can optionally
// carry a UserFeedback payload, whose severity drives HUD styling only.
public enum UserFeedbackSeverity { Info, Warning, Error }

// Routing role for a UserFeedback, independent of severity.
//   Replacement  — the workflow stopped or is moot; the message occupies the
//                  main HUD slot (chrono swapped out). Behavior matches the
//                  legacy single-message HUD.
//   Overlay      — the workflow continues (or a terminal state is already
//                  shown in the main HUD); the message appears as a separate
//                  stacked card above/below the main HUD, without interrupting
//                  it. Handled by HudOverlayManager.
//
// Every emitter knows this at the call site: if the code returns / aborts
// right after emission, it is Replacement; if recording / copy still happens,
// it is Overlay.
public enum UserFeedbackRole { Replacement, Overlay }

// Optional payload attached to a TelemetryEvent when the producer wants the
// message to reach the user via the HUD (HudFeedbackSink picks it up).
// Title is the short headline; Body is the one-line actionable hint.
// Role defaults to Replacement — safe fallback for any call site not yet
// annotated (matches legacy behavior: main HUD slot).
public sealed record UserFeedback(
    string Title,
    string Body,
    UserFeedbackSeverity Severity,
    UserFeedbackRole Role = UserFeedbackRole.Replacement);
