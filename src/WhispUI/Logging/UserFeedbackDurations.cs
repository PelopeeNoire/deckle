using System;

namespace WhispUI.Logging;

// Hardcoded HUD display durations, tuned for read time by severity. Kept out
// of Settings deliberately — introducing a knob here adds complexity for a
// setting users rarely touch, and the values below are the outcome of a
// conscious product decision (warn/error linger, info clears quickly).
//
// Success is not a UserFeedbackSeverity value — success messages (Pasted,
// Copied) are emitted directly by HudWindow, not through UserFeedback. The
// Success constant exists so HudWindow can pull from the same source.
internal static class UserFeedbackDurations
{
    public static readonly TimeSpan Success = TimeSpan.FromSeconds(2);

    public static TimeSpan For(UserFeedbackSeverity severity) => severity switch
    {
        UserFeedbackSeverity.Info    => TimeSpan.FromSeconds(4),
        UserFeedbackSeverity.Warning => TimeSpan.FromSeconds(8),
        UserFeedbackSeverity.Error   => TimeSpan.FromSeconds(8),
        _                            => TimeSpan.FromSeconds(4),
    };
}
