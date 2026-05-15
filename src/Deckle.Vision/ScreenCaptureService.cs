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
// J1 scope. SDR only, B8G8R8A8UIntNormalized pixel format, primary
// monitor only, no resize handling (logged Verbose if ContentSize
// changes but no pool recreate). HDR FP16 lands in J7 per the
// research note docs/research--hdr-graphics-capture--2026-05-15.md ;
// resize handling and multi-monitor lands when consumer needs surface.
public sealed class ScreenCaptureService : IDisposable
{
    private static readonly LogService _log = LogService.Instance;

    // Match the recommendation from the screen-capture sample: 2 buffers
    // is enough to avoid frame drops at typical desktop refresh rates,
    // any higher only inflates VRAM without latency benefit.
    private const int FramePoolBufferCount = 2;

    // Default pixel format for SDR. Switches to R16G16B16A16Float when
    // J7 (HDR) lands ; the framepool API accepts the change live via
    // Recreate(), so the service can transition without exposing the
    // format to the consumer.
    private const DirectXPixelFormat DefaultPixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly object _lock = new();

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    private Windows.Graphics.SizeInt32 _lastSize;
    private nint _hmon;
    private bool _disposed;

    // Frame stats — written from FrameArrived (worker thread), read by
    // consumers polling for display. Volatile so a polling reader on the
    // UI thread sees fresh values without a lock.
    private long _frameCount;
    private long _startTimestamp;

    /// <summary>True when a capture session is currently running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Total frames received since the last Start().</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

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

                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device,
                    DefaultPixelFormat,
                    FramePoolBufferCount,
                    _lastSize);

                _pool.FrameArrived += OnFrameArrived;
                _item.Closed += OnItemClosed;

                _session = _pool.CreateCaptureSession(_item);
                _session.StartCapture();

                _frameCount = 0;
                _startTimestamp = Stopwatch.GetTimestamp();
                IsRunning = true;

                _log.Verbose(LogSource.Screen,
                    $"start | hmon=0x{_hmon:X} | size={_lastSize.Width}x{_lastSize.Height} | format={DefaultPixelFormat} | bufs={FramePoolBufferCount}");
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
