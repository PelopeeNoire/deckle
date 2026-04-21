namespace WhispUI.Logging;

// Severity for user-facing feedback surfaced in the HUD.
// Distinct from LogLevel: a Verbose/Info/Warning/Error entry can optionally
// carry a UserFeedback payload, whose severity drives HUD styling only.
public enum UserFeedbackSeverity { Info, Warning, Error }

// Optional payload attached to a TelemetryEvent when the producer wants the
// message to reach the user via the HUD (HudFeedbackSink picks it up).
// Title is the short headline; Body is the one-line actionable hint.
public sealed record UserFeedback(
    string Title,
    string Body,
    UserFeedbackSeverity Severity);
