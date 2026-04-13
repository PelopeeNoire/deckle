using System.Runtime.InteropServices;
using WhispUI.Interop;

namespace WhispUI.Shell;

// Enregistre les hotkeys globaux et intercepte WM_HOTKEY via SetWindowSubclass.
// SetWindowSubclass chaîne dans la boucle de messages existante de WinUI 3
// sans remplacer son WndProc — c'est la seule approche safe.
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action<int> _onHotkey;

    // Le délégué doit vivre dans un champ pour empêcher le GC de le collecter
    // pendant que le code natif détient le pointeur de fonction.
    private NativeMethods.SubclassProc? _subclassDelegate;

    private bool _disposed;

    // Identifiant arbitraire pour retrouver notre subclass au moment du Remove.
    private static readonly UIntPtr SubclassId = new(0x5748_4B45); // "WHKE"

    public HotkeyManager(IntPtr hwnd, Action<int> onHotkey)
    {
        _hwnd = hwnd;
        _onHotkey = onHotkey;
    }

    public void Register()
    {
        _subclassDelegate = SubclassCallback;

        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);

        // Alt+` — transcription + collage
        bool ok1 = NativeMethods.RegisterHotKey(
            _hwnd,
            NativeMethods.HOTKEY_ID_TRANSCRIBE,
            NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_OEM_3);
        if (!ok1)
        {
            int err = Marshal.GetLastWin32Error();
            // err 1409 = ERROR_HOTKEY_ALREADY_REGISTERED — une autre app tient la combo
            throw new InvalidOperationException(
                $"RegisterHotKey Alt+` échoué (Win32 err {err}) — WhispInteropTest est-il encore en cours ?");
        }

        // Alt+Ctrl+` — transcription + réécriture LLM
        bool ok2 = NativeMethods.RegisterHotKey(
            _hwnd,
            NativeMethods.HOTKEY_ID_REWRITE,
            NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_OEM_3);
        if (!ok2)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"RegisterHotKey Alt+Ctrl+` échoué (Win32 err {err})");
        }
    }

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_HOTKEY)
        {
            // Le callback s'exécute sur le thread UI de WinUI 3 (même pump que
            // DispatcherQueue) — appel direct sans BeginInvoke ni TryEnqueue.
            _onHotkey(wParam.ToInt32());
            return IntPtr.Zero; // message traité
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NativeMethods.UnregisterHotKey(_hwnd, NativeMethods.HOTKEY_ID_TRANSCRIBE);
        NativeMethods.UnregisterHotKey(_hwnd, NativeMethods.HOTKEY_ID_REWRITE);

        if (_subclassDelegate is not null)
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
    }
}
