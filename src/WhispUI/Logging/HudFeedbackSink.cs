namespace WhispUI.Logging;

// Forwards TelemetryEvents that carry a UserFeedback payload to the right
// HUD surface, picked from UserFeedback.Role:
//   Replacement → main HudWindow slot (chrono swapped out).
//   Overlay     → stacked card above/below main HUD (HudOverlayManager).
//
// Events without feedback are ignored (they still reach LogWindow and the
// JSONL file sink via their own Write paths).
//
// The dispatch callbacks are responsible for UI thread marshaling — emission
// can happen from background threads. HudWindow.ShowUserFeedback and
// HudOverlayManager.Enqueue both handle this internally.
internal sealed class HudFeedbackSink : ITelemetrySink
{
    private readonly Action<UserFeedback> _onReplacement;
    private readonly Action<UserFeedback> _onOverlay;

    public HudFeedbackSink(
        Action<UserFeedback> onReplacement,
        Action<UserFeedback> onOverlay)
    {
        _onReplacement = onReplacement;
        _onOverlay     = onOverlay;
    }

    public void Write(TelemetryEvent ev)
    {
        if (ev.Feedback is null) return;

        if (ev.Feedback.Role == UserFeedbackRole.Overlay)
            _onOverlay(ev.Feedback);
        else
            _onReplacement(ev.Feedback);
    }
}
