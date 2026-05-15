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

    [DllImport("combase.dll", PreserveSig = false, ExactSpelling = true)]
    private static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IGraphicsCaptureItemInterop factory);

    public static GraphicsCaptureItem CreateGraphicsCaptureItem(nint hmon)
    {
        // 1. Get the activation factory and QI it as IGraphicsCaptureItemInterop.
        RoGetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            in IID_IGraphicsCaptureItemInterop,
            out IGraphicsCaptureItemInterop factory);

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
}
