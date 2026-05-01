namespace Deckle.Whisp;

// Guards against the Whisper hallucination loop: on a long audio with
// ambiguous trailing silence, the greedy decoder can enter a state where it
// emits the same segment forever. Observed 2026-04-18 — 84 identical
// segments on 237.7 s of audio with p̂ ≈ 0.99, so logprob_thold /
// entropy_thold never trip. We watch the segment stream ourselves and ask
// whisper.cpp to stop via abort_callback when the streak hits the threshold.
//
// V1 targets the observed case: N identical consecutive segments (case- and
// whitespace-insensitive). AB-AB alternation is not covered yet — upgrade
// when a real case surfaces.
internal sealed class RepetitionDetector  // engine-internal helper; only WhispEngine instantiates it
{
    private readonly int _threshold;
    private string? _lastText;
    private int _streak;

    public RepetitionDetector(int threshold = 5)
    {
        _threshold = threshold;
    }

    public void Reset()
    {
        _lastText = null;
        _streak = 0;
    }

    // Returns true the first time the streak hits the threshold. Caller is
    // expected to request whisper to abort and to log the trigger. Empty /
    // whitespace-only segments are ignored (they're common near silence and
    // would create spurious streaks).
    public bool ObserveAndShouldAbort(string segmentText, out int streak)
    {
        string norm = segmentText.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(norm))
        {
            streak = _streak;
            return false;
        }
        if (norm == _lastText)
        {
            _streak++;
        }
        else
        {
            _lastText = norm;
            _streak = 1;
        }
        streak = _streak;
        return _streak >= _threshold;
    }
}
