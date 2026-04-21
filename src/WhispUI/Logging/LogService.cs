namespace WhispUI.Logging;

// ── LogService ──────────────────────────────────────────────────────────────
//
// Thin façade over TelemetryService. Kept for source compatibility — every
// WhispUI caller already goes through `LogService.Instance.Info(...)` and
// the like, and rewriting the ~400 call sites to TelemetryService would
// bloat this chantier without changing behavior. The façade forwards the
// six levels unchanged.
//
// All sinks now live on TelemetryService. Emission still happens on the
// caller's thread; sinks decide their own marshaling.
public sealed class LogService
{
    public static LogService Instance { get; } = new();

    private LogService() { }

    public void Verbose  (string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Verbose,   feedback);
    public void Info     (string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Info,      feedback);
    public void Success  (string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Success,   feedback);
    public void Warning  (string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Warning,   feedback);
    public void Error    (string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Error,     feedback);
    public void Narrative(string source, string msg, UserFeedback? feedback = null) => TelemetryService.Instance.Log(source, msg, LogLevel.Narrative, feedback);
}
