namespace Deckle.Chrono;

// ── ChronoFormatter ───────────────────────────────────────────────────────────
//
// Pure formatting helpers for elapsed time. Decomposes a TimeSpan into
// (minutes, seconds, centiseconds) for digit-by-digit rendering, or
// produces a flat "MM:SS.cc" string for plain text consumers.
//
// `capSeconds` clamps the displayed elapsed to a hard ceiling. Useful
// when the UI mirrors a hard cap on recording duration — once the cap
// is hit the display stops advancing. 0 = no cap.
//
// Minutes are wrapped at 100 (% 100) to keep the digit-pair display
// stable for sessions longer than 99 minutes — overflow rolls back to
// 00 rather than overflowing the two-digit slot.
public static class ChronoFormatter
{
    public readonly record struct Decomposed(int Minutes, int Seconds, int Centiseconds);

    public static Decomposed Decompose(TimeSpan elapsed, int capSeconds = 0)
    {
        if (capSeconds > 0 && elapsed.TotalSeconds > capSeconds)
            elapsed = TimeSpan.FromSeconds(capSeconds);

        int totalMin = (int)elapsed.TotalMinutes;
        return new Decomposed(
            Minutes:      totalMin % 100,
            Seconds:      elapsed.Seconds,
            Centiseconds: elapsed.Milliseconds / 10);
    }

    public static string FormatMmSsCs(TimeSpan elapsed, int capSeconds = 0)
    {
        var d = Decompose(elapsed, capSeconds);
        return $"{d.Minutes:D2}:{d.Seconds:D2}.{d.Centiseconds:D2}";
    }
}
