using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Deckle.Vision;

// Interop helpers for the capture pipeline (DXGI Output Duplication
// since the WGC-to-DXGI migration — see ScreenCaptureService class
// header for the rationale).
//
// What lives here :
//   1. CreateDirect3DDevice — wraps a fresh ID3D11Device as the WinRT
//      IDirect3DDevice the FrameSampler holds. Accepts an optional
//      adapter pointer (mandatory for the DXGI Duplication path, where
//      the D3D11 device must be created on the adapter driving the
//      target output).
//   2. EnumerateMonitors / FindMonitorByDeviceName / GetPrimaryMonitor
//      — HMONITOR resolution helpers shared by capture and the
//      monitor-selector UI.
//   3. GetD3D11Device / GetD3D11Texture — IDirect3DDxgiInterfaceAccess
//      QI bridge to recover the native COM objects behind WinRT
//      wrappers (used at FrameSampler init).
//   4. DetectHdrState / FindDxgiOutputForMonitor — DXGI walk that
//      matches an HMONITOR to its IDXGIAdapter + IDXGIOutput5 +
//      reported HDR state.
//   5. DXGI Output Duplication primitives (DuplicateOutput1,
//      AcquireNextFrame, ReleaseFrame, GetDuplicationDesc) and the
//      backing structs / vtable indices.
//   6. D3D11 vtable indices + textur-related structs used by
//      FrameSampler's GPU downsample path.
//
// Errors from PreserveSig=false P/Invoke surface as Marshal-thrown
// COMException with the HRESULT preserved. ScreenCaptureService
// catches and logs.
internal static class ScreenCaptureInterop
{
    // ── HMONITOR ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // MONITORINFOEXW — 32-byte fixed header + 32-char device name buffer.
    // LayoutKind.Sequential + CharSet.Unicode + fixed-size buffer matches
    // the C struct exactly. CbSize must be filled with sizeof(MONITORINFOEXW)
    // before the call (108 bytes including the 64-byte device-name buffer).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct MONITORINFOEXW
    {
        public int   CbSize;
        public RECT  RcMonitor;
        public RECT  RcWork;
        public uint  DwFlags;
        public fixed char SzDevice[32]; // CCHDEVICENAME = 32
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITORINFOF_PRIMARY     = 0x00000001;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    public static nint GetPrimaryMonitor()
    {
        // (0,0) + MONITOR_DEFAULTTOPRIMARY canonically returns the primary
        // monitor regardless of taskbar orientation or workspace layout.
        return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
    }

    /// <summary>Describes one monitor returned by <see cref="EnumerateMonitors"/>.</summary>
    /// <param name="Handle">HMONITOR (opaque to callers — pass back to capture).</param>
    /// <param name="DeviceName">Win32 device name, e.g. "\\\\.\\DISPLAY1". Stable
    /// across reboots for a given physical port + GPU. Persisted in
    /// <c>AmbientSettings.SelectedMonitorDeviceName</c>.</param>
    /// <param name="IsPrimary">True when this is the user's primary monitor.</param>
    /// <param name="X">Virtual-desktop x-origin in pixels.</param>
    /// <param name="Y">Virtual-desktop y-origin in pixels.</param>
    /// <param name="Width">Pixel width of the monitor.</param>
    /// <param name="Height">Pixel height of the monitor.</param>
    public sealed record MonitorInfo(
        nint    Handle,
        string  DeviceName,
        bool    IsPrimary,
        int     X,
        int     Y,
        int     Width,
        int     Height);

    /// <summary>
    /// Enumerates every monitor attached to the current desktop, in the
    /// order Windows reports them (typically primary first, then by
    /// GPU output index). The list is a snapshot — call again after
    /// a display configuration change to refresh. Scaffolding for the
    /// J9 monitor selector ; the V0 capture service still uses the
    /// primary unconditionally.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>(4);

        bool Callback(nint hMon, nint hdc, ref RECT rect, nint data)
        {
            var info = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(hMon, ref info))
            {
                // Monitor enumerated but info call failed (rare ;
                // typically a driver hiccup). Skip ; the rest of the
                // enumeration continues.
                return true;
            }

            string deviceName;
            unsafe { deviceName = new string(info.SzDevice); }

            monitors.Add(new MonitorInfo(
                Handle:     hMon,
                DeviceName: deviceName,
                IsPrimary:  (info.DwFlags & MONITORINFOF_PRIMARY) != 0,
                X:          info.RcMonitor.Left,
                Y:          info.RcMonitor.Top,
                Width:      info.RcMonitor.Right - info.RcMonitor.Left,
                Height:     info.RcMonitor.Bottom - info.RcMonitor.Top));
            return true; // keep enumerating
        }

