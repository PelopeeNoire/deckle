using System.Diagnostics;

namespace Deckle.Chrono;

// ── ChronoTimer ───────────────────────────────────────────────────────────────
//
// Thin wrapper around System.Diagnostics.Stopwatch. Exists so consumers
// don't have to inline a Stopwatch field every time they want a "time
// since trigger" reading, and so the hosting visual (HudChrono UserControl
// in Deckle.Chrono.Hud) can be fed by an external timer when the source
// of timing is something other than its own start.
//
// Reusable by Ask-Ollama, spell-correction, or any module that wants to
// surface elapsed time without pulling the chrono visual.
//
// The `Start` semantics matches `Stopwatch.Restart` — it resets to zero
// and begins counting. Use `Resume` if you ever need to continue from a
// paused state without resetting (currently unused in shipping Deckle,
// listed for future module needs).
public sealed class ChronoTimer
{
    private readonly Stopwatch _sw = new();

    public bool     IsRunning => _sw.IsRunning;
    public TimeSpan Elapsed   => _sw.Elapsed;

    // Reset to zero and start counting.
    public void Start()  => _sw.Restart();

    // Stop counting; Elapsed retains the final value until Reset or Start.
    public void Stop()   => _sw.Stop();

    // Continue counting from the current Elapsed. No-op if already running.
    public void Resume() => _sw.Start();

    // Stop and zero out. Elapsed reads as TimeSpan.Zero afterwards.
    public void Reset()  => _sw.Reset();
}
