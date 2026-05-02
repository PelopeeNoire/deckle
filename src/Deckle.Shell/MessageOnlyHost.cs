using System.Runtime.InteropServices;
using Deckle.Interop;
using Deckle.Logging;

namespace Deckle.Shell;

// Invisible native Win32 host window (HWND_MESSAGE parent).
//
// Replaces the former AnchorWindow — a hidden Microsoft.UI.Xaml.Window that
// was moved off-screen and Show(false)'d just to get a stable HWND for tray
// callbacks and RegisterHotKey. That was a hack: WinUI 3 has no canonical
// "tray-only, no flash" pattern (documented platform gap, cf. PowerToys
// #11010, microsoft-ui-xaml #6723).
//
// Message-only window is the canonical Win32 answer: created with HWND_MESSAGE
// as its parent, it is invisible by construction — no z-order, no enumeration,
// no broadcast messages, just a message dispatcher. RegisterHotKey posts
// WM_HOTKEY to the thread that created it; Shell_NotifyIcon posts its
// callback message to it. TrayIconManager and HotkeyManager attach via
// SetWindowSubclass exactly as they did on the former AnchorWindow HWND —
// zero refactor on either side.
//
// Created on the UI thread in App.OnLaunched so WM_HOTKEY and tray callbacks
// arrive on the same thread as DispatcherQueue — direct invocation without
// marshaling, same as before.
//
// References:
//   learn.microsoft.com/windows/win32/winmsg/window-features#window-types
//   learn.microsoft.com/windows/win32/inputdev/about-keyboard-input#hot-key-support
public sealed class MessageOnlyHost : IDisposable
{
    private const string ClassName = "WhispMessageHost";

    private readonly IntPtr _hInstance;
    private readonly ushort _classAtom;
    private readonly IntPtr _hwnd;

    // The WndProc delegate must live in a field for the GC not to collect it
    // while native code holds its function pointer (same rule as SubclassProc
    // in HotkeyManager/TrayIconManager).
    private readonly NativeMethods.WndProc _wndProcDelegate;

    private bool _disposed;

    public IntPtr Hwnd => _hwnd;

    public MessageOnlyHost()
    {
        _hInstance = NativeMethods.GetModuleHandle(null);
        if (_hInstance == IntPtr.Zero)
            throw new InvalidOperationException(
                $"GetModuleHandle failed (Win32 err {Marshal.GetLastWin32Error()})");

        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance     = _hInstance,
            lpszClassName = ClassName,
        };

        _classAtom = NativeMethods.RegisterClassEx(ref wc);
        if (_classAtom == 0)
            throw new InvalidOperationException(
                $"RegisterClassEx('{ClassName}') failed (Win32 err {Marshal.GetLastWin32Error()})");

        _hwnd = NativeMethods.CreateWindowEx(
            dwExStyle:   0,
            lpClassName: ClassName,
            lpWindowName: null,
            dwStyle:     0,
            x: 0, y: 0, nWidth: 0, nHeight: 0,
            hWndParent:  NativeMethods.HWND_MESSAGE,
            hMenu:       IntPtr.Zero,
            hInstance:   _hInstance,
            lpParam:     IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.UnregisterClass(ClassName, _hInstance);
            throw new InvalidOperationException(
                $"CreateWindowEx(HWND_MESSAGE) failed (Win32 err {err})");
        }

        LogService.Instance.Verbose(LogSource.MsgHost, $"message-only window created hwnd={_hwnd}");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
            NativeMethods.DestroyWindow(_hwnd);

        if (_classAtom != 0)
            NativeMethods.UnregisterClass(ClassName, _hInstance);
    }
}
