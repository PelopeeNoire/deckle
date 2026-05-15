using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Deckle.Vision;

// Interop helpers for Windows.Graphics.Capture on a desktop monitor.
// The capture pipeline lives entirely in WinRT types (GraphicsCaptureItem,
// Direct3D11CaptureFramePool, GraphicsCaptureSession) but two gateway
// operations still require native interop:
//
//   1. Resolving an IDirect3DDevice (the WinRT type the capture pool
//      expects) from a fresh D3D11 device. We can't construct an
//      IDirect3DDevice directly in C# — Microsoft only exposes
//      CreateDirect3D11DeviceFromDXGIDevice (an undocumented but stable
//      D3D11.dll export) which takes an IDXGIDevice ABI pointer and
//      hands back an IInspectable that CsWinRT can project.
//
//   2. Building a GraphicsCaptureItem for a HMONITOR. The WinRT class
//      has no public factory for monitors — only the IGraphicsCaptureItemInterop
//      activation-factory COM interface exposes CreateForMonitor, which
//      we have to QI manually via RoGetActivationFactory.
//
// Both gateways are documented at:
//   learn.microsoft.com/uwp/api/windows.graphics.capture.direct3d11captureframepool
//   learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture
//
// Errors from PreserveSig=false P/Invoke surface as Marshal-thrown
// COMException with the HRESULT preserved. ScreenCaptureService catches
// and logs.
internal static class ScreenCaptureInterop
{
    // ── HMONITOR ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    public static nint GetPrimaryMonitor()
    {
        // (0,0) + MONITOR_DEFAULTTOPRIMARY canonically returns the primary
        // monitor regardless of taskbar orientation or workspace layout.
        return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
    }

    // ── D3D11 device → IDirect3DDevice (WinRT) ───────────────────────────────

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

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        // 1. Create the D3D11 device. BGRA_SUPPORT is required for any
        //    consumer that wants to share the frames with the DirectX
        //    composition stack (Win2D, CanvasBitmap, etc.). Driver type
        //    hardware ; software fallback (WARP) not requested.
        D3D11CreateDevice(
            pAdapter:        0,
            driverType:      D3D_DRIVER_TYPE_HARDWARE,
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

    // ── GraphicsCaptureItem for a HMONITOR ───────────────────────────────────

    // IGraphicsCaptureItemInterop. Documented in winrt/windows.graphics.capture.interop.h
    // (Windows 10 SDK). Activation factory of GraphicsCaptureItem implements
    // this COM interface in addition to IActivationFactory ; we QI it from
    // RoGetActivationFactory.
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        // PreserveSig=false would normally throw on HRESULT failure ; we
        // keep explicit out IntPtr return so we can log the HRESULT in the
        // caller (vs eating it inside the marshaller).
        [PreserveSig]
        int CreateForWindow([In] nint window, [In] in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor([In] nint monitor, [In] in Guid iid, out nint result);
    }

    // GraphicsCaptureItem WinRT class GUID. Stable across versions ; documented
    // in winrt/Windows.Graphics.Capture.h.
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // RoGetActivationFactory takes a HSTRING for the class id. We can't use
    // [MarshalAs(UnmanagedType.HString)] on the string parameter directly —
    // built-in HSTRING marshalling was removed from the .NET runtime in
    // .NET 5 (see learn.microsoft.com/dotnet/core/compatibility/interop/5.0/built-in-support-for-winrt-removed).
    // Attempting to call this P/Invoke with that directive throws
    // MarshalDirectiveException ("Invalid managed/unmanaged type combination").
    // We allocate the HSTRING manually via WindowsCreateString and pass it
    // as an opaque IntPtr ; WindowsDeleteString releases it in the finally
    // clause regardless of the call outcome.
    [DllImport("combase.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IGraphicsCaptureItemInterop factory);

    [DllImport("combase.dll", PreserveSig = false, ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    public static GraphicsCaptureItem CreateGraphicsCaptureItem(nint hmon)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        // 1. Allocate a HSTRING for the class id ; the activation factory
        //    lookup keeps a reference for the duration of the call, so we
        //    can free our copy right after RoGetActivationFactory returns.
        WindowsCreateString(className, (uint)className.Length, out IntPtr hstringClassId);
        IGraphicsCaptureItemInterop factory;
        try
        {
            RoGetActivationFactory(hstringClassId, in IID_IGraphicsCaptureItemInterop, out factory);
        }
        finally
        {
            WindowsDeleteString(hstringClassId);
        }

        // 2. Ask the factory for an item bound to our HMONITOR. The IID
        //    we pass is the GraphicsCaptureItem WinRT class IID — the
        //    factory uses it to decide which interface to project.
        int hr = factory.CreateForMonitor(hmon, in IID_GraphicsCaptureItem, out nint itemPtr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // 3. Project the ABI pointer as the managed GraphicsCaptureItem.
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != 0) Marshal.Release(itemPtr);
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
    private static readonly Guid IID_ID3D11Device         = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_ID3D11Texture2D      = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGIAdapter         = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    private static readonly Guid IID_IDXGIFactory6        = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIOutput6         = new("068346e8-aaec-4b84-add7-137f513f77a1");

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

        // ID3D11Texture2D method (after IUnknown's 3 + ID3D11DeviceChild's 4
        // + ID3D11Resource's 2).
        public const int Texture2D_GetDesc                 = 10;
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
