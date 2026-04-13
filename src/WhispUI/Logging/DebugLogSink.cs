namespace WhispUI.Logging;

// Bridges LogService → DebugLog file output (%TEMP%\whisp-debug.log).
// Filters by minimum level (default: Info) to avoid flooding the file
// with Verbose noise. DebugLog.cs itself stays untouched — it still works
// for crash handlers and bootstrap that fire before sinks are registered.
internal sealed class DebugLogSink : ILogSink
{
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public void Write(LogEntry entry)
    {
        if (entry.Level >= MinLevel)
            DebugLog.Write(entry.Source, entry.Message);
    }
}
