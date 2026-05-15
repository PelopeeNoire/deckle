using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Deckle.Composition;
using Deckle.Logging;

namespace Deckle.Vision;

// FrameSampler — capture frame → CPU stride sampling for ambient-lighting
// pixel analysis (J3 step 2).
//
// First attempt used a GPU mipmap pyramid (intermediate texture with
// MISC_GENERATE_MIPS, GenerateMips, mip pick + CopySubresourceRegion to
// a small staging). It didn't produce real colours on the test rig —
// the staging always came back zeros, the entire grid rendered black.
// CopySubresourceRegion semantics on cross-format / mip-mismatched paths
// are fragile and debugging them blind was eating tonight.
//
// Per-frame flow now (runs on the FreeThreaded frame pool's worker
// thread, serialised via _lock) :
//   1. QI Direct3D11CaptureFrame.Surface to ID3D11Texture2D.
//   2. CopyResource into our staging — both single-mip, same dimensions,
//      so this is the simple case that always works.
//   3. Map the staging, iterate the target grid with row/col strides
//      computed from (sourceWidth / targetCols, sourceHeight / targetRows).
//   4. Compute arithmetic RGB mean, fill the per-cell Color[] grid.
//   5. If the source format is FP16 (HDR), tone-map scRGB → 8-bit sRGB
//      via ColorSpace.ScRgbToSrgb against the display's reported peak
//      luminance.
//   6. Unmap and atomically publish the new SampledFrame via
//      Volatile.Write on _latestSample.
//
// Perf reality check : a 1920×1080 BGRA8 staging map costs ~8 MB
// transferred per frame ; at ~3-15 Hz that's 24-120 MB/s, well under
// 1 % CPU. A 4K BGRA8 map is ~33 MB, also fine in practice. The GPU
// mipmap optimisation we deferred can come back as a J5 polish if
// profiling shows it matters.
//
// What we don't do (out of scope J3 step 2) :
//   - Black-border detection (J4).
//   - Zone weighting / multi-region extraction (J4).
//   - Linear-light averaging (gamma-correct mean — J4/J5).
//   - Spring-damper smoothing (J5).
//
// Ownership :
//   - The IDirect3DDevice is borrowed (passed in the constructor). We
//     extract the native ID3D11Device + immediate context (AddRef'd
//     pointers we Release in DisposeAsync) but never close the WinRT
//     wrapper.
//   - The staging texture is owned ; allocated at construction, released
//     on dispose.
//
// Threading :
//   - Process() may be called from any thread (typically the frame
//     pool's worker). Serialised by _lock — successive frames don't
//     interleave their Map / Unmap.
//   - LatestSample is read by the engine push loop and the Playground
//     preview timer ; both via volatile read.
public sealed class FrameSampler : IAsyncDisposable
{
    private static readonly LogService _log = LogService.Instance;

    private readonly int _sourceWidth;
    private readonly int _sourceHeight;

    // Sampling stride. _gridCols × _gridRows cells, each sampled at
    // (col * _strideX, row * _strideY) in the source frame.
    private readonly int _gridCols;
    private readonly int _gridRows;
    private readonly int _strideX;
    private readonly int _strideY;

    // DXGI format of the pool and our textures. Decides whether we read
    // BGRA8 bytes or FP16 floats, and whether to tone-map.
    private readonly uint _dxgiFormat;
    private readonly bool _isHdr;
    private readonly float _peakLuminance;

    // Native COM pointers. AddRef'd ; Released in DisposeAsync.
    private nint _d3dDevice;
    private nint _d3dContext;
    private nint _stagingTex;

    private SampledFrame? _latestSample;
    private readonly object _lock = new();
    private bool _disposed;

    // Most recent snapshot — volatile read so consumers see the latest
    // value published by Process.
    public SampledFrame? LatestSample => Volatile.Read(ref _latestSample);

    public int GridCols => _gridCols;
    public int GridRows => _gridRows;
    public bool IsHdr   => _isHdr;

