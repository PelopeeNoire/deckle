namespace WhispUI.Logging;

// Forwards TelemetryEvents that carry a UserFeedback payload to a
// dispatcher, typically bound to HudWindow.ShowUserFeedback. Events
// without feedback are ignored (they still reach LogWindow and the
// JSONL file sink via their own Write paths).
//
// The dispatch callback is responsible for UI thread marshaling —
// emission can happen from background threads. HudWindow already
// handles this via DispatcherQueue.TryEnqueue.
internal sealed class HudFeedbackSink : ITelemetrySink
{
    private readonly Action<UserFeedback> _dispatch;

    public HudFeedbackSink(Action<UserFeedback> dispatch)
    {
        _dispatch = dispatch;
    }

    public void Write(TelemetryEvent ev)
    {
        if (ev.Feedback is not null)
            _dispatch(ev.Feedback);
    }
}
