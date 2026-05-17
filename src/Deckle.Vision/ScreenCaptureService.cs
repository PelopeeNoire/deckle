using System.Diagnostics;
using System.Runtime.InteropServices;
using Deckle.Logging;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Deckle.Vision;

// Screen capture pump on top of IDXGIOutputDuplication.
//
// Lifecycle. Construct → Start() → FrameArrived fires repeatedly →
// Stop() (or Dispose()) cancels the capture loop and releases the
// duplication, output, adapter and D3D device. Idempotent — Stop on a
// non-started service is a no-op, Dispose calls Stop. One instance =
// one capture session ; restart is done by disposing and rebuilding.
//
// Why DXGI Output Duplication and not Windows.Graphics.Capture. WGC is
// the modern API but the OS draws a yellow notification border around
// the captured surface, and the only way to disable it is the MSIX
// capability `graphicsCaptureWithoutBorder` — which can't be declared
// from an unpackaged desktop app. DXGI Output Duplication is the
// pre-WGC API (Windows 8+) and is not subject to the border. It's
// what HyperHDR / OBS / NVIDIA ShadowPlay use. The full architecture
// rationale lives in docs/architecture--color-science-pipeline--0.1.md
// axis 2 (the chantier that migrated us off WGC).
//
// Threading. The capture loop runs on a dedicated Task spun in Start.
// FrameArrived is raised on that worker thread, never on the caller's
// UI thread. Consumers that need to touch UI marshal themselves via
// DispatcherQueue.TryEnqueue. Matches the prior WGC contract.
//
// HDR. DuplicateOutput1 negotiates a pixel format from the supplied
// priority list (FP16-preferred when the display is in HDR mode,
// BGRA8-preferred for SDR). The negotiated format is read back via
// GetDuplicationDesc and exposed as ActiveFormat for FrameSampler to
// pick its tone-map path. Peak luminance comes from
// IDXGIOutput6::GetDesc1 during the adapter walk.
//
// Cadence. AcquireNextFrame blocks the loop thread until a desktop
// update or timeout. We throttle to ~15 Hz by sleeping the remainder
// of the 66 ms window after each delivered frame — same cadence as
// the AmbientEngine push loop.
//
// Recovery. DXGI_ERROR_ACCESS_LOST fires on desktop switch, mode
// change, secure-desktop transition, or fullscreen exclusive swap.
// The IDXGIOutputDuplication is invalidated ; we re-call
// DuplicateOutput1 on the same IDXGIOutput5 and resume. If recreation
// fails repeatedly (display disconnected) the loop surfaces a Stopped
// event and exits.
public sealed class ScreenCaptureService : IDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // ~15 Hz target. Matches the AmbientEngine push cadence so we
    // don't acquire frames the engine never consumes.
    private const int ThrottleIntervalMs = 66;

    // AcquireNextFrame timeout. Short enough that cancellation responds
    // promptly on Stop, long enough that we don't spin the CPU between
    // frames on a static screen.
    private const uint AcquireTimeoutMs = 200;

    // Back-off when AcquireNextFrame returns an unexpected error
    // (anything that isn't S_OK / WAIT_TIMEOUT / ACCESS_LOST). Keeps
    // a transient driver hiccup from busy-looping.
    private const int ErrorBackoffMs = 500;

    // Maximum consecutive recreate attempts on ACCESS_LOST before
    // surfacing Stopped and exiting. Recreate normally succeeds first
    // try ; a sustained failure means the monitor is gone for good.
    private const int MaxRecreateAttempts = 5;

    // Format priorities passed to DuplicateOutput1. The first format
    // the OS can honour wins. HDR sessions prefer FP16 scRGB ; SDR
    // sessions prefer BGRA8 (FP16 still acceptable as a fallback).
    private static readonly uint[] HdrFormats = new[]
    {
        ScreenCaptureInterop.DXGI_FORMAT_R16G16B16A16_FLOAT,
        ScreenCaptureInterop.DXGI_FORMAT_B8G8R8A8_UNORM,
    };
    private static readonly uint[] SdrFormats = new[]
    {
        ScreenCaptureInterop.DXGI_FORMAT_B8G8R8A8_UNORM,
        ScreenCaptureInterop.DXGI_FORMAT_R16G16B16A16_FLOAT,
    };

    private readonly object _lock = new();

    // Managed WinRT wrapper around the native D3D11 device. Kept for
    // FrameSampler's existing constructor signature ; the sampler
    // extracts the native pointer via ScreenCaptureInterop.GetD3D11Device.
    private IDirect3DDevice? _device;

    // Native COM pointers — AddRef'd on Start, Released on Stop.
    // _duplicationPtr is also released and re-AddRef'd on ACCESS_LOST.
    private nint _d3dDevicePtr;
    private nint _adapterPtr;
    private nint _output5Ptr;
    private nint _duplicationPtr;

    private Windows.Graphics.SizeInt32 _lastSize;
    private nint _hmon;
    private bool _disposed;

    private DirectXPixelFormat _activeFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private uint _activeDxgiFormat = ScreenCaptureInterop.DXGI_FORMAT_B8G8R8A8_UNORM;
    private float _peakLuminance = 80f;
    private bool _isHdrSession;

    // Capture loop task + cancellation. _captureLoopTask is non-null
    // while IsRunning, gated by _lock for visibility from Stop.
    private CancellationTokenSource? _cts;
    private Task? _captureLoopTask;

    private long _frameCount;
    private long _startTimestamp;

    /// <summary>True when a capture session is currently running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Total frames delivered since the last Start().</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>The WinRT IDirect3DDevice the duplication is bound to.
    /// Null when the service isn't running. Borrowed by consumers
    /// (FrameSampler) for D3D11 texture allocation — do not Dispose
    /// from outside.</summary>
    public IDirect3DDevice? Device => _device;

    /// <summary>Negotiated pixel format for the current session.
    /// Either <see cref="DirectXPixelFormat.B8G8R8A8UIntNormalized"/>
    /// (SDR) or <see cref="DirectXPixelFormat.R16G16B16A16Float"/>
    /// (HDR). Decided by DuplicateOutput1 based on the priority list
    /// and the OS's current display mode.</summary>
    public DirectXPixelFormat ActiveFormat => _activeFormat;

    /// <summary>True when the primary display reports an HDR colour
    /// space (HDR10 or scRGB) at Start time.</summary>
    public bool IsHdrSession => _isHdrSession;

    /// <summary>Reported peak luminance of the display in nits. 80 nits
    /// (SDR reference white) when HDR is off or unknown. Used by the
    /// FrameSampler tone-map to normalise scRGB FP16 values.</summary>
    public float PeakLuminance => _peakLuminance;

    /// <summary>Size of the captured surface (the source monitor's
    /// resolution). Valid only when <see cref="IsRunning"/> is true.</summary>
    public Windows.Graphics.SizeInt32 ContentSize => _lastSize;

    /// <summary>
    /// Raised on the capture loop's worker thread for every desktop
    /// frame the duplication delivers. The supplied
    /// <see cref="CapturedFrame"/>'s TexturePtr is valid only for the
    /// duration of the handler — do not retain it past return.
    /// </summary>
    public event Action<CapturedFrame>? FrameArrived;

    /// <summary>Raised on the worker thread when the capture stops
    /// after a sustained failure to recreate the duplication (display
    /// disconnected, mode change loop, etc.). Service is already in
    /// stopped state by the time this fires.</summary>
    public event Action? Stopped;

    /// <summary>
    /// Probes whether the running OS supports DXGI Output Duplication.
    /// Returns true on every Windows 8+ desktop session — kept as a
    /// method to preserve the call shape from the previous WGC-based
    /// service (where the WinRT API needed a feature check).
    /// </summary>
    public static bool IsSupported() => true;

    public void Start(string? targetMonitorDeviceName = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;

            _log.Info(LogSource.Screen, "Screen capture starting");

            try
            {
                _hmon = ResolveTargetMonitor(targetMonitorDeviceName);
                if (_hmon == 0)
                {
                    throw new InvalidOperationException(
                        "Primary monitor not found — MonitorFromPoint returned NULL.");
                }

                // Find the DXGI adapter + output5 driving this monitor,
                // capture the HDR state in passing. Throws if no match
                // (display disconnected mid-startup).
                var match = ScreenCaptureInterop.FindDxgiOutputForMonitor(_hmon);
                _adapterPtr    = match.AdapterPtr;
                _output5Ptr    = match.Output5Ptr;
                _isHdrSession  = match.Hdr.IsHdr;
                _peakLuminance = match.Hdr.PeakLuminance;

                // Create the D3D11 device on that specific adapter —
                // mandatory for DuplicateOutput1 (E_INVALIDARG otherwise
                // on multi-GPU laptops where the default adapter
                // doesn't drive the target monitor).
                _device = ScreenCaptureInterop.CreateDirect3DDevice(_adapterPtr);
                _d3dDevicePtr = ScreenCaptureInterop.GetD3D11Device(_device);

                // DuplicateOutput1 with the format priority list that
                // matches the OS's current display mode. The negotiated
                // format is read back via GetDuplicationDesc.
                uint[] formatList = _isHdrSession ? HdrFormats : SdrFormats;
                _duplicationPtr = ScreenCaptureInterop.DuplicateOutput1(
                    _output5Ptr, _d3dDevicePtr, formatList);

                var desc = ScreenCaptureInterop.GetDuplicationDesc(_duplicationPtr);
                _lastSize = new Windows.Graphics.SizeInt32
                {
                    Width  = (int)desc.ModeDesc.Width,
                    Height = (int)desc.ModeDesc.Height,
                };
                _activeDxgiFormat = desc.ModeDesc.Format;
                _activeFormat = _activeDxgiFormat == ScreenCaptureInterop.DXGI_FORMAT_R16G16B16A16_FLOAT
                    ? DirectXPixelFormat.R16G16B16A16Float
                    : DirectXPixelFormat.B8G8R8A8UIntNormalized;

                _frameCount = 0;
                _startTimestamp = Stopwatch.GetTimestamp();

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _captureLoopTask = Task.Run(() => CaptureLoop(token), token);

                IsRunning = true;

                _log.Verbose(LogSource.Screen,
                    $"start | hmon=0x{_hmon:X} | size={_lastSize.Width}x{_lastSize.Height} | format={_activeFormat} | hdr={(_isHdrSession ? "on" : "off")} | peak_lum={_peakLuminance:F0} | timeout_ms={AcquireTimeoutMs} | throttle_ms={ThrottleIntervalMs}");
                _log.Success(LogSource.Screen, "Screen capture started");
            }
            catch (Exception ex)
            {
                _log.Error(LogSource.Screen,
                    $"Screen capture failed to start — {ex.GetType().Name}: {ex.Message}");
                _log.Verbose(LogSource.Screen,
                    $"start failed | hr=0x{ex.HResult:X8} | type={ex.GetType().Name} | message={ex.Message}");

                DisposeInternals();
                throw;
            }
        }
    }

    private nint ResolveTargetMonitor(string? targetMonitorDeviceName)
    {
        if (string.IsNullOrEmpty(targetMonitorDeviceName))
        {
            return ScreenCaptureInterop.GetPrimaryMonitor();
        }

        var resolved = ScreenCaptureInterop.FindMonitorByDeviceName(targetMonitorDeviceName);
        if (resolved != 0)
        {
            _log.Verbose(LogSource.Screen,
                $"target monitor resolved | device_name={targetMonitorDeviceName} | hmon=0x{(long)resolved:X}");
            return resolved;
        }

        _log.Warning(LogSource.Screen,
            $"Monitor not found — requested={targetMonitorDeviceName}, falling back to primary. Display may be disconnected or the device name has changed.");
        return ScreenCaptureInterop.GetPrimaryMonitor();
    }

    public void Stop()
    {
        Task? loopTask;
        CancellationTokenSource? cts;
        bool wasRunning;
        lock (_lock)
        {
            if (!IsRunning && _duplicationPtr == 0) return;

            wasRunning = IsRunning;
            loopTask = _captureLoopTask;
            cts = _cts;

            // Flip the state first so a concurrent FrameArrived
            // consumer sees IsRunning=false even before the loop
            // actually wraps up.
            IsRunning = false;
            _captureLoopTask = null;
            _cts = null;
        }

        // Cancel the loop outside the lock — Wait might re-enter
        // via Stopped event subscribers.
        try { cts?.Cancel(); } catch { /* best effort */ }
        try { loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Expected — cooperative cancellation.
        }
        catch (Exception ex)
        {
            _log.Verbose(LogSource.Screen,
                $"capture loop wait threw — {ex.GetType().Name}: {ex.Message} (continuing shutdown)");
        }
        try { cts?.Dispose(); } catch { /* best effort */ }

        lock (_lock)
        {
            long endTimestamp = Stopwatch.GetTimestamp();
            long durationMs = (endTimestamp - _startTimestamp) * 1000 / Stopwatch.Frequency;
            long frames = Interlocked.Read(ref _frameCount);
            double fpsAvg = durationMs > 0 ? frames * 1000.0 / durationMs : 0.0;
            double durationSec = durationMs / 1000.0;

            DisposeInternals();

            if (wasRunning)
            {
                _log.Info(LogSource.Screen,
                    $"Screen capture stopped ({frames} frames in {durationSec:F1} s)");
                _log.Verbose(LogSource.Screen,
                    $"stop | frames={frames} | duration_ms={durationMs} | fps_avg={fpsAvg:F1}");
            }
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        long lastDeliveredTicks = 0;
        long throttleTicks = Stopwatch.Frequency * ThrottleIntervalMs / 1000;
        int consecutiveRecreates = 0;

        while (!ct.IsCancellationRequested)
        {
            // AcquireNextFrame blocks up to AcquireTimeoutMs. The duplication
            // pointer might be 0 transiently if a prior ACCESS_LOST recovery
            // attempt failed — try to recreate before each iteration.
            if (_duplicationPtr == 0)
            {
                if (!TryRecreateDuplication(ref consecutiveRecreates))
                {
                    break;
                }
            }

            int hr = ScreenCaptureInterop.AcquireNextFrame(
                _duplicationPtr,
                AcquireTimeoutMs,
                out var frameInfo,
                out nint desktopResourcePtr);

            if (hr == ScreenCaptureInterop.DXGI_ERROR_WAIT_TIMEOUT)
            {
                // Static screen — no new frame in the window. Normal,
                // not an error.
                continue;
            }

            if (hr == ScreenCaptureInterop.DXGI_ERROR_ACCESS_LOST)
            {
                _log.Verbose(LogSource.Screen,
                    "AcquireNextFrame returned ACCESS_LOST — desktop switch / mode change, recreating duplication");
                if (_duplicationPtr != 0)
                {
                    Marshal.Release(_duplicationPtr);
                    _duplicationPtr = 0;
                }
                continue;
            }

            if (hr == ScreenCaptureInterop.DXGI_ERROR_DEVICE_REMOVED ||
                hr == ScreenCaptureInterop.DXGI_ERROR_DEVICE_HUNG)
            {
                _log.Error(LogSource.Screen,
                    $"D3D11 device lost (hr=0x{hr:X8}) — capture session unrecoverable, stopping");
                break;
            }

            if (hr != 0)
            {
                // Downgraded to Verbose : the backoff + retry loop
                // can fire this once every 500 ms for the lifetime of
                // a degraded output (sleep / display unplug / HDR
                // toggle landing on INVALID_CALL = 0x887A0001), which
                // floods the LogWindow at Info. TODO : if a specific
                // hr starts showing up often in field reports, add a
                // dedicated recovery branch above (like ACCESS_LOST)
                // rather than relying on the generic retry.
                _log.Verbose(LogSource.Screen,
                    $"AcquireNextFrame failed (hr=0x{hr:X8}) — backing off {ErrorBackoffMs} ms");
                if (desktopResourcePtr != 0) Marshal.Release(desktopResourcePtr);
                try { Task.Delay(ErrorBackoffMs, ct).Wait(ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            // S_OK — got a frame. Reset the recreate streak.
            consecutiveRecreates = 0;

            try
            {
                long now = Stopwatch.GetTimestamp();
                bool skipForThrottle = lastDeliveredTicks != 0
                                    && (now - lastDeliveredTicks) < throttleTicks;

                if (skipForThrottle)
                {
                    // Honour the cadence cap : release the GPU buffer
                    // without copying it into the consumer's grid.
                    continue;
                }

                // QI the desktop image to ID3D11Texture2D. AddRef'd ;
                // released in the inner finally. A QI failure here is
                // unusual (the resource is guaranteed to back a texture
                // by the duplication contract) but we wrap to avoid
                // killing the loop on a one-off driver hiccup.
                nint texturePtr = 0;
                try
                {
                    texturePtr = ScreenCaptureInterop.QueryD3D11Texture(desktopResourcePtr);
                }
                catch (Exception ex)
                {
                    _log.Warning(LogSource.Screen,
                        $"Texture QI failed — {ex.GetType().Name}: {ex.Message} (frame dropped, session continues)");
                    continue;
                }

                try
                {
                    Interlocked.Increment(ref _frameCount);
                    lastDeliveredTicks = now;

                    var capturedFrame = new CapturedFrame(
                        texturePtr:     texturePtr,
                        width:          _lastSize.Width,
                        height:         _lastSize.Height,
                        timestampTicks: now);

                    try
                    {
                        FrameArrived?.Invoke(capturedFrame);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(LogSource.Screen,
                            $"FrameArrived consumer threw — {ex.GetType().Name}: {ex.Message} (frame dropped, session continues)");
                    }
                }
                finally
                {
                    if (texturePtr != 0) Marshal.Release(texturePtr);
                }
            }
            finally
            {
                if (desktopResourcePtr != 0) Marshal.Release(desktopResourcePtr);
                int releaseHr = ScreenCaptureInterop.ReleaseFrame(_duplicationPtr);
                if (releaseHr != 0 && releaseHr != ScreenCaptureInterop.DXGI_ERROR_INVALID_CALL)
                {
                    _log.Verbose(LogSource.Screen,
                        $"ReleaseFrame hr=0x{releaseHr:X8} (ignored)");
                }
            }
        }

        // Loop exited — surface Stopped if we didn't get there via a
        // user-triggered Stop() call (which sets IsRunning=false before
        // cancelling the token).
        if (!ct.IsCancellationRequested)
        {
            IsRunning = false;
            Stopped?.Invoke();
        }
    }

    private bool TryRecreateDuplication(ref int consecutiveRecreates)
    {
        consecutiveRecreates++;
        if (consecutiveRecreates > MaxRecreateAttempts)
        {
            _log.Error(LogSource.Screen,
                $"Duplication recreate failed {MaxRecreateAttempts} times in a row — capture stopped, display may be disconnected");
            return false;
        }

        try
        {
            uint[] formatList = _isHdrSession ? HdrFormats : SdrFormats;
            _duplicationPtr = ScreenCaptureInterop.DuplicateOutput1(
                _output5Ptr, _d3dDevicePtr, formatList);

            var desc = ScreenCaptureInterop.GetDuplicationDesc(_duplicationPtr);
            var newSize = new Windows.Graphics.SizeInt32
            {
                Width  = (int)desc.ModeDesc.Width,
                Height = (int)desc.ModeDesc.Height,
            };
            if (newSize.Width != _lastSize.Width || newSize.Height != _lastSize.Height)
            {
                _log.Verbose(LogSource.Screen,
                    $"resize detected on recreate | old={_lastSize.Width}x{_lastSize.Height} | new={newSize.Width}x{newSize.Height}");
                _lastSize = newSize;
            }

            _log.Verbose(LogSource.Screen,
                $"duplication recreated | attempt={consecutiveRecreates} | size={_lastSize.Width}x{_lastSize.Height}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Screen,
                $"DuplicateOutput1 failed on recreate (attempt {consecutiveRecreates}/{MaxRecreateAttempts}) — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DisposeInternals()
    {
        if (_duplicationPtr != 0)
        {
            try { Marshal.Release(_duplicationPtr); } catch { /* best effort */ }
            _duplicationPtr = 0;
        }
        if (_output5Ptr != 0)
        {
            try { Marshal.Release(_output5Ptr); } catch { /* best effort */ }
            _output5Ptr = 0;
        }
        if (_adapterPtr != 0)
        {
            try { Marshal.Release(_adapterPtr); } catch { /* best effort */ }
            _adapterPtr = 0;
        }
        if (_d3dDevicePtr != 0)
        {
            try { Marshal.Release(_d3dDevicePtr); } catch { /* best effort */ }
            _d3dDevicePtr = 0;
        }
        if (_device is not null)
        {
            // IDirect3DDevice implements IDisposable through IClosable in
            // CsWinRT projection. Release here so the underlying D3D11
            // device is freed promptly.
            try { (_device as IDisposable)?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
        _hmon = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
