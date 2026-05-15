using System.Diagnostics;
using Deckle.Lighting;
using Deckle.Logging;
using Deckle.Vision;

namespace Deckle.Lighting.Ambient;

// Orchestrator hub for the ambient-lighting pipeline. The engine
// stitches an upstream ScreenCaptureService to a downstream
// ILightOutput, then either (J3 step 1) pushes a deterministic mock
// colour to validate the wiring + cadence, or (J3 step 2+) reads the
// captured frame, computes a colour, and pushes that.
//
// Why an engine at all (vs. wiring the events directly in the
// Playground or in AmbientPage code-behind) : we want a single
// well-tested object that owns the lifecycle (start, stop, dispose),
// the cadence (throttle pushes to a sane Hz against the bridge),
// and the error surface (one Warning when a push fails, never spam
// the LogWindow). Both the Playground (transient, manual start) and
// later the Settings AmbientPage (persistent toggle Enabled) will
// instantiate the same engine — only the trigger differs.
//
// Ownership.
//   - The ScreenCaptureService is borrowed, not owned. The engine
//     calls Start() if the service isn't running, but never Stop() —
//     the caller decided to construct the service and is responsible
//     for its disposal.
//   - The ILightOutput is borrowed, not owned. ConnectAsync is
//     called on Start ; DisposeAsync is NOT called on engine
//     teardown.
//   - The engine owns its CancellationTokenSource and the push
//     loop ; both are released on Stop / DisposeAsync.
//
// Lifecycle.
//   - Construct cheap, no I/O.
//   - StartAsync : ConnectAsync the output (idempotent), ensure
//     capture is running, kick the push loop. Returns once both are
//     up — exceptions surface from there.
//   - Stop : cancels the loop. Doesn't await the loop's finally —
//     the cancellation token is enough.
//   - DisposeAsync : Stop + dispose CTS. Idempotent.
//
// J3 step 1 (this file). The push loop sends a rotating HSV colour
// (full hue cycle in 10 s) at 5 Hz. The lamp visibly cycles through
// the rainbow, which makes the pipeline status immediately obvious
// without any screen content needed. The screen capture is started
// alongside the loop so the latency and CPU footprint of running it
// in parallel are observable in LogWindow / Task Manager.
//
// J3 step 2 (next session). The mock loop is replaced by an analysis
// loop that snapshots the most recent capture frame via a
// Direct3D11 staging texture, computes a subsampled average colour,
// and pushes that. The mock path is retained behind a flag for
// regression tests of the bridge driver in isolation.
public sealed class AmbientEngine : IAsyncDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // Push cadence — 5 Hz is well below the REST CLIP v1 sweet spot
    // (10-20 Hz), keeps the bridge responsive, and is high enough
    // that the rainbow rotation looks smooth visually.
    private const int PushHz = 5;
    private const int PushIntervalMs = 1000 / PushHz;

    // Mock colour rotation period — one full HSV cycle every 10 s.
    // Slow enough that the user can verify each colour is reached,
    // fast enough that a rebuild → see-rainbow loop stays brisk.
    private const double MockHueCycleSeconds = 10.0;

    private readonly ScreenCaptureService _capture;
    private readonly ILightOutput _output;

    private CancellationTokenSource? _cts;
    private Task? _pushLoopTask;
    private long _startTimestamp;
    private bool _disposed;

    public AmbientEngine(ScreenCaptureService capture, ILightOutput output)
    {
        _capture = capture;
        _output  = output;
    }

    /// <summary>True between a successful <see cref="StartAsync"/>
    /// and the matching <see cref="Stop"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Connects the output, ensures the capture service is running,
    /// and launches the push loop. Idempotent — calling on a running
    /// engine is a no-op. Throws if the output fails to connect ;
    /// the capture failure (much rarer) propagates from
    /// <see cref="ScreenCaptureService.Start"/> too.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        _log.Info(LogSource.Ambient, "Ambient pipeline started");
        _log.Verbose(LogSource.Ambient,
            $"start | capture_running={_capture.IsRunning} | output={_output.GetType().Name} | push_hz={PushHz}");

        await _output.ConnectAsync(ct).ConfigureAwait(false);

        if (!_capture.IsRunning) _capture.Start();

        _cts = new CancellationTokenSource();
        _startTimestamp = Stopwatch.GetTimestamp();
        _pushLoopTask = Task.Run(() => MockPushLoopAsync(_cts.Token), _cts.Token);

        IsRunning = true;
    }

    /// <summary>
    /// Stops the push loop. Doesn't stop the capture service or
    /// disconnect the output — both are borrowed (see class header).
    /// Idempotent.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        long endTimestamp = Stopwatch.GetTimestamp();
        double durationSec = (endTimestamp - _startTimestamp) / (double)Stopwatch.Frequency;

        try { _cts?.Cancel(); } catch { /* best effort */ }
        IsRunning = false;

        _log.Info(LogSource.Ambient, "Ambient pipeline stopped");
        _log.Verbose(LogSource.Ambient,
            $"stop | reason=user | duration_sec={durationSec:F1}");
    }

    private async Task MockPushLoopAsync(CancellationToken ct)
    {
        // The loop runs on the thread-pool ; SetColorAsync goes
        // through HttpClient which is thread-safe. Any exception on
        // a single push is swallowed as a Warning so a transient
        // bridge failure (Wi-Fi blip, group renamed mid-session) does
        // not kill the loop — the next tick retries.
        var sw = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double cyclePosition =
                    (sw.Elapsed.TotalSeconds / MockHueCycleSeconds) % 1.0;
                double hueDegrees = cyclePosition * 360.0;
                var color = HsvToRgb(hueDegrees, 1.0, 1.0);

                try
                {
                    await _output.SetColorAsync(color, ct).ConfigureAwait(false);
                    _log.Verbose(LogSource.Ambient,
                        $"push | mode=mock | hue={hueDegrees:F1} | rgb={color.R},{color.G},{color.B}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Warning(LogSource.Ambient,
                        $"Push failed — {ex.GetType().Name}: {ex.Message}");
                }

                await Task.Delay(PushIntervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop / DisposeAsync cancels the token.
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Ambient,
                $"Mock push loop crashed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── HSV → RGB ─────────────────────────────────────────────────
    //
    // Classic HSV to sRGB conversion (the Wikipedia formula). Hue is
    // in degrees [0, 360), Saturation and Value in [0, 1]. Used by
    // the mock loop to walk the rainbow at full saturation and
    // brightness — the user sees a continuously rotating colour on
    // the lamps as long as the engine runs.
    private static LightColor HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hh = h / 60.0;
        double x = c * (1 - Math.Abs((hh % 2) - 1));
        double m = v - c;

        double r1, g1, b1;
        switch ((int)hh)
        {
            case 0:  (r1, g1, b1) = (c, x, 0); break;
            case 1:  (r1, g1, b1) = (x, c, 0); break;
            case 2:  (r1, g1, b1) = (0, c, x); break;
            case 3:  (r1, g1, b1) = (0, x, c); break;
            case 4:  (r1, g1, b1) = (x, 0, c); break;
            default: (r1, g1, b1) = (c, 0, x); break;
        }

        byte R = (byte)Math.Clamp((int)Math.Round((r1 + m) * 255), 0, 255);
        byte G = (byte)Math.Clamp((int)Math.Round((g1 + m) * 255), 0, 255);
        byte B = (byte)Math.Clamp((int)Math.Round((b1 + m) * 255), 0, 255);
        return new LightColor(R, G, B);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_pushLoopTask is not null)
        {
            try { await _pushLoopTask.ConfigureAwait(false); }
            catch { /* logged in the loop already */ }
            _pushLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;
    }
}
