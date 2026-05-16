using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Deckle.Composition;
using Deckle.Lighting;
using Deckle.Lighting.Hue;
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

    private readonly IAmbientEngineHost _host;

    // All four deps are owned by the engine — instantiated in
    // StartAsync, disposed in Stop. Null when the engine is idle. The
    // ScreenCaptureService is created fresh on every start so the
    // monitor selection + HDR negotiation are picked up from the
    // current Windows state rather than a stale snapshot.
    private ScreenCaptureService? _capture;
    private HueBridgeClient? _bridgeClient;
    private ILightOutput? _output;
    private FrameSampler? _sampler;

    // Resolved at StartAsync from _host.Ambient.UseMultiLight. The
    // multi-light pipeline shape is locked for the session : changing
    // UseMultiLight live mid-run would force a Stop + Start to reshape
    // the loop and the per-light state, so we snapshot at start and
    // ignore later host mutations until the next start.
    private bool _useMultiLightRequested;

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

    // Per-push HTTP duration buffer for the heartbeat. Reset every
    // HeartbeatIntervalMs. Captures the wall-clock cost of the
    // await on _output.SetColorAsync / IMultiLightOutput.SetLight-
    // ColorsAsync — i.e. the bridge round-trip + any back-pressure
    // from the HttpClient itself. Useful to diagnose the lag
    // accumulation observed in the Hue REST CLIP v1 pipeline (one
    // pushed value per tick — drops are not counted).
    private readonly List<double> _hbHttpDurationsMs = new(128);

    // HDR tuning snapshot, refreshed at the top of each tick from
    // _host.Ambient. Live-reload — the AmbientPage sliders apply on
    // the next tick without restarting the pipeline. See
    // AmbientSettings.ExposureEv / SaturationBoost / MinBrightness
    // for the user-facing semantics. The snapshot avoids re-reading
    // the host four times per pixel inside the helpers.
    //   - Exposure is forwarded to FrameSampler (applied in linear
    //     light before the tone-map, mathematically correct).
    //   - Saturation boost is applied here on the sRGB output (a
    //     simple HSV-S amplification to keep hue stable).
    //   - Min brightness is applied here on the sRGB output (raises
    //     the max channel to the floor while preserving chromaticity).
    private double _saturationBoost = 1.0;
    private int    _minBrightness   = 0;

    // Last-constructed engine, exposed for the AmbientPage that lives
    // in Deckle.Lighting.Ambient and cannot reference App (circular).
    // V0 assumes a single engine for the whole process ; multi-instance
    // scenarios (tests) get the most recent. Set by the ctor below.
    public static AmbientEngine? Current { get; private set; }

    // Bridged action invoked by the AmbientPage's "Open Playground"
    // button (rendered inside the NotPaired InfoBar). The App wires
    // this at boot to its lazy ShowPlaygroundLazy() — same approach as
    // TrayIconManager.OnShowPlayground. Lighting.Ambient cannot
    // reference App directly, so the App side fills the slot.
    public static Action? OpenPlaygroundRequested { get; set; }

    // ctor : the engine is glued to a host that exposes the live
    // AmbientSettings snapshot. The capture, the Hue bridge client,
    // the light output and the frame sampler are all owned — created
    // in StartAsync from the host's settings and disposed in Stop.
    // Construct is cheap (no I/O, no allocations beyond the empty
    // accumulator buffers above).
    public AmbientEngine(IAmbientEngineHost host)
    {
        _host = host;
        Current = this;
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

    // Preview accessors — forwarded from the owned FrameSampler. Null /
    // zero / 1.0 when the engine is idle ; consumers (Playground
    // preview grid, AmbientPage tuning panel) should treat the absence
    // of a sample as "engine not running" and render an empty state.
    public SampledFrame? LatestSample => _sampler?.LatestSample;
    public int   GridCols     => _sampler?.GridCols ?? 0;
    public int   GridRows     => _sampler?.GridRows ?? 0;
    public bool  IsHdr        => _sampler?.IsHdr ?? false;
    public float ContentPeak  => _sampler?.ContentPeak ?? 1f;

    // State machine fired on every transition. Consumers (App tray
    // tooltip + log, AmbientPage ProgressRing / InfoBar / ModeCombo
    // gating, Playground Pipeline UI) subscribe to surface the live
    // status and distinguish a transient "Starting" / "Stopping" from
    // a settled "Off" / "Running". Error is a transient blip — the
    // engine immediately collapses to Off after raising it, so the
    // subscriber gets two consecutive callbacks (Error then Off) and
    // is expected to flash a brief error indicator before returning
    // to the Off rendering.
    private AmbientEngineState _state = AmbientEngineState.Off;
    public  AmbientEngineState State => _state;
    public event Action<AmbientEngineState>? StateChanged;

    private void SetState(AmbientEngineState newState)
    {
        _state = newState;
        try { StateChanged?.Invoke(newState); }
        catch (Exception ex)
        {
            // A subscriber threw — don't let it kill the engine flow.
            _log.Warning(LogSource.Ambient,
                $"StateChanged subscriber threw — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // RFC1918 + APIPA validation for the Hue bridge address persisted
    // in AmbientSettings.HueBridgeIp. The bridge is a LAN-only device ;
    // accepting an arbitrary IP would let a corrupted (or tampered)
    // settings.json point the engine at an attacker-controlled server
    // on the internet (SSRF / data exfil through the SetColorAsync
    // payload). V0 accepts only IPv4 in the canonical private ranges
    // and 169.254/16 link-local. IPv6 + hostnames are out of scope for
    // V0 ; revisit when a user requests it with a justified setup.
    private static bool IsAcceptableBridgeIp(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!IPAddress.TryParse(s, out var ip)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        return
            b[0] == 10                                          // 10.0.0.0/8     class A private
         || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)           // 172.16.0.0/12  class B private
         || (b[0] == 192 && b[1] == 168)                        // 192.168.0.0/16 class C private
         || (b[0] == 169 && b[1] == 254);                       // 169.254.0.0/16 APIPA link-local
    }

    /// <summary>
    /// Builds the owned deps (capture, bridge client, output, sampler)
    /// from the host's AmbientSettings, connects the output, picks the
    /// pipeline shape (group vs multi-light), and launches the push
    /// loop. Idempotent — calling on a running engine is a no-op.
    /// Throws <see cref="InvalidOperationException"/> when the bridge
    /// isn't paired, when the persisted IP is not a LAN address, or
    /// when no group is selected ; throws other exceptions for
    /// unexpected I/O failures (network down, bridge unreachable).
    /// In every failure path the engine transitions Off → Starting →
    /// Error → Off so subscribers can react to the transient blip,
    /// and the caller (App observer) catches + reverts Enabled to
    /// false so the UI stays honest.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        SetState(AmbientEngineState.Starting);

        // Clean up any deps left over from a previous Stop (Stop only
        // cancels the loop ; the deps stay around so an in-flight
        // tick's await doesn't NRE). Idempotent if nothing to release.
        await DisposeOwnedDepsAsync().ConfigureAwait(false);

        // Wait for the previous push loop's tail to exit before we
        // spin up a new one. The cancel inside Stop already triggered
        // the OperationCanceledException at Task.Delay ; awaiting here
        // guarantees no two loops run in parallel against the same
        // _output / _sampler reference set.
        if (_pushLoopTask is not null)
        {
            try { await _pushLoopTask.ConfigureAwait(false); } catch { }
            _pushLoopTask = null;
        }
        _cts?.Dispose();
        _cts = null;

        var ambient = _host.Ambient;

        // Validate pair state. Without an IP, a username, and a group
        // id, the engine has nothing to talk to. Throw rather than
        // return silently so the App observer's catch fires and
        // reverts Enabled to false — keeps the tray checkmark and the
        // AmbientPage toggle in sync with the actual pipeline state.
        try
        {
            if (string.IsNullOrEmpty(ambient.HueBridgeIp)
             || string.IsNullOrEmpty(ambient.HueBridgeId)
             || string.IsNullOrEmpty(ambient.HueUsername)
             || string.IsNullOrEmpty(ambient.HueLastGroupId))
            {
                throw new InvalidOperationException(
                    "Hue bridge not paired or no group selected — open the Playground and complete the Hue pair + group selection first.");
            }

            if (!IsAcceptableBridgeIp(ambient.HueBridgeIp))
            {
                throw new InvalidOperationException(
                    $"Hue bridge IP '{ambient.HueBridgeIp}' is not on a private LAN range (RFC1918 or 169.254/16) — the bridge is a local device and any other address is rejected to avoid SSRF.");
            }
        }
        catch
        {
            SetState(AmbientEngineState.Error);
            SetState(AmbientEngineState.Off);
            throw;
        }

        // Snapshot the multi-light flag for this run. Live changes
        // via the AmbientPage (or anywhere else) only take effect at
        // the next Start because the loop shape and per-light state
        // dict are baked in here.
        _useMultiLightRequested = ambient.UseMultiLight;

        try
        {
            // ── Build owned deps ──────────────────────────────────
            // Hue bridge serves HTTPS on port 443 (the discovery
            // response confirms this on every consumer firmware). The
            // ClientKey field is unused on the REST CLIP v1 path —
            // pass empty to satisfy the record's non-nullable string.
            var bridge = new HueBridge(
                Id: ambient.HueBridgeId,
                InternalIpAddress: ambient.HueBridgeIp,
                Port: 443);
            var creds = new HueCredentials(ambient.HueUsername, "");

            _bridgeClient = new HueBridgeClient(bridge, creds);
            _output = new HueRestLightOutput(_bridgeClient, ambient.HueLastGroupId);

            _capture = new ScreenCaptureService();
            _capture.Start(ambient.SelectedMonitorDeviceName);

            _sampler = new FrameSampler(
                _capture.Device!,
                _capture.ContentSize,
                _capture.ActiveFormat,
                _capture.PeakLuminance);

            // Subscribe sampler to the capture pump. FrameArrived fires
            // on the FreeThreaded pool's worker thread ; FrameSampler
            // .Process is thread-safe internally (lock + Volatile.Write
            // on _latestSample).
            _capture.FrameArrived += OnFrameArrived;

            await _output!.ConnectAsync(ct).ConfigureAwait(false);

            // Resolve pipeline shape after Connect (ListLightsAsync
            // needs IsConnected). Multi-light requires : caller said
            // yes, driver exposes the capability, and the driver
            // reports at least one addressable light.
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
                        $"Multi-light requested but driver doesn't expose IMultiLightOutput ({_output!.GetType().Name}) — falling back to group push");
                }
                _multiLightActive = false;
                _pushIntervalMs = 1000 / GroupPushHz;
            }

            _log.Info(LogSource.Ambient, "Ambient pipeline started");
            _log.Verbose(LogSource.Ambient,
                $"start | source={(_capture!.IsRunning ? "running" : "stopped")} | output={_output!.GetType().Name} | shape={(_multiLightActive ? "multi" : "group")} | lights={(_multiLights?.Count ?? 0)} | push_hz={(_multiLightActive ? MultiPushHz : GroupPushHz)} | sampler_grid={_sampler!.GridCols}x{_sampler.GridRows} | hdr={(_sampler.IsHdr ? "on" : "off")}");

            _cts = new CancellationTokenSource();
            _startTimestamp = Stopwatch.GetTimestamp();
            _hbTimestamp    = _startTimestamp;
            _pushedCount = 0;
            _droppedCount = 0;
            _hbTicks = _hbPushed = _hbDropped = _hbUnmappedLights = 0;
            _hbHttpDurationsMs.Clear();
            _lastR = _lastG = _lastB = -1;

            // Open the capture-active window AFTER the started
            // milestones (Info + Verbose mirror above) have flushed,
            // so they pass the central filter even with
            // LogAmbientCaptureActivity off. From here on, Verbose
            // AMBIENT / SCREEN / HUE inside the loop are candidates
            // for filtering — see TelemetryService.Log. The window
            // closes at the very top of Stop() so the matching stop
            // milestones also pass.
            TelemetryService.Instance.SetCaptureActive(true);

            _pushLoopTask = Task.Run(() => PushLoopAsync(_cts.Token), _cts.Token);

            IsRunning = true;
            SetState(AmbientEngineState.Running);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Ambient,
                $"Ambient pipeline failed to start — {ex.GetType().Name}: {ex.Message}");
            await DisposeOwnedDepsAsync().ConfigureAwait(false);
            SetState(AmbientEngineState.Error);
            SetState(AmbientEngineState.Off);
            throw;
        }
    }

    private void OnFrameArrived(global::Windows.Graphics.Capture.Direct3D11CaptureFrame frame)
    {
        _sampler?.Process(frame);
    }

    private async Task DisposeOwnedDepsAsync()
    {
        if (_capture is not null)
        {
            try { _capture.FrameArrived -= OnFrameArrived; } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        if (_sampler is not null)
        {
            try { await _sampler.DisposeAsync().ConfigureAwait(false); } catch { }
            _sampler = null;
        }
        if (_output is IAsyncDisposable adisp)
        {
            try { await adisp.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        else if (_output is IDisposable disp)
        {
            try { disp.Dispose(); } catch { }
        }
        _output = null;
        if (_bridgeClient is not null)
        {
            try { _bridgeClient.Dispose(); } catch { }
            _bridgeClient = null;
        }
        _multiLights = null;
        _multiLastPushed = null;
    }

    /// <summary>
    /// Cancels the push loop. Idempotent — calls on an idle engine
    /// return silently. Transitions Running → Stopping → Off, firing
    /// StateChanged on each step so subscribers can render a brief
    /// "stopping" indicator before the final Off rendering. The full
    /// dep teardown (sampler / output / bridge client / capture) is
    /// deferred to the next StartAsync (re-create) or to DisposeAsync
    /// to keep Stop non-blocking for the caller.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        SetState(AmbientEngineState.Stopping);

        // Close the capture-active window FIRST so the stopped
        // milestones (Info + Verbose mirror below) pass the central
        // filter even with LogAmbientCaptureActivity off. The push
        // loop may still emit a final tick before cancellation
        // propagates ; those late Verbose lines also pass since the
        // flag is already off.
        TelemetryService.Instance.SetCaptureActive(false);

        long endTimestamp = Stopwatch.GetTimestamp();
        double durationSec = (endTimestamp - _startTimestamp) / (double)Stopwatch.Frequency;

        try { _cts?.Cancel(); } catch { /* best effort */ }
        IsRunning = false;

        _log.Info(LogSource.Ambient, "Ambient pipeline stopped");
        _log.Verbose(LogSource.Ambient,
            $"stop | reason=user | shape={(_multiLightActive ? "multi" : "group")} | duration_sec={durationSec:F1} | pushed={_pushedCount} | dropped={_droppedCount}");

        // Disconnect the FrameArrived subscription synchronously so
        // no further frames queue against the still-mapped sampler.
        // The full dep teardown (sampler / output / bridge client /
        // capture) is deferred to the next StartAsync (re-create) or
        // to DisposeAsync — keeps Stop non-blocking for the caller
        // (tray click handler, AmbientSettings.Changed observer)
        // while avoiding a race with the in-flight push tick's await.
        if (_capture is not null)
        {
            try { _capture.FrameArrived -= OnFrameArrived; } catch { }
        }

        SetState(AmbientEngineState.Off);
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
                // Refresh the HDR tuning snapshot from the host. Cheap
                // (three property reads on the singleton settings) and
                // gives the AmbientPage sliders a one-tick reaction
                // window without a restart.
                var ambient = _host.Ambient;
                _sampler!.SetExposureEv(ambient.ExposureEv);
                _saturationBoost = ambient.SaturationBoost;
                _minBrightness   = ambient.MinBrightness;

                var sample = _sampler!.LatestSample;
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
        byte rawR = isDark ? (byte)0 : avg.R;
        byte rawG = isDark ? (byte)0 : avg.G;
        byte rawB = isDark ? (byte)0 : avg.B;

        // Apply HDR tuning (saturation boost + min brightness floor)
        // BEFORE the early-exit so a user moving the AmbientPage
        // slider on a static screen still gets the new look pushed —
        // comparing on the raw values would suppress the change.
        var tuned = ApplyTuning(rawR, rawG, rawB, isDark);
        byte targetR = tuned.R;
        byte targetG = tuned.G;
        byte targetB = tuned.B;

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
            long httpStart = Stopwatch.GetTimestamp();
            await _output!.SetColorAsync(color, ct).ConfigureAwait(false);
            double httpMs = (Stopwatch.GetTimestamp() - httpStart) * 1000.0 / Stopwatch.Frequency;
            _hbHttpDurationsMs.Add(httpMs);

            _lastR = targetR; _lastG = targetG; _lastB = targetB;
            _pushedCount++;
            _hbPushed++;
            // Verbose gating is centralised in TelemetryService :
            // since the capture-active flag is on at this point and
            // source=AMBIENT, this line is dropped automatically when
            // the user toggle is off. No call-site check needed.
            _log.Verbose(LogSource.Ambient,
                $"push | mode=group | rgb={targetR},{targetG},{targetB} | off={isDark} | http_ms={httpMs:F1}");
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

        // Snapshot the per-light state from the host once per tick so
        // we re-read the live dictionary at most once even if a slider
        // mutation lands between fan-out steps.
        var zoneAssignments = _host.Ambient.LightZones;
        var lightBrightness = _host.Ambient.LightBrightness;

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
            LightZone zone = (zoneAssignments is not null && zoneAssignments.TryGetValue(light.Id, out var z))
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
            if (lightBrightness is not null && lightBrightness.TryGetValue(light.Id, out var b))
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
            byte rawR = isDark ? (byte)0 : scaledR;
            byte rawG = isDark ? (byte)0 : scaledG;
            byte rawB = isDark ? (byte)0 : scaledB;

            // Apply HDR tuning (saturation boost + min brightness)
            // per light, same rationale as GroupTick : the early-exit
            // compares on tuned values so a slider move always pushes.
            var tuned = ApplyTuning(rawR, rawG, rawB, isDark);
            byte targetR = tuned.R;
            byte targetG = tuned.G;
            byte targetB = tuned.B;

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
            var multi = (IMultiLightOutput)_output!;
            long httpStart = Stopwatch.GetTimestamp();
            await multi.SetLightColorsAsync(toPush, ct).ConfigureAwait(false);
            double httpMs = (Stopwatch.GetTimestamp() - httpStart) * 1000.0 / Stopwatch.Frequency;
            _hbHttpDurationsMs.Add(httpMs);

            _pushedCount++;
            _hbPushed++;
            // Verbose gating is centralised in TelemetryService.
            _log.Verbose(LogSource.Ambient,
                $"push | mode=multi | lights={toPush.Count}/{_multiLights.Count} | colors={FormatPushedColors(toPush)} | http_ms={httpMs:F1}");
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

    // Apply the user-tuned HDR transforms to a candidate sRGB colour.
    // Order : saturation boost first (in HSV-S, hue stable), min
    // brightness floor last (raises chromaticity-preserving). Bypassed
    // when the off-threshold has fired — a dark scene must stay dark
    // even if the user set a high min-brightness floor (otherwise the
    // floor would re-light the lamp during a movie's black frame).
    private (byte R, byte G, byte B) ApplyTuning(byte r, byte g, byte b, bool isDark)
    {
        if (isDark) return (0, 0, 0);

        (byte sR, byte sG, byte sB) = ApplySaturationBoost(r, g, b, _saturationBoost);
        return ApplyMinBrightness(sR, sG, sB, _minBrightness);
    }

    // OKLCh chroma amplification : multiply C by `boost` at constant L,
    // hue preserved. boost=1 is a no-op (early-out skips the
    // conversion). boost=0 collapses to greyscale, boost around
    // 1.3–1.8 lifts averaged scenes back toward the saturation the eye
    // perceives in the raw frame.
    //
    // Why OKLCh, not HSV. HSV's V is not perceptually uniform — at
    // V=0.5, yellow (H=60°) has perceived luminance ≈ 0.93 and blue
    // (H=240°) ≈ 0.07. Multiplying S therefore drags yellows brighter
    // and blues darker, visible on the lamp as bleach-out on warm
    // scenes and dimming on cool scenes. OKLCh's L is perceptually
    // uniform, so scaling C preserves perceived lightness across the
    // hue wheel. Same reason the conic stroke in HudComposition uses
    // OKLCh — see ColorSpace.cs header.
    //
    // OklchToRgb gamut-clips on the gamma output, so a too-high boost
    // that pushes C beyond the sRGB cube gets a gentle flattening
    // rather than a hard stop.
    private static (byte R, byte G, byte B) ApplySaturationBoost(byte r, byte g, byte b, double boost)
    {
        if (Math.Abs(boost - 1.0) < 0.001) return (r, g, b);

        var (L, C, h) = ColorSpace.RgbToOklch(r, g, b);
        if (C <= 0f) return (r, g, b);

        float newC = (float)Math.Max(0.0, C * boost);
        var result = ColorSpace.OklchToRgb(L, newC, h);
        return (result.R, result.G, result.B);
    }

    // Min-brightness floor : raise the max channel to `minBri` while
    // preserving chromaticity (the R:G:B ratio). HueColorMath derives
    // bri from max(R,G,B), so a mid-tone scene like (60, 40, 80) sends
    // bri ≈ 80, dim enough that the lamp's diffuser swallows the
    // colour. A floor at 180 keeps the chromaticity readable on the
    // lamp ; 0 disables the floor, 255 forces full brightness for any
    // non-zero scene.
    private static (byte R, byte G, byte B) ApplyMinBrightness(byte r, byte g, byte b, int minBri)
    {
        if (minBri <= 0) return (r, g, b);

        int max = Math.Max(r, Math.Max(g, b));
        if (max == 0 || max >= minBri) return (r, g, b);

        double scale = minBri / (double)max;
        return (
            (byte)Math.Min(255, Math.Round(r * scale)),
            (byte)Math.Min(255, Math.Round(g * scale)),
            (byte)Math.Min(255, Math.Round(b * scale)));
    }

    private void MaybeEmitHeartbeat()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - _hbTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < HeartbeatIntervalMs) return;

        // HTTP stats over the elapsed window. Skipped from the line
        // when no push happened in the window (static screen) — the
        // ticks=N pushed=0 prefix already says "loop alive, nothing
        // to push", a "http_avg_ms=0.0" suffix would be misleading.
        string httpStats = "";
        if (_hbHttpDurationsMs.Count > 0)
        {
            double min = double.MaxValue, max = 0, sum = 0;
            foreach (var v in _hbHttpDurationsMs)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            double avg = sum / _hbHttpDurationsMs.Count;
            var sorted = _hbHttpDurationsMs.ToArray();
            Array.Sort(sorted);
            int p95Idx = Math.Max(0, Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1));
            double p95 = sorted[p95Idx];
            httpStats = $" | http_avg_ms={avg:F1} | http_p95_ms={p95:F1} | http_max_ms={max:F1}";
        }

        // Per-tick Verbose : centrally gated by the capture-active
        // window + user toggle in TelemetryService. Counters are
        // reset whether the line was emitted or not, so the next
        // heartbeat window starts from zero — the metric stays
        // correct when the toggle flips mid-session.
        _log.Verbose(LogSource.Ambient,
            $"heartbeat | mode={(_multiLightActive ? "multi" : "group")} | period_sec={elapsedMs / 1000.0:F1} | ticks={_hbTicks} | pushed={_hbPushed} | dropped={_hbDropped}{(_multiLightActive ? $" | unmapped_lights={_hbUnmappedLights}" : "")}{httpStats}");

        _hbTimestamp = now;
        _hbTicks = _hbPushed = _hbDropped = _hbUnmappedLights = 0;
        _hbHttpDurationsMs.Clear();
    }

    // Averages all cells whose centre falls inside the matching border
    // rectangle, expressed in normalised [0,1]² coordinates on the
    // frame grid. Top = y ∈ [0, BorderDepth] × x ∈ [0, 1] ; Bottom =
    // y ∈ [1-BorderDepth, 1] × x ∈ [0, 1] ; Left = x ∈ [0, BorderDepth]
    // × y ∈ [0, 1] ; Right = x ∈ [1-BorderDepth, 1] × y ∈ [0, 1]. The
    // cell-index bounds are rounded inward via Floor / Ceiling so we
    // never read out-of-range. Returned colour is the gamma-correct
    // mean of the cells in the rectangle — sRGB bytes are linearised
    // via ColorSpace.SrgbToLinear8Lut, averaged in linear light, then
    // re-encoded via LinearToSrgb (matches the gamma-correct averaging
    // applied upstream in FrameSampler). None / unknown zones return
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

        double sumRLin = 0, sumGLin = 0, sumBLin = 0;
        int count = 0;
        for (int r = rMin; r <= rMax; r++)
        {
            int rowBase = r * cols;
            for (int c = cMin; c <= cMax; c++)
            {
                var px = sample.Grid[rowBase + c];
                sumRLin += ColorSpace.SrgbToLinear8Lut[px.R];
                sumGLin += ColorSpace.SrgbToLinear8Lut[px.G];
                sumBLin += ColorSpace.SrgbToLinear8Lut[px.B];
                count++;
            }
        }
        if (count == 0) return LightColor.Black;

        float avgR = (float)(sumRLin / count);
        float avgG = (float)(sumGLin / count);
        float avgB = (float)(sumBLin / count);
        return new LightColor(
            (byte)Math.Clamp((int)MathF.Round(ColorSpace.LinearToSrgb(avgR) * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(ColorSpace.LinearToSrgb(avgG) * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(ColorSpace.LinearToSrgb(avgB) * 255f), 0, 255));
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

        // Sync await the owned-deps teardown that Stop kicked off
        // fire-and-forget — DisposeAsync callers expect the engine
        // to be fully torn down on return.
        await DisposeOwnedDepsAsync().ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
        _multiLastPushed = null;
        _multiLights = null;
    }
}