        EnumDisplayMonitors(0, 0, Callback, 0);
        return monitors;
    }

    /// <summary>
    /// Resolves a monitor by its Win32 device name (the same string
    /// <see cref="MonitorInfo.DeviceName"/> exposes). Returns 0 if no
    /// monitor with that name is currently attached — the caller is
    /// expected to fall back to <see cref="GetPrimaryMonitor"/>.
    /// </summary>
    public static nint FindMonitorByDeviceName(string deviceName)
    {
        foreach (var m in EnumerateMonitors())
        {
            if (string.Equals(m.DeviceName, deviceName, StringComparison.Ordinal))
                return m.Handle;
        }
        return 0;
    }

    // ── D3D11 device → IDirect3DDevice (WinRT) ───────────────────────────────

    // D3D_DRIVER_TYPE — UNKNOWN is mandatory when D3D11CreateDevice
    // receives an explicit adapter pointer (the adapter implies its
    // own driver type) ; HARDWARE is used when we let DXGI pick the
    // default adapter.
    private const int D3D_DRIVER_TYPE_UNKNOWN  = 0;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x00000020;
    private const uint D3D11_SDK_VERSION = 7;

    [DllImport("d3d11.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void D3D11CreateDevice(
        nint pAdapter,
        int driverType,
        nint software,
        uint flags,
        nint pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out int pFeatureLevel,
        out nint ppImmediateContext);

    [DllImport("d3d11.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    // IDXGIDevice IID, queried from the freshly-created ID3D11Device.
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    public static IDirect3DDevice CreateDirect3DDevice(nint pAdapter = 0)
    {
        // 1. Create the D3D11 device. BGRA_SUPPORT is required for any
        //    consumer that wants to share the frames with the DirectX
        //    composition stack (Win2D, CanvasBitmap, etc.). When the
        //    caller passes a specific adapter (pAdapter != 0), the
        //    driver type must be UNKNOWN per the D3D11CreateDevice
        //    contract — the adapter implies its type. With pAdapter=0
        //    (default) we ask for HARDWARE explicitly to skip WARP.
        int driverType = pAdapter == 0 ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_UNKNOWN;
        D3D11CreateDevice(
            pAdapter:        pAdapter,
            driverType:      driverType,
            software:        0,
            flags:           D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            pFeatureLevels:  0,
            featureLevels:   0,
            sdkVersion:      D3D11_SDK_VERSION,
            ppDevice:        out nint d3dDevicePtr,
            pFeatureLevel:   out _,
            ppImmediateContext: out nint d3dContextPtr);

        // We never use the immediate context here — capture goes through
        // the frame pool, not direct draw calls. Release immediately.
        if (d3dContextPtr != 0) Marshal.Release(d3dContextPtr);

        try
        {
            // 2. QI to IDXGIDevice. Required because CreateDirect3D11Device-
            //    FromDXGIDevice operates on the DXGI face of the device,
            //    not on ID3D11Device directly.
            int hr = Marshal.QueryInterface(d3dDevicePtr, in IID_IDXGIDevice, out nint dxgiDevicePtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // 3. Wrap as the WinRT IDirect3DDevice. The d3d11.dll export
                //    hands back an IInspectable pointer ; we project it via
                //    CsWinRT's MarshalInspectable to get the managed
                //    IDirect3DDevice the FramePool wants.
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out nint winrtDevicePtr);
                try
                {
                    return MarshalInspectable<IDirect3DDevice>.FromAbi(winrtDevicePtr);
                }
                finally
                {
                    if (winrtDevicePtr != 0) Marshal.Release(winrtDevicePtr);
                }
            }
            finally
            {
                if (dxgiDevicePtr != 0) Marshal.Release(dxgiDevicePtr);
            }
        }
        finally
        {
            if (d3dDevicePtr != 0) Marshal.Release(d3dDevicePtr);
        }
    }

    // ── D3D11 staging support (J3 step 2 — FrameSampler) ─────────────────────
    //
    // FrameSampler runs the GPU downsample path : take the captured texture,
    // GenerateMips on an intermediate that has mip levels enabled, then
    // CopySubresourceRegion the target mip into a CPU-readable staging
    // texture. The CPU only ever reads the small mip (~500 pixels), never
    // the 4K source — that's where the perf comes from.
    //
    // We don't declare full ComImport interfaces for ID3D11Device /
    // ID3D11DeviceContext (60+ methods each), only stub vtable indices for
    // the calls we need and call them via function pointers. This is the
    // modern .NET 7+ pattern : less code than ComImport stubs, no
    // dependency on a third-party D3D11 wrapper. The vtable indices are
    // stable across Windows versions (D3D11 interfaces never change once
    // shipped, by COM convention).
    //
    // Every helper that returns an unmanaged COM pointer requires the
    // caller to Release it. Helpers that return managed wrappers
    // (IDirect3DDevice etc.) follow the existing release convention.

    // IDirect3DDxgiInterfaceAccess — the bridge between WinRT IDirect3D*
    // wrappers and native DXGI/D3D11 interfaces. Every IDirect3DDevice
    // and IDirect3DSurface implements it ; GetInterface returns the
    // underlying ID3D11Device / ID3D11Texture2D for a requested IID.
    //
    // We never declare a [ComImport] managed interface here. The mix of
    // CsWinRT projection (which owns the WinRT IDirect3DDevice) and
    // classic COM RCW threw "InvalidCastException: element not found"
    // at every pipeline start — the runtime couldn't reconcile a managed
    // cast on an object that CsWinRT had already wrapped its own way.
    //
    // The fix takes the canonical CsWinRT path : MarshalInspectable<T>.
    // FromManaged returns the raw AddRef'd ABI pointer of the WinRT
    // object (the same pointer the CsWinRT runtime uses internally).
    // We QI that to IDirect3DDxgiInterfaceAccess, then call GetInterface
    // via the vtable directly (slot 3, after IUnknown's 3 methods).
    // Zero managed cast in the path = no InvalidCastException.

    // IDirect3DDxgiInterfaceAccess IID. Held as a static field rather
    // than reconstructed each call so the GUID parsing cost is paid once.
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // Native D3D11 + DXGI interface IIDs. Used as QI targets through
    // IDirect3DDxgiInterfaceAccess.GetInterface (D3D11) or
    // IDXGIAdapter / IDXGIOutput chains (DXGI).
    private static readonly Guid IID_ID3D11Device           = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_ID3D11Texture2D        = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGIAdapter           = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    private static readonly Guid IID_IDXGIFactory6          = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIOutput5           = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    private static readonly Guid IID_IDXGIOutput6           = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");

    // Internal helper. Given a freshly AddRef'd ABI pointer (from
    // MarshalInspectable.FromManaged) and a target native COM IID, returns
    // an AddRef'd native interface pointer by QI'ing to IDirect3DDxgi-
    // InterfaceAccess and calling GetInterface via vtable[3]. The input
    // ABI pointer is Released in finally — caller doesn't own it
    // afterwards. Caller does own the returned pointer.
    private static nint GetNativeInterfaceFromAbi(nint abiPtr, Guid targetIid)
    {
        try
        {
            int hr = Marshal.QueryInterface(abiPtr, in IID_IDirect3DDxgiInterfaceAccess, out nint accessPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                unsafe
                {
                    // vtable layout : IUnknown (3 slots) + GetInterface (slot 3).
                    var vtbl = *(nint**)accessPtr;
                    var getInterface = (delegate* unmanaged<nint, Guid*, nint*, int>)vtbl[3];
                    nint targetPtr;
                    int gotHr = getInterface(accessPtr, &targetIid, &targetPtr);
                    Marshal.ThrowExceptionForHR(gotHr);
                    return targetPtr;
                }
            }
            finally
            {
                Marshal.Release(accessPtr);
            }
        }
        finally
        {
            Marshal.Release(abiPtr);
        }
    }

    // Extracts the native ID3D11Device behind a WinRT IDirect3DDevice. The
    // returned pointer is AddRef'd ; caller must Marshal.Release it.
    public static nint GetD3D11Device(IDirect3DDevice device)
        => GetNativeInterfaceFromAbi(
            MarshalInspectable<IDirect3DDevice>.FromManaged(device),
            IID_ID3D11Device);

    // Extracts the native ID3D11Texture2D behind a Direct3D11CaptureFrame's
    // Surface. The returned pointer is AddRef'd ; caller must Marshal.Release
    // it (typically inside the FrameArrived handler, paired with the frame's
    // own Dispose).
    public static nint GetD3D11Texture(IDirect3DSurface surface)
        => GetNativeInterfaceFromAbi(
            MarshalInspectable<IDirect3DSurface>.FromManaged(surface),
            IID_ID3D11Texture2D);

    // ── HDR detection (IDXGIOutput6::GetDesc1) ───────────────────────────────
    //
    // Reads the primary monitor's colour space + peak luminance to decide
    // whether to allocate the frame pool in R16G16B16A16Float (HDR / scRGB
    // linear) or B8G8R8A8UNorm (SDR). DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
    // is HDR10 (PQ transfer) ; DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 is
    // scRGB linear — both indicate the OS is in HDR mode. peakLuminance is
    // the display's reported max nits (typ. 400-1000 for HDR monitors,
    // 0 or 80 for SDR).
    //
    // Returns (false, 80.0, sRGB) when no HDR signalling is detected, so the
    // SDR tone-map path can use 80 nits as the reference white.

    private const int DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709     = 0;  // sRGB
    private const int DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709     = 1;  // scRGB
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020  = 12; // HDR10

    private const uint DXGI_CREATE_FACTORY_DEBUG = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC1
    {
        // WCHAR DeviceName[32] — 64 bytes of fixed-width name. We treat
        // it as a flat byte array via two ulong pairs for layout safety ;
        // we never read the name here.
        public ulong DeviceName0;
        public ulong DeviceName1;
        public ulong DeviceName2;
        public ulong DeviceName3;
        public ulong DeviceName4;
        public ulong DeviceName5;
        public ulong DeviceName6;
        public ulong DeviceName7;

        public int  DesktopLeft;
        public int  DesktopTop;
        public int  DesktopRight;
        public int  DesktopBottom;
        public int  AttachedToDesktop;       // BOOL
        public int  Rotation;                // DXGI_MODE_ROTATION enum
        public nint Monitor;                 // HMONITOR
        public uint BitsPerColor;
        public int  ColorSpace;              // DXGI_COLOR_SPACE_TYPE enum

        public float RedPrimary0;
        public float RedPrimary1;
        public float GreenPrimary0;
        public float GreenPrimary1;
        public float BluePrimary0;
        public float BluePrimary1;
        public float WhitePoint0;
        public float WhitePoint1;

        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    // CreateDXGIFactory2 entry point. Exported by dxgi.dll, available on
    // every Win10+. The Flags parameter is 0 for production (debug factory
    // not requested).
    [DllImport("dxgi.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void CreateDXGIFactory2(uint flags, [In] in Guid iid, out nint factory);

    // Snapshot of the relevant HDR state for the primary monitor.
    public readonly record struct HdrState(bool IsHdr, float PeakLuminance, int ColorSpace);

    public static HdrState DetectHdrState(nint hmon)
    {
        // Default fallback when something goes wrong (no HDR monitor, no
        // adapter found, etc.) : SDR with 80 nits as reference white.
        const float SdrReferenceNits = 80f;
        var fallback = new HdrState(false, SdrReferenceNits, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);

        nint factoryPtr = 0;
        try
        {
            try
            {
                CreateDXGIFactory2(0, in IID_IDXGIFactory6, out factoryPtr);
            }
            catch
            {
                return fallback;
            }

            // Walk adapters / outputs to find the one matching hmon.
            // IDXGIFactory6::EnumAdapters lives at vtable slot 7 (after
            // IUnknown's 3 + IDXGIObject's 4 = 7). IDXGIAdapter::EnumOutputs
            // lives at vtable slot 7 as well.
            unsafe
            {
                var factoryVtbl = *(nint**)factoryPtr;
                var enumAdapters = (delegate* unmanaged<nint, uint, nint*, int>)factoryVtbl[7];

                for (uint adapterIdx = 0; adapterIdx < 32; adapterIdx++)
                {
                    nint adapterPtr;
                    int hr = enumAdapters(factoryPtr, adapterIdx, &adapterPtr);
                    if (hr != 0 || adapterPtr == 0) break;

                    try
                    {
                        var adapterVtbl = *(nint**)adapterPtr;
                        var enumOutputs = (delegate* unmanaged<nint, uint, nint*, int>)adapterVtbl[7];

                        for (uint outputIdx = 0; outputIdx < 16; outputIdx++)
                        {
                            nint outputPtr;
                            hr = enumOutputs(adapterPtr, outputIdx, &outputPtr);
                            if (hr != 0 || outputPtr == 0) break;

                            try
                            {
                                // QI to IDXGIOutput6 to get GetDesc1.
                                Guid iidOutput6 = IID_IDXGIOutput6;
                                hr = Marshal.QueryInterface(outputPtr, in iidOutput6, out nint output6Ptr);
                                if (hr != 0) continue;

                                try
                                {
                                    // IDXGIOutput6::GetDesc1 is at vtable
                                    // slot 27 (3 IUnknown + 4 IDXGIObject
                                    // + 12 IDXGIOutput + 4 IDXGIOutput1
                                    // + 1 IDXGIOutput2 + 1 IDXGIOutput3
                                    // + 1 IDXGIOutput4 + 1 IDXGIOutput5
                                    // = 27, GetDesc1 is the first method
                                    // declared in IDXGIOutput6).
                                    var output6Vtbl = *(nint**)output6Ptr;
                                    var getDesc1 = (delegate* unmanaged<nint, DXGI_OUTPUT_DESC1*, int>)output6Vtbl[27];
                                    DXGI_OUTPUT_DESC1 desc;
                                    hr = getDesc1(output6Ptr, &desc);
                                    if (hr != 0) continue;

                                    if (desc.Monitor == hmon)
                                    {
                                        bool isHdr =
                                            desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
                                            desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709;
                                        float peak = desc.MaxLuminance > 0 ? desc.MaxLuminance : SdrReferenceNits;
                                        return new HdrState(isHdr, peak, desc.ColorSpace);
                                    }
                                }
                                finally
                                {
                                    Marshal.Release(output6Ptr);
                                }
                            }
                            finally
                            {
                                Marshal.Release(outputPtr);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                }
            }

            return fallback;
        }
        finally
        {
            if (factoryPtr != 0) Marshal.Release(factoryPtr);
        }
    }

    // ── DXGI Output Duplication ──────────────────────────────────────────────
    //
    // Desktop Duplication is the capture API that predates
    // Windows.Graphics.Capture. It runs on every desktop session without
    // a system-drawn capture indicator (the yellow border WGC paints
    // around the captured monitor), and supports HDR via DXGI 1.5's
    // DuplicateOutput1 + DXGI_FORMAT_R16G16B16A16_FLOAT in the format
    // list. Documented in Microsoft Learn "Desktop Duplication API" and
    // standard practice in OBS / NVIDIA ShadowPlay / HyperHDR.
    //
    // The pump is poll-based : IDXGIOutputDuplication::AcquireNextFrame
    // blocks up to a caller-specified timeout for a new desktop frame,
    // returning an IDXGIResource the caller QI's to ID3D11Texture2D.
    // ReleaseFrame returns the buffer to the OS. A worker thread loops
    // these two calls.
    //
    // Architecture note. The D3D11 device passed to DuplicateOutput1
    // MUST be created on the same DXGI adapter as the output being
    // duplicated, otherwise E_INVALIDARG. On multi-GPU laptops (Intel
    // iGPU + NVIDIA dGPU) the default adapter is rarely the one driving
    // the target monitor — we walk adapters/outputs to find the match,
    // then create the device on that specific adapter.

    public const int DXGI_ERROR_ACCESS_LOST   = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT  = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_INVALID_CALL  = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_HUNG   = unchecked((int)0x887A0006);

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_MODE_DESC
    {
        public uint          Width;
        public uint          Height;
        public DXGI_RATIONAL RefreshRate;
        public uint          Format;            // DXGI_FORMAT
        public uint          ScanlineOrdering;  // DXGI_MODE_SCANLINE_ORDER
        public uint          Scaling;           // DXGI_MODE_SCALING
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_DESC
    {
        public DXGI_MODE_DESC ModeDesc;
        public uint           Rotation;                    // DXGI_MODE_ROTATION
        public int            DesktopImageInSystemMemory;  // BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public int X;
        public int Y;
        public int Visible; // BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long                          LastPresentTime;
        public long                          LastMouseUpdateTime;
        public uint                          AccumulatedFrames;
        public int                           RectsCoalesced;            // BOOL
        public int                           ProtectedContentMaskedOut; // BOOL
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint                          TotalMetadataBufferSize;
        public uint                          PointerShapeBufferSize;
    }

    /// <summary>The IDXGIAdapter + IDXGIOutput5 pair that drives a given
    /// HMONITOR, plus the HDR state of that output. Both pointers are
    /// AddRef'd ; caller releases via <see cref="Marshal.Release"/> when
    /// done (typically alongside the IDXGIOutputDuplication's own
    /// lifetime).</summary>
    public readonly record struct DxgiOutputMatch(
        nint     AdapterPtr,
        nint     Output5Ptr,
        HdrState Hdr);

    /// <summary>
    /// Walks every DXGI adapter / output combination and returns the
    /// pair whose IDXGIOutput6::GetDesc1 reports the requested HMONITOR.
    /// Throws <see cref="InvalidOperationException"/> if no match is
    /// found (display disconnected mid-startup, headless adapter, etc.).
    /// On success the caller owns one reference to each returned
    /// pointer and must Release both.
    /// </summary>
    public static DxgiOutputMatch FindDxgiOutputForMonitor(nint hmon)
    {
        nint factoryPtr = 0;
        try
        {
            CreateDXGIFactory2(0, in IID_IDXGIFactory6, out factoryPtr);

            unsafe
            {
                var factoryVtbl = *(nint**)factoryPtr;
                var enumAdapters = (delegate* unmanaged<nint, uint, nint*, int>)factoryVtbl[7];

                for (uint adapterIdx = 0; adapterIdx < 32; adapterIdx++)
                {
                    nint adapterPtr;
                    int hr = enumAdapters(factoryPtr, adapterIdx, &adapterPtr);
                    if (hr != 0 || adapterPtr == 0) break;

                    bool keepAdapter = false;
                    try
                    {
                        var adapterVtbl = *(nint**)adapterPtr;
                        var enumOutputs = (delegate* unmanaged<nint, uint, nint*, int>)adapterVtbl[7];

                        for (uint outputIdx = 0; outputIdx < 16; outputIdx++)
                        {
                            nint outputPtr;
                            hr = enumOutputs(adapterPtr, outputIdx, &outputPtr);
                            if (hr != 0 || outputPtr == 0) break;

                            bool keepOutput = false;
                            try
                            {
                                // QI to IDXGIOutput6 for GetDesc1 (HDR state +
                                // HMONITOR). Same vtable shape walk as
                                // DetectHdrState.
                                Guid iidOutput6 = IID_IDXGIOutput6;
                                hr = Marshal.QueryInterface(outputPtr, in iidOutput6, out nint output6Ptr);
                                if (hr != 0) continue;

                                try
                                {
                                    var output6Vtbl = *(nint**)output6Ptr;
                                    var getDesc1 = (delegate* unmanaged<nint, DXGI_OUTPUT_DESC1*, int>)output6Vtbl[27];
                                    DXGI_OUTPUT_DESC1 desc;
                                    hr = getDesc1(output6Ptr, &desc);
                                    if (hr != 0) continue;

                                    if (desc.Monitor != hmon) continue;

                                    bool isHdr =
                                        desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
                                        desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709;
                                    float peak = desc.MaxLuminance > 0 ? desc.MaxLuminance : 80f;
                                    var hdrState = new HdrState(isHdr, peak, desc.ColorSpace);

                                    // QI down to IDXGIOutput5 — the
                                    // interface that exposes
                                    // DuplicateOutput1 (HDR-capable
                                    // duplication).
                                    Guid iidOutput5 = IID_IDXGIOutput5;
                                    hr = Marshal.QueryInterface(outputPtr, in iidOutput5, out nint output5Ptr);
                                    Marshal.ThrowExceptionForHR(hr);

                                    keepAdapter = true;
                                    keepOutput = true;
                                    return new DxgiOutputMatch(adapterPtr, output5Ptr, hdrState);
                                }
                                finally
                                {
                                    Marshal.Release(output6Ptr);
                                }
                            }
                            finally
                            {
                                if (!keepOutput) Marshal.Release(outputPtr);
                            }
                        }
                    }
                    finally
                    {
                        if (!keepAdapter) Marshal.Release(adapterPtr);
                    }
                }
            }
        }
        finally
        {
            if (factoryPtr != 0) Marshal.Release(factoryPtr);
        }

        throw new InvalidOperationException(
            $"No DXGI output found for HMONITOR 0x{hmon:X}. The monitor may have been disconnected, or its adapter doesn't expose a duplication-capable output.");
    }

    /// <summary>
    /// Calls IDXGIOutput5::DuplicateOutput1 on the supplied output with
    /// the given list of acceptable surface formats (priority order —
    /// DXGI picks the first the OS can fulfil). The returned
    /// IDXGIOutputDuplication pointer is AddRef'd ; caller releases
    /// when done. supportedFormats must include at least one valid
    /// DXGI_FORMAT (typically R16G16B16A16_FLOAT for HDR fallback to
    /// B8G8R8A8_UNORM for SDR).
    /// </summary>
    public static nint DuplicateOutput1(nint output5Ptr, nint d3dDevicePtr, uint[] supportedFormats)
    {
        if (supportedFormats is null || supportedFormats.Length == 0)
            throw new ArgumentException("supportedFormats must contain at least one DXGI_FORMAT", nameof(supportedFormats));

        unsafe
        {
            var output5Vtbl = *(nint**)output5Ptr;
            var duplicate = (delegate* unmanaged<nint, nint, uint, uint, uint*, nint*, int>)output5Vtbl[26];
            nint duplicationPtr;
            fixed (uint* fmtPtr = supportedFormats)
            {
                int hr = duplicate(
                    output5Ptr,
                    d3dDevicePtr,
                    /* Flags */ 0,
                    (uint)supportedFormats.Length,
                    fmtPtr,
                    &duplicationPtr);
                Marshal.ThrowExceptionForHR(hr);
            }
            return duplicationPtr;
        }
    }

    /// <summary>
    /// Calls IDXGIOutputDuplication::GetDesc to retrieve the negotiated
    /// surface format and dimensions. Useful right after DuplicateOutput1
    /// to learn which format DXGI picked from the supplied priority list.
    /// </summary>
    public static DXGI_OUTDUPL_DESC GetDuplicationDesc(nint duplicationPtr)
    {
        unsafe
        {
            var vtbl = *(nint**)duplicationPtr;
            var getDesc = (delegate* unmanaged<nint, DXGI_OUTDUPL_DESC*, void>)vtbl[7];
            DXGI_OUTDUPL_DESC desc;
            getDesc(duplicationPtr, &desc);
            return desc;
        }
    }

    /// <summary>
    /// Calls IDXGIOutputDuplication::AcquireNextFrame. Blocks up to
    /// <paramref name="timeoutMs"/> for a desktop image update. On
    /// success returns S_OK and populates the out parameters ;
    /// pDesktopResource is AddRef'd and the caller must call
    /// <see cref="ReleaseFrame"/> after processing (which also Releases
    /// the resource implicitly via OS bookkeeping — but we Release the
    /// COM ref ourselves for symmetry with the QI to ID3D11Texture2D).
    /// Other notable HRESULTs to handle explicitly :
    /// <see cref="DXGI_ERROR_WAIT_TIMEOUT"/> (no new frame in the
    /// interval — common, not an error), <see cref="DXGI_ERROR_ACCESS_LOST"/>
    /// (desktop switch / mode change / fullscreen swap — caller must
    /// recreate the IDXGIOutputDuplication).
    /// </summary>
    public static int AcquireNextFrame(
        nint duplicationPtr,
        uint timeoutMs,
        out DXGI_OUTDUPL_FRAME_INFO frameInfo,
        out nint desktopResourcePtr)
    {
        unsafe
        {
            var vtbl = *(nint**)duplicationPtr;
            var acquire = (delegate* unmanaged<nint, uint, DXGI_OUTDUPL_FRAME_INFO*, nint*, int>)vtbl[8];
            DXGI_OUTDUPL_FRAME_INFO info;
            nint resourcePtr;
            int hr = acquire(duplicationPtr, timeoutMs, &info, &resourcePtr);
            frameInfo = info;
            desktopResourcePtr = resourcePtr;
            return hr;
        }
    }

    /// <summary>
    /// Calls IDXGIOutputDuplication::ReleaseFrame. Returns the previously
    /// acquired frame's GPU buffer to the OS. Must be called once per
    /// successful AcquireNextFrame ; calling it a second time returns
    /// DXGI_ERROR_INVALID_CALL.
    /// </summary>
    public static int ReleaseFrame(nint duplicationPtr)
    {
        unsafe
        {
            var vtbl = *(nint**)duplicationPtr;
            var release = (delegate* unmanaged<nint, int>)vtbl[14];
            return release(duplicationPtr);
        }
    }

    /// <summary>
    /// QI helper : given an IDXGIResource pointer (typically from
    /// AcquireNextFrame's out param), returns the underlying
    /// ID3D11Texture2D. The returned pointer is AddRef'd ; caller
    /// releases.
    /// </summary>
    public static nint QueryD3D11Texture(nint dxgiResourcePtr)
    {
        Guid iid = IID_ID3D11Texture2D;
        int hr = Marshal.QueryInterface(dxgiResourcePtr, in iid, out nint texturePtr);
        Marshal.ThrowExceptionForHR(hr);
        return texturePtr;
    }

    // ── D3D11 vtable indices (counted from IUnknown::QueryInterface = 0) ─────
    //
    // Pulled from d3d11.h. Stable per COM contract (interfaces never change
    // shape once published). Exposed as constants so call sites read clearly.

    internal static class D3D11Vtbl
    {
        // ID3D11Device methods (after IUnknown's 3).
        public const int Device_CreateTexture2D            = 5;
        public const int Device_CreateShaderResourceView   = 7;
        public const int Device_GetImmediateContext        = 40;

        // ID3D11DeviceContext methods (after IUnknown's 3 + ID3D11DeviceChild's 4).
        public const int Context_Map                       = 14;
        public const int Context_Unmap                     = 15;
        public const int Context_CopySubresourceRegion     = 46;
        public const int Context_CopyResource              = 47;
        // GenerateMips is at slot 54 — after the Map/Unmap/Copy*/Update*/
        // Clear* family. Trust the d3d11.h declaration order ; the slot
        // is stable per COM contract.
        public const int Context_GenerateMips              = 54;
    }

    // ── D3D11 structs + constants ────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;          // DXGI_FORMAT
        public uint SampleDescCount;
        public uint SampleDescQuality;
        public uint Usage;           // D3D11_USAGE
        public uint BindFlags;       // D3D11_BIND_FLAG
        public uint CPUAccessFlags;  // D3D11_CPU_ACCESS_FLAG
        public uint MiscFlags;       // D3D11_RESOURCE_MISC_FLAG
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public nint pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    // D3D11_USAGE
    public const uint D3D11_USAGE_DEFAULT         = 0;
    public const uint D3D11_USAGE_STAGING         = 3;

    // D3D11_BIND_FLAG
    public const uint D3D11_BIND_SHADER_RESOURCE  = 0x8;
    public const uint D3D11_BIND_RENDER_TARGET    = 0x20;

    // D3D11_CPU_ACCESS_FLAG
    public const uint D3D11_CPU_ACCESS_WRITE      = 0x10000;
    public const uint D3D11_CPU_ACCESS_READ       = 0x20000;

    // D3D11_RESOURCE_MISC_FLAG
    public const uint D3D11_RESOURCE_MISC_GENERATE_MIPS = 0x1;

    // D3D11_MAP
    public const uint D3D11_MAP_READ              = 1;

    // DXGI_FORMAT
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM       = 87;
    public const uint DXGI_FORMAT_R16G16B16A16_FLOAT   = 10;
}
