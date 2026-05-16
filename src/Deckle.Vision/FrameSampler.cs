using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Deckle.Composition;
using Deckle.Logging;

namespace Deckle.Vision;

// FrameSampler — GPU downsample + CPU readback for the ambient-lighting
// pixel analysis path (J3 step 2).
//
// Per-frame flow (runs on the FreeThreaded frame pool's worker thread,
// serialised via _lock) :
//   1. QI the Direct3D11CaptureFrame.Surface to ID3D11Texture2D.
//   2. CopyResource into our intermediate texture (allocated at source
//      resolution with mip levels enabled and MISC_GENERATE_MIPS).
//   3. GenerateMips on the intermediate SRV — the GPU walks the
//      pyramid in hardware, microseconds on modern adapters.
//   4. CopySubresourceRegion the target mip into a small staging
//      texture (CPU-readable, USAGE_STAGING + CPU_ACCESS_READ).
//      Readback bus traffic ≈ 1 KB at 30×17×4 bytes (BGRA8) or
//      ≈ 2 KB at 30×17×8 bytes (FP16). Sub-millisecond.
//   5. Map the staging, iterate the ~510 pixels, compute the arithmetic
//      RGB average + fill the per-cell Color[] grid.
//   6. If the source format is FP16 (HDR), tone-map scRGB → 8-bit sRGB
//      via ColorSpace.ScRgbToSrgb against the display's reported
//      peak luminance.
//   7. Unmap and atomically publish the new SampledFrame via
//      Volatile.Write on _latestSample.
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
//     pointers we Release in DisposeAsync) but never close the
//     WinRT wrapper.
//   - The intermediate texture, intermediate SRV, and staging texture
//     are owned ; allocated at construction, released on dispose.
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

    // Mip level we sample. Chosen at construction so the resulting grid
    // is as close as possible to the target shape (30 × 17 for 16:9
    // sources, adjusted for other aspect ratios).
    private readonly int _targetMip;
    private readonly int _gridCols;
    private readonly int _gridRows;

    // DXGI format of the pool and our textures. Decides whether we read
    // BGRA8 bytes or FP16 floats, and whether to tone-map.
    private readonly uint _dxgiFormat;
    private readonly bool _isHdr;
    private readonly float _peakLuminance;

    // Hard ceiling on the rolling content peak, in scRGB units (=
    // display peak nits / 80). Caps the rolling max so a transient
    // sun-glint pixel cannot crush the rest of the scene below it.
    // Floored at 1.0 even in non-HDR sessions where the field is
    // unused.
    private readonly float _displayPeakScRgb;

    // Rolling max of the recent frames' max-channel scRGB values,
    // consumed by ColorSpace.ScRgbToSrgb as the normalisation peak.
    // Updated at the end of each ReadGridFP16 ; consumed at the start
    // of the next frame's tone-map (one-frame lag is harmless at
    // 15 Hz). Asymmetric attack / release :
    //   - attack instant (rises with the first bright frame)
    //   - release exponential — ContentPeakReleaseDecay per frame
    private float _contentPeak = 1.0f;
    private const float ContentPeakReleaseDecay = 0.97f;

    // Live-reloaded by the engine before each tick when the user
    // moves the AmbientPage Exposure slider. Applied as a linear-
    // light EV bias inside ColorSpace.ScRgbToSrgb.
    private double _exposureEv = 0.0;

    // Native COM pointers. AddRef'd ; Released in DisposeAsync.
    private nint _d3dDevice;
    private nint _d3dContext;
    private nint _intermediateTex;
    private nint _intermediateSrv;
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

    /// <summary>Rolling content-peak in scRGB units that the tone-map
    /// is currently normalising against. Exposed for the Playground
    /// preview and the AmbientPage tuning panel (shows whether the
    /// auto-exposure is biting). Always ≥ 1.0 (SDR floor). On a non-
    /// HDR session the value is pinned at 1.0.</summary>
    public float ContentPeak => _contentPeak;

    /// <summary>Live-reload entry point for the AmbientPage Exposure
    /// slider. Applied on the next frame ; no restart required. EV is
    /// linear-light (one stop = ×2 of brightness).</summary>
    public void SetExposureEv(double exposureEv) => _exposureEv = exposureEv;

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

        // Display peak in scRGB units (80 nits = 1.0 by scRGB
        // convention). Floored at 1.0 so non-HDR sessions and
        // pathological 0-nit reports still produce a sensible
        // ceiling.
        _displayPeakScRgb = _isHdr ? MathF.Max(_peakLuminance / 80f, 1f) : 1f;

        (_targetMip, _gridCols, _gridRows) =
            ComputeTargetMip(_sourceWidth, _sourceHeight, targetCols: 30, targetRows: 17);

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

        _intermediateTex = CreateIntermediateTexture();
        _intermediateSrv = CreateIntermediateSrv();
        _stagingTex      = CreateStagingTexture();

        _log.Verbose(LogSource.Screen,
            $"sampler init | grid={_gridCols}x{_gridRows} | mip={_targetMip} | tone_map={(_isHdr ? "scrgb_to_srgb" : "none")} | peak_lum={_peakLuminance:F0}");
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

                    // Copy the captured frame's mip 0 into the intermediate's
                    // mip 0. We can NOT use ID3D11DeviceContext::CopyResource
                    // here : it requires src and dst to have the same mip
                    // level count, and our intermediate has a full mip chain
                    // while the captured frame is single-level. The silent
                    // failure mode of CopyResource on mismatched MipLevels
                    // leaves the intermediate uninitialised (zeros), which
                    // is what gave every grid cell a black sample at first
                    // run. CopySubresourceRegion copies a specific
                    // (subresource, region) pair and is the right tool.
                    var copySubresourceRegion = (delegate* unmanaged<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)
                        ctxVtbl[ScreenCaptureInterop.D3D11Vtbl.Context_CopySubresourceRegion];
                    copySubresourceRegion(_d3dContext, _intermediateTex, 0, 0, 0, 0, capturedTex, 0, null);

                    // GenerateMips on the intermediate SRV — fills mip 1+
                    // by averaging mip 0 in hardware.
                    var generateMips = (delegate* unmanaged<nint, nint, void>)
                        ctxVtbl[ScreenCaptureInterop.D3D11Vtbl.Context_GenerateMips];
                    generateMips(_d3dContext, _intermediateSrv);

                    // CopySubresourceRegion(staging, 0, 0,0,0, intermediate, mip, null).
                    copySubresourceRegion(_d3dContext, _stagingTex, 0, 0, 0, 0, _intermediateTex, (uint)_targetMip, null);

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
                byte* rowPtr = basePtr + row * rowPitch;
                for (int col = 0; col < _gridCols; col++)
                {
                    byte* p = rowPtr + col * 4;
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
        // Tone-map runs against the rolling content peak captured by
        // the *previous* frame ; this frame's max feeds the rolling
        // update at the end of the loop. One-frame lag at 15 Hz is
        // imperceptible and avoids a two-pass read.
        float framePeak = 1f; // SDR floor — never normalise below 1.0
        float toneMapPeak = _contentPeak;
        double exposureEv = _exposureEv;

        unsafe
        {
            byte* basePtr = (byte*)mapped.pData;
            int rowPitch = (int)mapped.RowPitch;

            for (int row = 0; row < _gridRows; row++)
            {
                ushort* rowPtr = (ushort*)(basePtr + row * rowPitch);
                for (int col = 0; col < _gridCols; col++)
                {
                    ushort* p = rowPtr + col * 4;
                    float r = (float)BitConverter.UInt16BitsToHalf(p[0]);
                    float g = (float)BitConverter.UInt16BitsToHalf(p[1]);
                    float b = (float)BitConverter.UInt16BitsToHalf(p[2]);

                    if (r > framePeak) framePeak = r;
                    if (g > framePeak) framePeak = g;
                    if (b > framePeak) framePeak = b;

                    Color c = ColorSpace.ScRgbToSrgb(r, g, b, toneMapPeak, exposureEv);
                    grid[row * _gridCols + col] = c;
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    count++;
                }
            }
        }

        // Rolling content peak update. Attack is instant (peak rises
        // with the first bright frame) ; release decays toward the
        // current frame's max so a quick scene change brightens fast
        // but doesn't crash when a dark frame slips in. Capped at the
        // display's hard ceiling — a freak sun-glint pixel reading
        // above peakWhite (rare but possible in scRGB) cannot crush
        // the rest of the scene below it.
        if (framePeak > _contentPeak)
        {
            _contentPeak = framePeak;
        }
        else
        {
            _contentPeak = _contentPeak * ContentPeakReleaseDecay
                         + framePeak * (1f - ContentPeakReleaseDecay);
        }
        if (_contentPeak > _displayPeakScRgb) _contentPeak = _displayPeakScRgb;
        if (_contentPeak < 1f) _contentPeak = 1f; // SDR floor
    }

    // Pick the mip level whose dimensions land closest to (targetCols,
    // targetRows) without going below. Halves the source size at each
    // step ; stops when the next halving would undershoot the target.
    // 3840×2160 with target 30×17 → mip 7 (30×17). 1920×1080 → mip 6
    // (30×17). 2560×1440 → mip 6 (40×22). Aspect ratio of the source
    // is preserved in the resulting grid.
    private static (int mip, int cols, int rows) ComputeTargetMip(
        int width, int height, int targetCols, int targetRows)
    {
        int mip = 0;
        int w = width, h = height;
        while (w / 2 >= targetCols && h / 2 >= targetRows)
        {
            w /= 2;
            h /= 2;
            mip++;
        }
        return (mip, w, h);
    }

    private nint CreateIntermediateTexture()
    {
        var desc = new ScreenCaptureInterop.D3D11_TEXTURE2D_DESC
        {
            Width             = (uint)_sourceWidth,
            Height            = (uint)_sourceHeight,
            MipLevels         = 0,           // 0 = full mip chain
            ArraySize         = 1,
            Format            = _dxgiFormat,
            SampleDescCount   = 1,
            SampleDescQuality = 0,
            Usage             = ScreenCaptureInterop.D3D11_USAGE_DEFAULT,
            BindFlags         = ScreenCaptureInterop.D3D11_BIND_SHADER_RESOURCE
                              | ScreenCaptureInterop.D3D11_BIND_RENDER_TARGET,
            CPUAccessFlags    = 0,
            MiscFlags         = ScreenCaptureInterop.D3D11_RESOURCE_MISC_GENERATE_MIPS,
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

    private nint CreateIntermediateSrv()
    {
        // null SRV desc → SRV inherits the texture's format and full
        // mip chain. Sufficient for GenerateMips.
        unsafe
        {
            var deviceVtbl = *(nint**)_d3dDevice;
            var createSrv = (delegate* unmanaged<nint, nint, void*, nint*, int>)
                deviceVtbl[ScreenCaptureInterop.D3D11Vtbl.Device_CreateShaderResourceView];
            nint srvPtr;
            int hr = createSrv(_d3dDevice, _intermediateTex, null, &srvPtr);
            Marshal.ThrowExceptionForHR(hr);
            return srvPtr;
        }
    }

    private nint CreateStagingTexture()
    {
        var desc = new ScreenCaptureInterop.D3D11_TEXTURE2D_DESC
        {
            Width             = (uint)_gridCols,
            Height            = (uint)_gridRows,
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

            if (_intermediateSrv != 0) { Marshal.Release(_intermediateSrv); _intermediateSrv = 0; }
            if (_intermediateTex != 0) { Marshal.Release(_intermediateTex); _intermediateTex = 0; }
            if (_stagingTex != 0)      { Marshal.Release(_stagingTex);      _stagingTex = 0; }
            if (_d3dContext != 0)      { Marshal.Release(_d3dContext);      _d3dContext = 0; }
            if (_d3dDevice != 0)       { Marshal.Release(_d3dDevice);       _d3dDevice = 0; }
        }

        return ValueTask.CompletedTask;
    }
}
