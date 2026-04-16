using System.Runtime.InteropServices;
using WhispUI.Interop;
using WhispUI.Logging;

namespace WhispUI.Shell;

// Registers the 3 global hotkeys and intercepts WM_HOTKEY via SetWindowSubclass.
// SetWindowSubclass chains into the existing message pump of the host window
// (message-only window in our case) without replacing its WndProc — the only
// safe approach.
//
// Layout portability: the three chords all use the physical key to the left
// of "1" (scancode 0x29). At registration time we resolve the current VK for
// that scancode via MapVirtualKeyExW(GetKeyboardLayout(0)). On layout switch
// we receive WM_INPUTLANGCHANGE, unregister, re-resolve, re-register.
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action<int> _onHotkey;

    // The delegate must live in a field to prevent the GC from collecting it
    // while the native code holds the function pointer.
    private NativeMethods.SubclassProc? _subclassDelegate;

    private bool _disposed;
    private bool _registered;

    // Arbitrary identifier to retrieve our subclass at Remove time.
    private static readonly UIntPtr SubclassId = new(0x5748_4B45); // "WHKE"

    // (id, modifiers) pairs for RegisterAll / UnregisterAll. Adding a 4th
    // hotkey is just adding a line here.
    private static readonly (int Id, uint Modifiers)[] Hotkeys =
    {
        (NativeMethods.HOTKEY_ID_TRANSCRIBE,
            NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT),
        (NativeMethods.HOTKEY_ID_PRIMARY_REWRITE,
            NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT),
        (NativeMethods.HOTKEY_ID_SECONDARY_REWRITE,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT),
    };

    public HotkeyManager(IntPtr hwnd, Action<int> onHotkey)
    {
        _hwnd = hwnd;
        _onHotkey = onHotkey;
    }

    public void Register()
    {
        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

        RegisterAll();
    }

    // Resolves the current VK for the physical "left of 1" key under the
    // active keyboard layout, unregisters any previous bindings, and
    // registers the 3 chords. Called at startup and on every WM_INPUTLANGCHANGE.
    private void RegisterAll()
    {
        // Always unregister first — no-op if nothing is registered yet, but
        // required when re-registering after a layout change.
        UnregisterAll();

        IntPtr hkl = NativeMethods.GetKeyboardLayout(0);
        uint vk = NativeMethods.MapVirtualKeyExW(
            NativeMethods.SC_LEFT_OF_ONE,
            NativeMethods.MAPVK_VSC_TO_VK_EX,
            hkl);

        if (vk == 0)
        {
            DebugLog.Write("HOTKEY", $"MapVirtualKeyExW returned 0 for scancode 0x29 (HKL {hkl.ToInt64():X}) — skipping register");
            return;
        }

        DebugLog.Write("HOTKEY", $"register scancode 0x29 → VK 0x{vk:X2} under HKL {hkl.ToInt64():X}");

        foreach (var (id, modifiers) in Hotkeys)
        {
            bool ok = NativeMethods.RegisterHotKey(_hwnd, id, modifiers, vk);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // err 1409 = ERROR_HOTKEY_ALREADY_REGISTERED — another app
                // owns the combo (or WhispInteropTest is still running).
                throw new InvalidOperationException(
                    $"RegisterHotKey id={id} modifiers=0x{modifiers:X} vk=0x{vk:X2} failed (Win32 err {err}) — is WhispInteropTest still running?");
            }
        }

        _registered = true;
    }

    private void UnregisterAll()
    {
        if (!_registered) return;
        foreach (var (id, _) in Hotkeys)
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _registered = false;
    }

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_HOTKEY)
        {
            // The callback runs on the UI thread of the host (same pump as
            // DispatcherQueue) — direct call without BeginInvoke / TryEnqueue.
            _onHotkey(wParam.ToInt32());
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_INPUTLANGCHANGE)
        {
            // Keyboard layout changed — re-resolve and re-register. Continue
            // chaining so other subclasses / DefWindowProc still see the message.
            DebugLog.Write("HOTKEY", "WM_INPUTLANGCHANGE — re-registering hotkeys");
            try { RegisterAll(); }
            catch (Exception ex) { DebugLog.Write("HOTKEY", $"re-register failed: {ex.Message}"); }
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_subclassDelegate is not null)
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
    }
}
