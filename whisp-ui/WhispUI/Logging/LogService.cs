namespace WhispUI.Logging;

// Sink interface: anything that wants to receive log entries.
public interface ILogSink
{
    void Write(LogEntry entry);
}

// Central logging service — singleton, thread-safe.
// Callers use: LogService.Instance.Info(LogSource.Model, "...");
//
// Entry creation happens here (once). All sinks receive the same immutable
// LogEntry instance. Sinks are dispatched on the caller's thread — each sink
// is responsible for its own marshaling (e.g. LogWindow uses DispatcherQueue).
public sealed class LogService
{
    public static LogService Instance { get; } = new();

    private readonly List<ILogSink> _sinks = new();
    private readonly object _sinkLock = new();

    private LogService() { }

    public void AddSink(ILogSink sink)
    {
        lock (_sinkLock) _sinks.Add(sink);
    }

    public void RemoveSink(ILogSink sink)
    {
        lock (_sinkLock) _sinks.Remove(sink);
    }

    // ── Public API (one method per level) ────────────────────────────────────
    public void Verbose(string source, string msg) => Emit(source, msg, LogLevel.Verbose);
    public void Info(string source, string msg)    => Emit(source, msg, LogLevel.Info);
    public void Step(string source, string msg)    => Emit(source, msg, LogLevel.Step);
    public void Warning(string source, string msg) => Emit(source, msg, LogLevel.Warning);
    public void Error(string source, string msg)   => Emit(source, msg, LogLevel.Error);

    private void Emit(string source, string message, LogLevel level)
    {
        var entry = new LogEntry(source, message, level);

        // Snapshot: no lock held during dispatch — sinks can take time
        // without blocking other log calls.
        ILogSink[] snapshot;
        lock (_sinkLock) snapshot = _sinks.ToArray();

        foreach (var sink in snapshot)
        {
            try { sink.Write(entry); }
            catch { /* A sink must never crash the caller. */ }
        }
    }
}