    public FrameSampler(
        IDirect3DDevice device,
        SizeInt32 sourceSize,
        DirectXPixelFormat poolFormat,
        float peakLuminance)
    {
        _sourceWidth  = sourceSize.Width;
        _sourceHeight = sourceSize.Height;
        _peakLuminance = peakLuminance > 0 ? peakLuminance : 80f;

        _isHdr = poolFormat == DirectXPixelFormat.R16G16B16A16Float;
        _dxgiFormat = _isHdr
            ? ScreenCaptureInterop.DXGI_FORMAT_R16G16B16A16_FLOAT
            : ScreenCaptureInterop.DXGI_FORMAT_B8G8R8A8_UNORM;

        (_gridCols, _gridRows, _strideX, _strideY) =
            ComputeGrid(_sourceWidth, _sourceHeight, targetCols: 60, targetRows: 34);

        // Extract the native ID3D11Device (AddRef'd) and its immediate
        // context. Both are released in DisposeAsync.
        _d3dDevice = ScreenCaptureInterop.GetD3D11Device(device);

        unsafe
        {
            var deviceVtbl = *(nint**)_d3dDevice;
            var getImmediateContext = (delegate* unmanaged<nint, nint*, void>)
                deviceVtbl[ScreenCaptureInterop.D3D11Vtbl.Device_GetImmediateContext];
            nint ctxPtr;
            getImmediateContext(_d3dDevice, &ctxPtr);
            _d3dContext = ctxPtr;
        }

        _stagingTex = CreateStagingTexture();

        _log.Verbose(LogSource.Screen,
            $"sampler init | grid={_gridCols}x{_gridRows} | stride={_strideX}x{_strideY} | tone_map={(_isHdr ? "scrgb_to_srgb" : "none")} | peak_lum={_peakLuminance:F0}");
    }

