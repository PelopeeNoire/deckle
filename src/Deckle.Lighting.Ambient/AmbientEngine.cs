using System.Diagnostics;
using Deckle.Lighting;
using Deckle.Logging;
using Deckle.Vision;

namespace Deckle.Lighting.Ambient;

// Orchestrator hub for the ambient-lighting pipeline. The engine
// stitches an upstream ScreenCaptureService to a downstream
// ILightOutput via a FrameSampler that runs the analysis on the GPU.
// At each tick the engine reads the sampler's most recent
// SampledFrame and pushes colour(s) to the output — with per-channel
// early-exit thresholds so a static screen doesn't spam the bridge.
//
// Two pipeline shapes, selected at Start time.
//
//   - Group mode (default). One sRGB average over the whole frame, one
//     push to the connected group via <see cref="ILightOutput.SetColorAsync"/>.
//     Every driver supports this path. Cadence : 15 Hz, matched with
//     the capture pump.
//
//   - Multi-light mode. One sRGB sample per screen-border zone (top /
//     bottom / left / right), broadcast to every light assigned to
//     that zone via <see cref="LightZone"/>, pushed in a single batch
//     via <see cref="IMultiLightOutput.SetLightColorsAsync"/>. Only
//     enabled when the driver implements <see cref="IMultiLightOutput"/>
//     AND the caller passes a non-empty zone assignment dictionary AND
//     `useMultiLight=true`. Cadence is throttled to 10 Hz to keep total
//     per-second PUT count within the Hue REST CLIP v1 comfort zone
//     for a typical 3-5 light setup (3 lights × 10 Hz = 30 PUT/s).
//     The four zones are sampled once per tick from a band of
//     <see cref="BorderDepth"/> on the matching edge of the frame —
//     this mirrors HyperHDR's <c>horizontalDepth</c> / <c>verticalDepth</c>
//     concept, collapsed to a single shared constant since V1 doesn't
//     support sub-zone positioning.
//
// If multi-light is requested but the driver doesn't expose the
// capability, the engine logs a warning and falls back to group mode
// transparently — the user still gets ambient lighting, just not the
// zoned variant.
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
//   - StartAsync : ConnectAsync the output (idempotent), pick the
//     pipeline shape based on capability + placements, ensure capture
//     is running, kick the push loop. Returns once both are up —
//     exceptions surface from there.
//   - Stop : cancels the loop. Doesn't await the loop's finally —
//     the cancellation token is enough.
//   - DisposeAsync : Stop + dispose CTS. Idempotent.
public sealed class AmbientEngine : IAsyncDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // Push cadence — group mode. 15 Hz matches the screen capture
    // cadence set on GraphicsCaptureSession.MinUpdateInterval. One
    // frame in, one push out (modulo the early-exit). 15 Hz is well
    // within the REST CLIP v1 sweet spot (10-20 Hz) for the Hue bridge.
    private const int GroupPushHz = 15;

    // Push cadence — multi-light mode. Each tick fans out N parallel
    // PUTs (one per light), so the effective per-second pressure on
    // the bridge is N × Hz. 10 Hz × 3 lights = 30 PUT/s, still within
    // the bridge's comfort zone (Philips guidance is "no faster than
    // 10 Hz for lights, 1 Hz for groups" — we're at the lights ceiling
    // and the consumer rate-limit knob lives here).
    private const int MultiPushHz = 10;

    // Early-exit threshold — if |ΔR| + |ΔG| + |ΔB| < this, the push
    // is skipped. 3 (out of 0-765 max) is conservative — it catches
    // FrameSampler quantisation noise on a static screen but lets
    // through anything the eye would notice. J5 will surface this
    // value in the Playground tuning panel.
    private const int ChangeThreshold = 3;

    // Lights-out threshold — if every channel of the analysed average
    // is at or below this, we clamp the colour to (0,0,0) before the
    // push. HueColorMath maps pure black to bri=0 which the bridge
    // client translates into on:false (lamp off) ; without the clamp,
    // a near-black sample like (5,5,5) maps to bri≈2 and the lamp
    // stays faintly on instead of going dark when the screen is dark.
    // J5 will surface this in the Playground tuning panel ; for V0 we
    // keep it conservative (8 / 255 ≈ 3 %) so it only triggers on
    // unambiguously dark content (lock screen, off display).
    private const int OffThreshold = 8;

    // Border-band depth used by the four zone-sampling helpers, as a
    // fraction of the matching screen dimension. 0.20 ≈ HyperHDR's
    // default `horizontalDepth` / `verticalDepth` range (15-25 %).
    // The top zone reads pixels in y ∈ [0, BorderDepth], the bottom
    // zone in y ∈ [1-BorderDepth, 1], the left in x ∈ [0, BorderDepth],
    // the right in x ∈ [1-BorderDepth, 1]. Constants kept symmetric
    // (one depth for both axes) since a typical user setup wraps
    // around the screen symmetrically. J5 will expose this on the
    // Playground panel for tuning.
    public const double BorderDepth = 0.20;

    // Heartbeat cadence for the push-loop telemetry. The per-tick
    // "push" log used to fire 10-15 times a second on a steady
    // screen, flooding the LogWindow with identical lines. We now
    // log a per-tick line only on an actual colour change, and roll
    // up the rest into a single heartbeat every N seconds so the
    // pipeline still reports it's alive without producing 300 lines
    // for a 30-second session.
    private const int HeartbeatIntervalMs = 5000;

    private readonly ScreenCaptureService _capture;
    private readonly ILightOutput _output;
    private readonly FrameSampler _sampler;
    private readonly IReadOnlyDictionary<string, LightZone>? _zoneAssignments;
    private readonly IReadOnlyDictionary<string, double>? _lightBrightness;
    private readonly bool _useMultiLightRequested;

    private CancellationTokenSource? _cts;
    private Task? _pushLoopTask;
    private long _startTimestamp;
    private bool _disposed;

    // Group-mode state — last colour we actually pushed to the bridge.
    // Compared with the sampler's most recent average to decide whether
    // to push or suppress. Default to (-1, -1, -1) so the first tick
    // always pushes.
    private int _lastR = -1, _lastG = -1, _lastB = -1;

    // Multi-light-mode state — last colour pushed per light id. The
    // dictionary lives for the engine session ; cleared on Stop.
    private Dictionary<string, (int R, int G, int B)>? _multiLastPushed;

    // Resolved multi-light fixture list (driver-reported) at Start time.
    // Null when group mode is active.
    private IReadOnlyList<LightDescriptor>? _multiLights;

    // Active pipeline shape. Set in StartAsync, read by the loop.
    private bool _multiLightActive;
    private int _pushIntervalMs = 1000 / GroupPushHz;

    private long _pushedCount;
    private long _droppedCount;

    // Heartbeat accumulators — counted by the push loop between log
    // emissions and reset every HeartbeatIntervalMs. Distinct from
    // the cumulative session counters above (which feed the stop
    // summary) so a heartbeat shows recent activity, not the whole
    // session-to-date.
    private long _hbTimestamp;
    private int  _hbTicks;
    private int  _hbPushed;
    private int  _hbDropped;
    private int  _hbUnmappedLights;

    public AmbientEngine(
        ScreenCaptureService capture,
        ILightOutput output,
        FrameSampler sampler,
        IReadOnlyDictionary<string, LightZone>? zoneAssignments = null,
        IReadOnlyDictionary<string, double>? lightBrightness = null,
        bool useMultiLight = false)
    {
        _capture                = capture;
        _output                 = output;
        _sampler                = sampler;
        _zoneAssignments        = zoneAssignments;
        _lightBrightness        = lightBrightness;
        _useMultiLightRequested = useMultiLight;
    }

    /// <summary>True between a successful <see cref="StartAsync"/>
    /// and the matching <see cref="Stop"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True when the engine resolved to multi-light mode at
    /// Start. Useful for the UI to display the active pipeline shape.</summary>
    public bool IsMultiLightActive => _multiLightActive;

    /// <summary>Lights resolved from the driver when multi-light mode is
    /// active. Null when the engine isn't running or when group mode is
    /// active.</summary>
    public IReadOnlyList<LightDescriptor>? MultiLights => _multiLights;

    /// <summary>
    /// Connects the output, picks the pipeline shape (group vs multi-
    /// light), ensures the capture service is running, and launches the
    /// push loop. Idempotent — calling on a running engine is a no-op.
    /// Throws if the output fails to connect ; the capture failure
    /// (much rarer) propagates from <see cref="ScreenCaptureService.Start"/>
    /// too.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        await _output.ConnectAsync(ct).ConfigureAwait(false);

        // Resolve pipeline shape after Connect (ListLightsAsync needs
        // IsConnected). Multi-light requires : caller said yes, driver
        // exposes the capability, and the driver reports at least one
        // addressable light.
        if (_useMultiLightRequested && _output is IMultiLightOutput multi)
        {
            _multiLights = await multi.ListLightsAsync(ct).ConfigureAwait(false);
            _multiLightActive = _multiLights.Count > 0;

            if (_multiLightActive)
            {
                _pushIntervalMs = 1000 / MultiPushHz;
                _multiLastPushed = new Dictionary<string, (int, int, int)>(_multiLights.Count);
            }
            else
            {
                _log.Warning(LogSource.Ambient,
                    "Multi-light requested but driver returned no lights — falling back to group push");
                _pushIntervalMs = 1000 / GroupPushHz;
            }
        }
        else
        {
            if (_useMultiLightRequested)
            {
                _log.Warning(LogSource.Ambient,
                    $"Multi-light requested but driver doesn't expose IMultiLightOutput ({_output.GetType().Name}) — falling back to group push");
            }
            _multiLightActive = false;
            _pushIntervalMs = 1000 / GroupPushHz;
        }

        _log.Info(LogSource.Ambient, "Ambient pipeline started");
        _log.Verbose(LogSource.Ambient,
            $"start | source={(_capture.IsRunning ? "running" : "stopped")} | output={_output.GetType().Name} | shape={(_multiLightActive ? "multi" : "group")} | lights={(_multiLights?.Count ?? 0)} | push_hz={(_multiLightActive ? MultiPushHz : GroupPushHz)} | sampler_grid={_sampler.GridCols}x{_sampler.GridRows} | hdr={(_sampler.IsHdr ? "on" : "off")}");

        if (!_capture.IsRunning) _capture.Start();

        _cts = new CancellationTokenSource();
        _startTimestamp = Stopwatch.GetTimestamp();
        _hbTimestamp    = _startTimestamp;
        _pushedCount = 0;
        _droppedCount = 0;
        _hbTicks = _hbPushed = _hbDropped = _hbUnmappedLights = 0;
        _lastR = _lastG = _lastB = -1;
        _pushLoopTask = Task.Run(() => PushLoopAsync(_cts.Token), _cts.Token);

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
            $"stop | reason=user | shape={(_multiLightActive ? "multi" : "group")} | duration_sec={durationSec:F1} | pushed={_pushedCount} | dropped={_droppedCount}");
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        // The loop runs on the thread-pool ; downstream SetColorAsync /
        // SetLightColorsAsync go through HttpClient which is thread-safe.
        // Any exception on a single tick's push is swallowed as a Warning
        // so a transient bridge failure (Wi-Fi blip, group renamed mid-
        // session) does not kill the loop — the next tick retries.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sample = _sampler.LatestSample;
                if (sample is null)
                {
                    // Sampler hasn't produced a frame yet (first ~66 ms
                    // after Start). Wait one cadence and retry.
                    await Task.Delay(_pushIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                if (_multiLightActive)
                {
                    await MultiLightTickAsync(sample, ct).ConfigureAwait(false);
                }
                else
                {
                    await GroupTickAsync(sample, ct).ConfigureAwait(false);
                }

                _hbTicks++;
                MaybeEmitHeartbeat();

                await Task.Delay(_pushIntervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop / DisposeAsync cancels the token.
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Ambient,
                $"Push loop crashed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task GroupTickAsync(SampledFrame sample, CancellationToken ct)
    {
        var avg = sample.Average;

        // Clamp near-black to true black so the lights turn off
        // instead of glowing faintly. See OffThreshold rationale.
        bool isDark = avg.R <= OffThreshold
                   && avg.G <= OffThreshold
                   && avg.B <= OffThreshold;
        byte targetR = isDark ? (byte)0 : avg.R;
        byte targetG = isDark ? (byte)0 : avg.G;
        byte targetB = isDark ? (byte)0 : avg.B;

        int delta = Math.Abs(targetR - _lastR)
                  + Math.Abs(targetG - _lastG)
                  + Math.Abs(targetB - _lastB);
        bool dropped = _lastR >= 0 && delta < ChangeThreshold;

        if (dropped)
        {
            _droppedCount++;
            _hbDropped++;
            return; // Silent : the heartbeat will summarise.
        }

        var color = new LightColor(targetR, targetG, targetB);
        try
        {
            await _output.SetColorAsync(color, ct).ConfigureAwait(false);
            _lastR = targetR; _lastG = targetG; _lastB = targetB;
            _pushedCount++;
            _hbPushed++;
            // Per-tick Verbose : gated by the user toggle. Cf.
            // LoggingSettings.LogAmbientCaptureActivity rationale —
            // these lines fire many times per second during active
            // play and the user wants to silence them locally without
            // losing the milestones or user actions.
            if (ShouldLogCaptureActivity())
            {
                _log.Verbose(LogSource.Ambient,
                    $"push | mode=group | rgb={targetR},{targetG},{targetB} | off={isDark}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Warning unconditional — capture-activity gating never
            // suppresses faults, the user needs to see when the bridge
            // throws even with the toggle off.
            _log.Warning(LogSource.Ambient,
                $"Push failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task MultiLightTickAsync(SampledFrame sample, CancellationToken ct)
    {
        if (_multiLights is null || _multiLights.Count == 0 || _multiLastPushed is null)
            return;

        // Sample the four border zones once per tick — cheap (each
        // averages ~50-100 cells of a 30×17 grid) and one set of
        // numbers shared across all lights that map to the same zone.
        // Zones with no assigned light are still computed for the
        // overlay UI but their result isn't pushed anywhere.
        var topColor    = SampleZone(sample, LightZone.Top);
        var bottomColor = SampleZone(sample, LightZone.Bottom);
        var leftColor   = SampleZone(sample, LightZone.Left);
        var rightColor  = SampleZone(sample, LightZone.Right);

        // Per-light fan-out + per-light early-exit. We build a
        // dictionary of (lightId → colour) only for lights whose target
        // colour has changed enough to warrant a push ; lights mapped
        // to <see cref="LightZone.None"/> (or unmapped entirely) are
        // skipped without counting as dropped — they're explicit
        // opt-outs, not throttled pushes.
        var toPush = new Dictionary<string, LightColor>(_multiLights.Count);
        int droppedThisTick = 0;
        int unmappedThisTick = 0;

        foreach (var light in _multiLights)
        {
            LightZone zone = (_zoneAssignments is not null && _zoneAssignments.TryGetValue(light.Id, out var z))
                ? z
                : LightZone.None;

            if (zone == LightZone.None)
            {
                unmappedThisTick++;
                continue;
            }

            LightColor zoneColor = zone switch
            {
                LightZone.Top    => topColor,
                LightZone.Bottom => bottomColor,
                LightZone.Left   => leftColor,
                LightZone.Right  => rightColor,
                _                => LightColor.Black,
            };

            // Apply the per-light brightness multiplier in [0, 1].
            // Scaling RGB linearly here halves Hue's derived `bri`
            // (max-channel based, see HueColorMath) so the lamp shows
            // the same chromaticity at the requested intensity. The
            // multiplier defaults to 1.0 when the user hasn't touched
            // the slider yet.
            double bri = 1.0;
            if (_lightBrightness is not null && _lightBrightness.TryGetValue(light.Id, out var b))
                bri = Math.Clamp(b, 0.0, 1.0);
            byte scaledR = (byte)Math.Round(zoneColor.R * bri);
            byte scaledG = (byte)Math.Round(zoneColor.G * bri);
            byte scaledB = (byte)Math.Round(zoneColor.B * bri);

            // Off-threshold applied per light independently after the
            // brightness scale — a zone of the screen can be near-black
            // while the rest is bright, AND the user can pin a single
            // lamp to "off" by sliding its brightness to 0 (which
            // collapses scaledR/G/B below the threshold).
            bool isDark = scaledR <= OffThreshold
                       && scaledG <= OffThreshold
                       && scaledB <= OffThreshold;
            byte targetR = isDark ? (byte)0 : scaledR;
            byte targetG = isDark ? (byte)0 : scaledG;
            byte targetB = isDark ? (byte)0 : scaledB;

            var prev = _multiLastPushed.TryGetValue(light.Id, out var last) ? last : (-1, -1, -1);
            int delta = Math.Abs(targetR - prev.Item1)
                      + Math.Abs(targetG - prev.Item2)
                      + Math.Abs(targetB - prev.Item3);
            bool dropped = prev.Item1 >= 0 && delta < ChangeThreshold;

            if (dropped)
            {
                droppedThisTick++;
                continue;
            }

            toPush[light.Id] = new LightColor(targetR, targetG, targetB);
            _multiLastPushed[light.Id] = (targetR, targetG, targetB);
        }

        // Track per-tick lights-with-no-zone count so the heartbeat
        // surfaces the user's "lights assigned to None" backlog
        // without us logging it every tick.
        _hbUnmappedLights += unmappedThisTick;

        if (toPush.Count == 0)
        {
            _droppedCount++;
            _hbDropped++;
            return; // Silent : the heartbeat will summarise.
        }

        try
        {
            var multi = (IMultiLightOutput)_output;
            await multi.SetLightColorsAsync(toPush, ct).ConfigureAwait(false);
            _pushedCount++;
            _hbPushed++;
            // Per-tick Verbose : gated. Same rationale as group mode.
            if (ShouldLogCaptureActivity())
            {
                _log.Verbose(LogSource.Ambient,
                    $"push | mode=multi | lights={toPush.Count}/{_multiLights.Count} | colors={FormatPushedColors(toPush)}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Ambient,
                $"Multi-light push failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Format the per-light colour set as "id=R,G,B id=R,G,B …" for
    // the push log. Short enough to fit on one line for 3-5 lamps ;
    // longer setups will wrap but stay readable.
    private static string FormatPushedColors(Dictionary<string, LightColor> pushed)
    {
        var sb = new System.Text.StringBuilder(pushed.Count * 18);
        bool first = true;
        foreach (var (id, c) in pushed)
        {
            if (!first) sb.Append(' ');
            sb.Append(id).Append('=').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
            first = false;
        }
        return sb.ToString();
    }

    private void MaybeEmitHeartbeat()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - _hbTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < HeartbeatIntervalMs) return;

        // Gated like the push lines : heartbeat is per-loop chatter,
        // belongs to the same family the user wants to silence while
        // playing. Counters are reset either way so the next heartbeat
        // window starts from zero — the metric stays correct when the
        // toggle is flipped back on mid-session.
        if (ShouldLogCaptureActivity())
        {
            _log.Verbose(LogSource.Ambient,
                $"heartbeat | mode={(_multiLightActive ? "multi" : "group")} | period_sec={elapsedMs / 1000.0:F1} | ticks={_hbTicks} | pushed={_hbPushed} | dropped={_hbDropped}{(_multiLightActive ? $" | unmapped_lights={_hbUnmappedLights}" : "")}");
        }

        _hbTimestamp = now;
        _hbTicks = _hbPushed = _hbDropped = _hbUnmappedLights = 0;
    }

    // Reads the user-controlled per-loop logging toggle. Called from
    // each per-tick Verbose emission inside the push loop, never from
    // the milestone Info lines (those are always visible). Reading the
    // settings store on every tick costs essentially nothing — it's an
    // in-memory snapshot — and lets the toggle take effect on the next
    // tick when the user flips it from the Diagnostics page mid-
    // session. Try/catch with a true fallback so a settings I/O glitch
    // can't accidentally silence the engine — the doctrine fallback
    // here is "keep emitting", consistent with the POCO default.
    private static bool ShouldLogCaptureActivity()
    {
        try { return LoggingSettingsService.Instance.Current.LogAmbientCaptureActivity; }
        catch { return true; }
    }

    // Averages all cells whose centre falls inside the matching border
    // rectangle, expressed in normalised [0,1]² coordinates on the
    // frame grid. Top = y ∈ [0, BorderDepth] × x ∈ [0, 1] ; Bottom =
    // y ∈ [1-BorderDepth, 1] × x ∈ [0, 1] ; Left = x ∈ [0, BorderDepth]
    // × y ∈ [0, 1] ; Right = x ∈ [1-BorderDepth, 1] × y ∈ [0, 1]. The
    // cell-index bounds are rounded inward via Floor / Ceiling so we
    // never read out-of-range. Returned colour is the arithmetic mean
    // of the cells in the rectangle — no gamma linearisation yet (J5
    // turf, behind a Playground toggle). None / unknown zones return
    // black so a misconfigured callsite leaves the lamps dark rather
    // than tinting them arbitrarily.
    public static LightColor SampleZone(SampledFrame sample, LightZone zone)
    {
        int cols = sample.Cols;
        int rows = sample.Rows;

        // Compute the cell-index bounding box for the zone. Inclusive
        // on both ends — at least one cell is always selected even on
        // a tiny grid where BorderDepth × dim < 1.
        int cMin, cMax, rMin, rMax;
        switch (zone)
        {
            case LightZone.Top:
                cMin = 0;
                cMax = cols - 1;
                rMin = 0;
                rMax = Math.Max(0, (int)Math.Floor(BorderDepth * rows) - 1);
                break;
            case LightZone.Bottom:
                cMin = 0;
                cMax = cols - 1;
                rMin = Math.Min(rows - 1, (int)Math.Ceiling((1.0 - BorderDepth) * rows));
                rMax = rows - 1;
                break;
            case LightZone.Left:
                cMin = 0;
                cMax = Math.Max(0, (int)Math.Floor(BorderDepth * cols) - 1);
                rMin = 0;
                rMax = rows - 1;
                break;
            case LightZone.Right:
                cMin = Math.Min(cols - 1, (int)Math.Ceiling((1.0 - BorderDepth) * cols));
                cMax = cols - 1;
                rMin = 0;
                rMax = rows - 1;
                break;
            default:
                return LightColor.Black;
        }

        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;
        for (int r = rMin; r <= rMax; r++)
        {
            int rowBase = r * cols;
            for (int c = cMin; c <= cMax; c++)
            {
                var px = sample.Grid[rowBase + c];
                sumR += px.R;
                sumG += px.G;
                sumB += px.B;
                count++;
            }
        }
        if (count == 0) return LightColor.Black;
        return new LightColor(
            (byte)(sumR / count),
            (byte)(sumG / count),
            (byte)(sumB / count));
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
        _multiLastPushed = null;
        _multiLights = null;
    }
}
