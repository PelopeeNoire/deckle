using System.Diagnostics;
using Deckle.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Deckle.Vision;

// Screen capture pump on top of Windows.Graphics.Capture.
//
// Lifecycle. Construct → Start() → FrameArrived fires repeatedly →
// Stop() (or Dispose()) releases the session, the frame pool, and the
// D3D device. Idempotent — Stop on a non-started service is a no-op,
// Dispose calls Stop. One instance = one capture session ; restart is
// done by disposing and rebuilding (cheap, all state is per-instance).
//
// Threading. The FramePool is created via CreateFreeThreaded, which
// means FrameArrived is raised on the pool's internal worker thread,
// not on the caller's UI thread. The service relays the event as-is —
// consumers that need to touch UI marshal themselves (e.g. via
// DispatcherQueue.TryEnqueue). This matches the recommendation in
// the Microsoft Learn "Screen capture" article: don't block the UI
// thread with per-frame work.
//
// J3 step 2. HDR is detected at Start ; the frame pool format follows
// (R16G16B16A16Float when the primary display reports HDR, BGRA8 otherwise).
// MinUpdateInterval is set to 66 ms so Windows only notifies us 15 times
// per second — aligned with the AmbientEngine push cadence to avoid
// wasted readbacks. Resize handling and multi-monitor remain deferred
// (logged Verbose only if ContentSize drifts during a session).
public sealed class ScreenCaptureService : IDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // Match the recommendation from the screen-capture sample: 2 buffers
    // is enough to avoid frame drops at typical desktop refresh rates,
    // any higher only inflates VRAM without latency benefit.
    private const int FramePoolBufferCount = 2;

    // Cadence cap on the capture pump. Windows throttles the FrameArrived
    // event to at most one fire per interval. 66 ms ≈ 15 Hz — matches
    // the AmbientEngine push cadence so we don't waste readbacks the
    // engine never consumes. Available on Windows 10 19041+ ; older
    // builds ignore the setter.
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(66);

    private readonly object _lock = new();

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    private Windows.Graphics.SizeInt32 _lastSize;
    private nint _hmon;
    private bool _disposed;

    // Pool format + HDR state, captured at Start so consumers (FrameSampler)
    // can ask after the fact what was negotiated. Both are stable for the
    // duration of a session — switching format mid-flight requires Stop /
    // Start.
    private DirectXPixelFormat _activeFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private float _peakLuminance = 80f;
    private bool _isHdrSession;

    // Frame stats — written from FrameArrived (worker thread), read by
    // consumers polling for display. Volatile so a polling reader on the
    // UI thread sees fresh values without a lock.
    private long _frameCount;
    private long _startTimestamp;

    /// <summary>True when a capture session is currently running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Total frames received since the last Start().</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>The WinRT IDirect3DDevice the frame pool is bound to.
    /// Null when the service isn't running. Borrowed by consumers
    /// (FrameSampler) for D3D11 texture allocation — do not Dispose
    /// from outside.</summary>
    public IDirect3DDevice? Device => _device;

    /// <summary>Pixel format the pool was created with for the current
    /// session. Either <see cref="DirectXPixelFormat.B8G8R8A8UIntNormalized"/>
    /// (SDR) or <see cref="DirectXPixelFormat.R16G16B16A16Float"/> (HDR).
    /// </summary>
    public DirectXPixelFormat ActiveFormat => _activeFormat;

    /// <summary>True when the primary display reports an HDR colour space
    /// and the frame pool is in FP16 mode.</summary>
    public bool IsHdrSession => _isHdrSession;

    /// <summary>Reported peak luminance of the display in nits. 80 nits
    /// (SDR reference white) when HDR is off or unknown. Used by the
    /// FrameSampler tone-map to normalise scRGB FP16 values.</summary>
    public float PeakLuminance => _peakLuminance;

    /// <summary>Size of the captured surface (the source monitor's
    /// resolution). Valid only when <see cref="IsRunning"/> is true.</summary>
    public Windows.Graphics.SizeInt32 ContentSize => _lastSize;

    /// <summary>
    /// Raised on the framepool's worker thread for every frame the system
    /// delivers. The handler MUST dispose the supplied frame ; failing to
    /// dispose retires no buffer and the pool stalls within a few frames.
    /// </summary>
    public event Action<Direct3D11CaptureFrame>? FrameArrived;

    /// <summary>Raised on the worker thread when the captured item closes
    /// (display disconnected, signed-out, etc.). Service is already in
    /// stopped state by the time this fires.</summary>
    public event Action? Stopped;

    /// <summary>
    /// Probes whether the running OS supports Windows.Graphics.Capture.
    /// Returns false on Windows 10 builds older than 1809 (very rare in
    /// 2026 but worth a graceful refusal rather than a confusing crash).
    /// </summary>
    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;

            if (!GraphicsCaptureSession.IsSupported())
            {
                _log.Error(LogSource.Screen,
                    "Screen capture unsupported on this OS — Windows.Graphics.Capture requires Windows 10 1809 or newer.");
                throw new NotSupportedException("Windows.Graphics.Capture is not supported on this OS.");
            }

            _log.Info(LogSource.Screen, "Screen capture starting");

            try
            {
                _device = ScreenCaptureInterop.CreateDirect3DDevice();

                _hmon = ScreenCaptureInterop.GetPrimaryMonitor();
                if (_hmon == 0)
                {
                    throw new InvalidOperationException(
                        "Primary monitor not found — MonitorFromPoint returned NULL.");
                }

                _item = ScreenCaptureInterop.CreateGraphicsCaptureItem(_hmon);
                _lastSize = _item.Size;

                // HDR detection — query the DXGI output that matches our
                // HMONITOR. When the primary display is in HDR mode the
                // OS reports DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
                // (HDR10) or _G10_NONE_P709 (scRGB linear), and we ask
                // the pool for FP16 frames so the bright highlights
                // survive intact. SDR path keeps BGRA8.
                var hdrState = ScreenCaptureInterop.DetectHdrState(_hmon);
                _isHdrSession  = hdrState.IsHdr;
                _peakLuminance = hdrState.PeakLuminance;
                _activeFormat = _isHdrSession
                    ? DirectXPixelFormat.R16G16B16A16Float
                    : DirectXPixelFormat.B8G8R8A8UIntNormalized;

                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device,
                    _activeFormat,
                    FramePoolBufferCount,
                    _lastSize);

                _pool.FrameArrived += OnFrameArrived;
                _item.Closed += OnItemClosed;

                _session = _pool.CreateCaptureSession(_item);

                // Throttle the pump to ~15 Hz (66 ms). Available on Win10
                // 19041+ ; older builds silently ignore the setter, so the
                // try/catch is paranoia — failing here doesn't break the
                // capture, the consumer just sees more frames than it
                // needs.
                try { _session.MinUpdateInterval = MinUpdateInterval; }
                catch (Exception ex)
                {
                    _log.Verbose(LogSource.Screen,
                        $"min_update_interval setter failed (older OS ?) — {ex.GetType().Name}: {ex.Message}");
                }

                _session.StartCapture();

                _frameCount = 0;
                _startTimestamp = Stopwatch.GetTimestamp();
                IsRunning = true;

                _log.Verbose(LogSource.Screen,
                    $"start | hmon=0x{_hmon:X} | size={_lastSize.Width}x{_lastSize.Height} | format={_activeFormat} | hdr={(_isHdrSession ? "on" : "off")} | peak_lum={_peakLuminance:F0} | bufs={FramePoolBufferCount} | min_update_ms={(int)MinUpdateInterval.TotalMilliseconds}");
                _log.Success(LogSource.Screen, "Screen capture started");
            }
            catch (Exception ex)
            {
                _log.Error(LogSource.Screen,
                    $"Screen capture failed to start — {ex.GetType().Name}: {ex.Message}");
                _log.Verbose(LogSource.Screen,
                    $"start failed | hr=0x{ex.HResult:X8} | type={ex.GetType().Name} | message={ex.Message}");

                // Roll back any partial state so a retry can succeed.
                DisposeInternals();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning && _pool is null) return;

            long endTimestamp = Stopwatch.GetTimestamp();
            long durationMs = (endTimestamp - _startTimestamp) * 1000 / Stopwatch.Frequency;
            long frames = Interlocked.Read(ref _frameCount);
            double fpsAvg = durationMs > 0 ? frames * 1000.0 / durationMs : 0.0;
            double durationSec = durationMs / 1000.0;

            DisposeInternals();
            IsRunning = false;

            _log.Info(LogSource.Screen,
                $"Screen capture stopped ({frames} frames in {durationSec:F1} s)");
            _log.Verbose(LogSource.Screen,
                $"stop | frames={frames} | duration_ms={durationMs} | fps_avg={fpsAvg:F1}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame;
        try
        {
            frame = sender.TryGetNextFrame();
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Screen,
                $"Frame retrieval failed — {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (frame is null) return;

        Interlocked.Increment(ref _frameCount);

        // Resize detection logged for diagnostic. Not handled in J1 — the
        // captured texture stays at _lastSize, the consumer just gets a
        // smaller content area inside it. Recreate of the pool comes
        // later when a downstream consumer (Ambient analysis) needs the
        // frames to track resolution changes precisely.
        var contentSize = frame.ContentSize;
        if (contentSize.Width != _lastSize.Width || contentSize.Height != _lastSize.Height)
        {
            _log.Verbose(LogSource.Screen,
                $"resize detected | old={_lastSize.Width}x{_lastSize.Height} | new={contentSize.Width}x{contentSize.Height}");
            _lastSize = contentSize;
        }

        try
        {
            FrameArrived?.Invoke(frame);
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Screen,
                $"FrameArrived consumer threw — {ex.GetType().Name}: {ex.Message} (frame dropped, session continues)");
        }
        finally
        {
            // Disposing the frame retires its buffer back to the pool.
            // Failing to dispose stalls the pool within 2 frames (the
            // buffer count). Always-dispose regardless of consumer
            // exceptions.
            frame.Dispose();
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _log.Warning(LogSource.Screen,
            "Capture item closed by system — display disconnected, signed out, or session ended.");
        Stop();
        Stopped?.Invoke();
    }

    private void DisposeInternals()
    {
        if (_session is not null)
        {
            try { _session.Dispose(); } catch { /* best effort */ }
            _session = null;
        }
        if (_pool is not null)
        {
            try
            {
                _pool.FrameArrived -= OnFrameArrived;
                _pool.Dispose();
            }
            catch { /* best effort */ }
            _pool = null;
        }
        if (_item is not null)
        {
            try { _item.Closed -= OnItemClosed; } catch { /* best effort */ }
            _item = null;
        }
        if (_device is not null)
        {
            // IDirect3DDevice implements IDisposable through IClosable in
            // CsWinRT projection. Release here so the underlying D3D11
            // device is freed promptly (we hold the only reference).
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