    public void Process(Direct3D11CaptureFrame frame)
    {
        if (_disposed) return;

        nint capturedTex = 0;
        try
        {
            capturedTex = ScreenCaptureInterop.GetD3D11Texture(frame.Surface);

            lock (_lock)
            {
                if (_disposed) return;

                unsafe
                {
                    var ctxVtbl = *(nint**)_d3dContext;

                    // CopyResource(staging, captured). Both textures are
                    // single-mip and same dimensions, so the canonical
                    // CopyResource works without ambiguity. The cost is
                    // a full-res GPU→CPU readback (~8 MB at 1080p, ~33 MB
                    // at 4K) but at 5-15 Hz that's still well under 1 %
                    // CPU on a modern adapter. Switch back to a GPU mip
                    // pyramid path in J5 if profiling shows it matters.
                    var copyResource = (delegate* unmanaged<nint, nint, nint, void>)
                        ctxVtbl[ScreenCaptureInterop.D3D11Vtbl.Context_CopyResource];
                    copyResource(_d3dContext, _stagingTex, capturedTex);

                    // Map(staging, 0, READ, 0, &mapped).
                    var map = (delegate* unmanaged<nint, nint, uint, uint, uint, ScreenCaptureInterop.D3D11_MAPPED_SUBRESOURCE*, int>)
                        ctxVtbl[ScreenCaptureInterop.D3D11Vtbl.Context_Map];
                    ScreenCaptureInterop.D3D11_MAPPED_SUBRESOURCE mapped;
                    int hr = map(_d3dContext, _stagingTex, 0, ScreenCaptureInterop.D3D11_MAP_READ, 0, &mapped);
                    if (hr != 0)
                    {
                        _log.Warning(LogSource.Screen, $"sampler map fail | hr=0x{hr:X8}");
                        return;
                    }

                    try
                    {
                        var sample = ReadSampleFromMapped(in mapped);
                        Volatile.Write(ref _latestSample, sample);
                    }
                    finally
                    {
                        // Unmap(staging, 0).
                        var unmap = (delegate* unmanaged<nint, nint, uint, void>)
                            ctxVtbl[ScreenCaptureInterop.D3D11Vtbl.Context_Unmap];
                        unmap(_d3dContext, _stagingTex, 0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Screen,
                $"sampler process failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (capturedTex != 0) Marshal.Release(capturedTex);
        }
    }

    private SampledFrame ReadSampleFromMapped(in ScreenCaptureInterop.D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var grid = new Color[_gridCols * _gridRows];
        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;

        if (_isHdr)
        {
            ReadGridFP16(in mapped, grid, ref sumR, ref sumG, ref sumB, ref count);
        }
        else
        {
            ReadGridBGRA8(in mapped, grid, ref sumR, ref sumG, ref sumB, ref count);
        }

        Color avg = count == 0
            ? Color.FromArgb(0xFF, 0, 0, 0)
            : Color.FromArgb(0xFF,
                (byte)(sumR / count),
                (byte)(sumG / count),
                (byte)(sumB / count));

        return new SampledFrame(avg, grid, _gridCols, _gridRows);
    }

    private void ReadGridBGRA8(
        in ScreenCaptureInterop.D3D11_MAPPED_SUBRESOURCE mapped,
        Color[] grid,
        ref long sumR, ref long sumG, ref long sumB, ref int count)
    {
        unsafe
        {
            byte* basePtr = (byte*)mapped.pData;
            int rowPitch = (int)mapped.RowPitch;

            for (int row = 0; row < _gridRows; row++)
            {
                int srcY = row * _strideY;
                if (srcY >= _sourceHeight) srcY = _sourceHeight - 1;
                byte* rowPtr = basePtr + srcY * rowPitch;

                for (int col = 0; col < _gridCols; col++)
                {
                    int srcX = col * _strideX;
                    if (srcX >= _sourceWidth) srcX = _sourceWidth - 1;
                    byte* p = rowPtr + srcX * 4;

                    byte b = p[0];
                    byte g = p[1];
                    byte r = p[2];
                    grid[row * _gridCols + col] = Color.FromArgb(0xFF, r, g, b);
                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }
            }
        }
    }

    private void ReadGridFP16(
        in ScreenCaptureInterop.D3D11_MAPPED_SUBRESOURCE mapped,
        Color[] grid,
        ref long sumR, ref long sumG, ref long sumB, ref int count)
    {
        unsafe
        {
            byte* basePtr = (byte*)mapped.pData;
            int rowPitch = (int)mapped.RowPitch;

            for (int row = 0; row < _gridRows; row++)
            {
                int srcY = row * _strideY;
                if (srcY >= _sourceHeight) srcY = _sourceHeight - 1;
                ushort* rowPtr = (ushort*)(basePtr + srcY * rowPitch);

                for (int col = 0; col < _gridCols; col++)
                {
                    int srcX = col * _strideX;
                    if (srcX >= _sourceWidth) srcX = _sourceWidth - 1;
                    ushort* p = rowPtr + srcX * 4;

                    float r = (float)BitConverter.UInt16BitsToHalf(p[0]);
                    float g = (float)BitConverter.UInt16BitsToHalf(p[1]);
                    float b = (float)BitConverter.UInt16BitsToHalf(p[2]);

                    Color c = ColorSpace.ScRgbToSrgb(r, g, b, _peakLuminance);
                    grid[row * _gridCols + col] = c;
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    count++;
                }
            }
        }
    }

    // Pick a grid shape that's as close as possible to the target without
    // exceeding the source dimensions. The strides are integer divisions,
    // so for a source that doesn't divide cleanly the last column / row
    // hits the same source pixel as the previous one (bounded read in
    // ReadGrid*).
    private static (int cols, int rows, int strideX, int strideY) ComputeGrid(
        int width, int height, int targetCols, int targetRows)
    {
        int strideX = Math.Max(1, width  / targetCols);
        int strideY = Math.Max(1, height / targetRows);
        int cols = Math.Max(1, width  / strideX);
        int rows = Math.Max(1, height / strideY);
        return (cols, rows, strideX, strideY);
    }

    private nint CreateStagingTexture()
    {
        // Staging at the source resolution — same shape as the captured
        // frame, single mip, CPU-readable. The same-shape requirement is
        // what lets us use ID3D11DeviceContext::CopyResource without the
        // silent-no-op trap of mismatched MipLevels.
        var desc = new ScreenCaptureInterop.D3D11_TEXTURE2D_DESC
        {
            Width             = (uint)_sourceWidth,
            Height            = (uint)_sourceHeight,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = _dxgiFormat,
            SampleDescCount   = 1,
            SampleDescQuality = 0,
            Usage             = ScreenCaptureInterop.D3D11_USAGE_STAGING,
            BindFlags         = 0,
            CPUAccessFlags    = ScreenCaptureInterop.D3D11_CPU_ACCESS_READ,
            MiscFlags         = 0,
        };

        unsafe
        {
            var deviceVtbl = *(nint**)_d3dDevice;
            var createTexture2D = (delegate* unmanaged<nint, ScreenCaptureInterop.D3D11_TEXTURE2D_DESC*, void*, nint*, int>)
                deviceVtbl[ScreenCaptureInterop.D3D11Vtbl.Device_CreateTexture2D];
            nint texPtr;
            int hr = createTexture2D(_d3dDevice, &desc, null, &texPtr);
            Marshal.ThrowExceptionForHR(hr);
            return texPtr;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;

        lock (_lock)
        {
            _disposed = true;

            if (_stagingTex != 0) { Marshal.Release(_stagingTex); _stagingTex = 0; }
            if (_d3dContext != 0) { Marshal.Release(_d3dContext); _d3dContext = 0; }
            if (_d3dDevice != 0)  { Marshal.Release(_d3dDevice);  _d3dDevice = 0; }
        }

        return ValueTask.CompletedTask;
    }
}
