namespace WhispUI.Logging;

// ─── Log levels ──────────────────────────────────────────────────────────────
// Verbose : background noise (heartbeats, per-segment dumps, clipboard plumbing)
//           — only visible in "All" filter.
// Info    : normal workflow events (recording, return codes, text, copy, paste).
// Step    : rare verified milestones (model loaded, end-to-end OK).
// Warning : non-fatal issues (focus loss, empty buffers, repetition detected).
// Error   : failures (init errors, transcription failures, mic unavailable).
public enum LogLevel { Verbose, Info, Step, Warning, Error }

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; }
    public string Source  { get; }
    public string Message { get; }
    public LogLevel Level { get; }

    // Optional user-facing payload. When non-null, sinks routing to the user
    // (HudFeedbackSink) will surface it. File/LogWindow sinks ignore it.
    public UserFeedback? Feedback { get; }

    /// <summary>Formatted display text, computed once at creation.</summary>
    public string Text { get; }

    public LogEntry(string source, string message, LogLevel level, UserFeedback? feedback = null)
    {
        Timestamp = DateTimeOffset.Now;
        Source = source;
        Message = message;
        Level = level;
        Feedback = feedback;
        Text = source.Length > 0
            ? $"{Timestamp:HH:mm:ss.fff} [{Source}] {Message}"
            : $"{Timestamp:HH:mm:ss.fff} {Message}";
    }
}
