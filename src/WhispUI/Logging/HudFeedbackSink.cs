namespace WhispUI.Logging;

// Forwards log entries that carry a UserFeedback payload to a dispatcher,
// typically bound to HudWindow.ShowUserFeedback. Entries without feedback
// are ignored (they still reach LogWindow / DebugLog via their own sinks).
//
// The dispatch callback is responsible for UI thread marshaling — log
// emission can happen from background threads. HudWindow already handles
// this via DispatcherQueue.TryEnqueue.
internal sealed class HudFeedbackSink : ILogSink
{
    private readonly Action<UserFeedback> _dispatch;

    public HudFeedbackSink(Action<UserFeedback> dispatch)
    {
        _dispatch = dispatch;
    }

    public void Write(LogEntry entry)
    {
        if (entry.Feedback is not null)
            _dispatch(entry.Feedback);
    }
}
