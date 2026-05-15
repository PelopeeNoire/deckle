using System.Diagnostics;
using Deckle.Lighting;
using Deckle.Logging;
using Deckle.Vision;

namespace Deckle.Lighting.Ambient;

// Orchestrator hub for the ambient-lighting pipeline. The engine
// stitches an upstream ScreenCaptureService to a downstream
// ILightOutput via a FrameSampler that runs the analysis on the GPU.
// At each tick the engine reads the sampler's most recent
// SampledFrame.Average and pushes that colour to the light output —
// with an early-exit when the change is below the just-noticeable
// threshold so we don't spam the bridge on a static screen.
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
//   - The FrameSampler is borrowed, not owned. The caller (Playground
//     or AmbientPage) instantiates it from the capture service's
//     Device + ContentSize once the capture is running, and disposes
//     it when the pipeline stops. The engine never disposes the
//     sampler.
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
// Per-tick flow (J3 step 2).
//   1. Read FrameSampler.LatestSample (volatile read, may be null
//      until the first frame lands).
//   2. If null, skip and try again next tick.
//   3. Compare LatestSample.Average to _lastPushedColor ; if Δ
//      summed across R+G+B is below the early-exit threshold, skip
//      the push.
//   4. Push the colour via _output.SetColorAsync. Any exception is
//      logged as a Warning and the loop continues — a transient
//      bridge failure (Wi-Fi blip, group renamed mid-session) does
//      not kill the pipeline.
//   5. Wait PushIntervalMs (66 ms ≈ 15 Hz, aligned with the capture
//      pump cadence).
public sealed class AmbientEngine : IAsyncDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // Push cadence — 15 Hz matches the screen capture cadence set on
    // GraphicsCaptureSession.MinUpdateInterval. One frame in, one push
    // out (modulo the early-exit). 15 Hz is well within the REST CLIP
    // v1 sweet spot (10-20 Hz) for the Hue bridge.
    private const int PushHz = 15;
    private const int PushIntervalMs = 1000 / PushHz;

    // Early-exit threshold — if |ΔR| + |ΔG| + |ΔB| < this, the push
    // is skipped. 3 (out of 0-765 max) is conservative — it catches
    // FrameSampler quantisation noise on a static screen but lets
    // through anything the eye would notice. J5 will surface this
    // value in the Playground tuning panel.
    private const int ChangeThreshold = 3;

    private readonly ScreenCaptureService _capture;
    private readonly ILightOutput _output;
    private readonly FrameSampler _sampler;

    private CancellationTokenSource? _cts;
    private Task? _pushLoopTask;
    private long _startTimestamp;
    private bool _disposed;

    // Last colour we actually pushed to the bridge. Compared with the
    // sampler's most recent average to decide whether to push or
    // suppress. Default to (-1, -1, -1) so the first tick always pushes.
    private int _lastR = -1, _lastG = -1, _lastB = -1;
    private long _pushedCount;
    private long _droppedCount;

    public AmbientEngine(ScreenCaptureService capture, ILightOutput output, FrameSampler sampler)
    {
        _capture = capture;
        _output  = output;
        _sampler = sampler;
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
            $"start | source={(_capture.IsRunning ? "running" : "stopped")} | output={_output.GetType().Name} | push_hz={PushHz} | sampler_grid={_sampler.GridCols}x{_sampler.GridRows} | hdr={(_sampler.IsHdr ? "on" : "off")}");

        await _output.ConnectAsync(ct).ConfigureAwait(false);

        if (!_capture.IsRunning) _capture.Start();

        _cts = new CancellationTokenSource();
        _startTimestamp = Stopwatch.GetTimestamp();
        _pushedCount = 0;
        _droppedCount = 0;
        _lastR = _lastG = _lastB = -1;
        _pushLoopTask = Task.Run(() => AnalysisPushLoopAsync(_cts.Token), _cts.Token);

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
            $"stop | reason=user | duration_sec={durationSec:F1} | pushed={_pushedCount} | dropped={_droppedCount}");
    }

    private async Task AnalysisPushLoopAsync(CancellationToken ct)
    {
        // The loop runs on the thread-pool ; SetColorAsync goes through
        // HttpClient which is thread-safe. Any exception on a single
        // push is swallowed as a Warning so a transient bridge failure
        // (Wi-Fi blip, group renamed mid-session) does not kill the
        // loop — the next tick retries.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sample = _sampler.LatestSample;
                if (sample is null)
                {
                    // Sampler hasn't produced a frame yet (first ~66 ms
                    // after Start). Wait one cadence and retry.
                    await Task.Delay(PushIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                var avg = sample.Average;
                int delta = Math.Abs(avg.R - _lastR)
                          + Math.Abs(avg.G - _lastG)
                          + Math.Abs(avg.B - _lastB);
                bool dropped = _lastR >= 0 && delta < ChangeThreshold;

                if (dropped)
                {
                    _droppedCount++;
                    _log.Verbose(LogSource.Ambient,
                        $"push | mode=analysis | rgb={avg.R},{avg.G},{avg.B} | dropped=true");
                }
                else
                {
                    var color = new LightColor(avg.R, avg.G, avg.B);
                    try
                    {
                        await _output.SetColorAsync(color, ct).ConfigureAwait(false);
                        _lastR = avg.R; _lastG = avg.G; _lastB = avg.B;
                        _pushedCount++;
                        _log.Verbose(LogSource.Ambient,
                            $"push | mode=analysis | rgb={avg.R},{avg.G},{avg.B} | dropped=false");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.Warning(LogSource.Ambient,
                            $"Push failed — {ex.GetType().Name}: {ex.Message}");
                    }
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
                $"Analysis push loop crashed — {ex.GetType().Name}: {ex.Message}");
        }
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
