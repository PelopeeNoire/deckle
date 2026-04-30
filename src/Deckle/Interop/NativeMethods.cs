using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Deckle.Setup;

namespace Deckle.Interop;

internal static class NativeMethods
{
    // ── libwhisper resolver ───────────────────────────────────────────────────
    //
    // libwhisper.dll and its transitive ggml-*.dll dependencies live under
    // AppPaths.NativeDirectory (= <UserDataRoot>\native\), NOT alongside the
    // application binary. This static initializer wires a managed resolver
    // that loads libwhisper from that directory; Windows then resolves the
    // ggml-*.dll dependencies from the same directory automatically (DLL
    // load order: directory of the loaded DLL first).
    //
    // Runs on first access to any NativeMethods member, before any
    // [DllImport(PInvokeKey)] P/Invoke is executed — guaranteed by the
    // CLR's static-constructor contract.
    //
    // The PInvokeKey constant below MUST stay in sync with the literal
    // in every [DllImport("libwhisper")] attribute in this file. C# requires
    // a constant literal in the attribute, so the duplication is unavoidable
    // — keep PInvokeKey here as the documented match-target.
    //
    // Falls through to default resolution when NativeDirectory doesn't hold
    // the DLLs yet: the first-run wizard catches the missing-deps state via
    // NativeRuntime.IsInstalled() and prompts the user before the engine
    // boots, so a DllNotFoundException shouldn't happen in practice. The
    // fallback exists for the edge case where the wizard is bypassed (env
    // var override, manual DLL placement next to the exe in dev).
    private const string PInvokeKey = "libwhisper";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != PInvokeKey) return IntPtr.Zero;

        string candidate = Path.Combine(AppPaths.NativeDirectory, NativeRuntime.EntryDll);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            return handle;

        return IntPtr.Zero;
    }

    // ── Hotkey ────────────────────────────────────────────────────────────────

    public const int WM_HOTKEY = 0x0312;

    // Sent to the focused window when the active input language changes
    // (layout switch via Win+Space, language bar, etc.). We intercept it in
    // the hotkey host subclass to re-resolve the VK for the "left of 1" key
    // and re-register all hotkeys under the new layout.
    public const int WM_INPUTLANGCHANGE = 0x0051;

    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // évite les WM_HOTKEY répétés par l'auto-repeat clavier

    // Scancode of the physical key to the left of "1" on every ANSI/ISO 104
    // keyboard. Stable across layouts — only the VK it maps to changes
    // (backtick on QWERTY US, ² on AZERTY FR, etc.). Resolve the current VK
    // via MapVirtualKeyExW(SC, MAPVK_VSC_TO_VK_EX, GetKeyboardLayout(0)).
    public const uint SC_LEFT_OF_ONE = 0x29;

    // uMapType for MapVirtualKeyEx — scancode → virtual-key (distinguishing
    // left/right shift/ctrl/alt, which MAPVK_VSC_TO_VK does not).
    // Official value is 3, not 4 (4 is MAPVK_VK_TO_VSC_EX, the inverse mapping).
    public const uint MAPVK_VSC_TO_VK_EX = 3;

    public const int HOTKEY_ID_TRANSCRIBE        = 1; // Win+[left-of-1]
    public const int HOTKEY_ID_PRIMARY_REWRITE   = 2; // Shift+Win+[left-of-1]
    public const int HOTKEY_ID_SECONDARY_REWRITE = 3; // Ctrl+Win+[left-of-1]

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Translates a scancode/virtual-key via a specific keyboard layout (HKL).
    // Needed for layout portability: at registration time we resolve the VK
    // for SC_LEFT_OF_ONE under the *current* HKL, so the physical key is
    // always matched regardless of the active layout.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

    // Returns the HKL (keyboard layout handle) of the specified thread, or
    // of the active thread when idThread == 0.
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    // ── Positionnement fenêtre ────────────────────────────────────────────────

    public static readonly IntPtr HWND_TOP      = new(0);
    public static readonly IntPtr HWND_TOPMOST  = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE       = 0x0001;
    public const uint SWP_NOMOVE       = 0x0002;
    public const uint SWP_NOZORDER     = 0x0004;
    public const uint SWP_NOACTIVATE   = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW   = 0x0040;

    // SW_SHOWNOACTIVATE : affiche la fenêtre sans lui donner le focus
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_HIDE           = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ── Focus fenêtre ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ── Identification fenêtre / focus clavier (debug) ────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int    cbSize;
        public uint   flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT   rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint uFormat);

    // Renvoie le DPI logique de la fenêtre (96 = 100%, 120 = 125%, 144 = 150%…).
    // Per-monitor DPI aware : suit le moniteur sur lequel se trouve la fenêtre.
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── SetWindowSubclass (comctl32 v6) ───────────────────────────────────────
    // Requiert Common Controls v6 dans app.manifest.
    // Ne pas utiliser SetWindowLongPtr(GWLP_WNDPROC) : remplacerait entièrement
    // la chaîne de messages de WinUI 3 et casserait le compositor.

    public delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // ── Injection clavier (SendInput) ─────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ── Presse-papier ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    // ── waveIn (capture audio PCM) ────────────────────────────────────────────

    [DllImport("winmm.dll")]
    public static extern uint waveInOpen(
        out IntPtr phwi, uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    public static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    public static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    public static extern uint waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    public static extern uint waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    public static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    public static extern uint waveInClose(IntPtr hwi);

    // ── waveIn : énumération des périphériques d'entrée ─────────────────────

    [DllImport("winmm.dll")]
    public static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveInGetDevCapsW(uint uDeviceID, ref WAVEINCAPSW pwic, uint cbwic);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WAVEINCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    // ── kernel32 (event, mémoire) ─────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    public static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    // ── Icône tray (Shell32) ──────────────────────────────────────────────────

    public const uint NIM_ADD    = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON    = 0x00000002;
    public const uint NIF_TIP     = 0x00000004;

    public const uint WM_TRAY = 0x0400 + 1; // WM_USER + 1

    public const uint WM_RBUTTONUP      = 0x0205;
    public const uint WM_LBUTTONUP      = 0x0202;
    public const uint WM_LBUTTONDBLCLK  = 0x0203;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    // ── Icône (user32 / LoadImage) ────────────────────────────────────────────

    public const uint IMAGE_ICON      = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(
        IntPtr hInst, string lpszName, uint uType,
        int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // ── Menu contextuel (user32) ──────────────────────────────────────────────

    public const uint MF_STRING    = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_GRAYED   = 0x00000001;

    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RETURNCMD  = 0x0100;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_RIGHTALIGN  = 0x0008;

    public const uint WM_COMMAND = 0x0111;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(
        IntPtr hMenu, uint uFlags,
        int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ── Raw Input (WM_INPUT) ──────────────────────────────────────────────────
    // Approche event-driven : on s'abonne aux mouvements souris globaux via
    // RegisterRawInputDevices avec RIDEV_INPUTSINK (reçoit même quand la fenêtre
    // n'a pas le focus). Pour notre besoin proximité, on n'a pas besoin de parser
    // le RAWINPUT — on appelle GetCursorPos pour avoir la position absolue à jour.

    public const uint WM_INPUT       = 0x00FF;
    public const uint RIDEV_INPUTSINK = 0x00000100;

    // WM_NCCALCSIZE lets us claim the entire window rect as client area.
    // Returning 0 with wParam=TRUE leaves rgrc[0] unchanged, so Windows
    // concludes there is no non-client area to paint — no caption, no
    // frame, no 3D edge — regardless of what WS_DLGFRAME / WS_EX_WINDOWEDGE
    // bits are still on the HWND. Canonical pattern for borderless custom-
    // chrome windows (used by Chromium, Electron, PowerToys).
    public const uint WM_NCCALCSIZE  = 0x0083;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    // ── Layered Window (alpha global, Mica inclus) ────────────────────────────
    // WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA) permet d'appliquer
    // un alpha 0-255 à la fenêtre entière, par-dessus la composition WinUI 3
    // (Mica compris). Sans ça, animer Content.Opacity ne touche pas le backdrop.

    public const int  GWL_STYLE    = -16;
    public const int  GWL_EXSTYLE   = -20;

    // WS_CAPTION is the composite style (WS_BORDER | WS_DLGFRAME) that causes
    // Windows to paint a title bar *and* the thin frame around the client
    // area. OverlappedPresenter.SetBorderAndTitleBar(false, false) is supposed
    // to clear both bits but does not fully — the frame around the client
    // area remains visible, which reads as a rough XP-style outline on
    // WS_EX_LAYERED overlays. Stripping WS_CAPTION explicitly on GWL_STYLE is
    // the documented Win32 workaround (Microsoft Q&A 1300756, WinUIEx #134,
    // WindowsAppSDK #3622).
    public const uint WS_CAPTION    = 0x00C00000;
    public const uint WS_THICKFRAME = 0x00040000;

    public const uint WS_EX_LAYERED    = 0x00080000;
    // WS_EX_TOOLWINDOW : exclut la fenêtre d'Alt+Tab et de la taskbar. Effet
    // de bord observé (non documenté) : les fenêtres tool topmost apparaissent
    // sur tous les bureaux virtuels. C'est le mécanisme qu'utilise PowerToys
    // pour ses overlays. Best-effort, peut casser sur futures builds Windows.
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    // WS_EX_TRANSPARENT : exclut la fenêtre du hit-testing. Les clics, le
    // curseur et la sélection traversent la HUD et atteignent la fenêtre
    // en dessous, quel que soit l'alpha layered appliqué.
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint LWA_ALPHA     = 0x00000002;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // Accessibility → Visual effects → "Animation effects". When the user
    // disables it, SystemParametersInfo(SPI_GETCLIENTAREAANIMATION) returns
    // pvParam=0. Our slide/fade animators short-circuit to the final state in
    // that case, so we never spin a timer for a transition the user opted out of.
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    // ── DWM window attributes ─────────────────────────────────────────────────
    //
    // DWMWA_WINDOW_CORNER_PREFERENCE (33) controls whether DWM clips the HWND
    // to rounded corners at the compositor level. DWMWA_BORDER_COLOR (34)
    // controls the 1-dip system accent stroke DWM paints around the HWND.
    // DWMWA_COLOR_NONE (0xFFFFFFFE) is the sentinel that disables that stroke.

    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint DWMWCP_DEFAULT                 = 0;
    public const uint DWMWCP_DONOTROUND              = 1;
    public const uint DWMWCP_ROUND                   = 2;
    public const uint DWMWCP_ROUNDSMALL              = 3;

    public const uint DWMWA_BORDER_COLOR  = 34;
    public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF; // system default accent stroke
    public const uint DWMWA_COLOR_NONE    = 0xFFFFFFFE; // disables the stroke entirely

    // DWMWA_SYSTEMBACKDROP_TYPE (38) controls the DWM system backdrop layer
    // rendered behind the window (Mica / Acrylic / Tabbed). DWMSBT_NONE (1)
    // explicitly disables the backdrop — distinct from DWMSBT_AUTO (0) which
    // lets the OS pick. WinUI 3 may auto-apply a backdrop when the Window's
    // SystemBackdrop property is unset on recent WindowsAppSDK versions;
    // setting the DWM attribute is the Win32-side guarantee that nothing
    // paints behind our opaque content.
    public const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const uint DWMSBT_AUTO              = 0;
    public const uint DWMSBT_NONE              = 1;

    // DWMWA_NCRENDERING_POLICY (2) tells DWM whether to render non-client
    // decorations (frame, 1-dip accent stroke, Shell dropshadow around
    // rounded corners) for the window. DWMNCRP_DISABLED (1) turns all of
    // that off — needed on the overlay cards so their Win11 rounded-corner
    // Shell shadow stops bleeding down onto the main HUD sitting 12 dip
    // below. The DWMWCP_ROUND corner clipping is a separate compositor-
    // level attribute and keeps working with NC rendering disabled.
    public const uint DWMWA_NCRENDERING_POLICY = 2;
    public const uint DWMNCRP_USEWINDOWSTYLE   = 0;
    public const uint DWMNCRP_DISABLED         = 1;
    public const uint DWMNCRP_ENABLED          = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    // ── Message-only window (tray + hotkey host) ─────────────────────────────
    //
    // A message-only window has HWND_MESSAGE as its parent. It is invisible,
    // has no z-order, cannot be enumerated, receives no broadcast messages,
    // and simply dispatches messages sent to it. Canonical Win32 pattern for
    // hosting tray callbacks and RegisterHotKey targets without a UI window.

    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x, int y,
        int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ── libwhisper.dll ────────────────────────────────────────────────────────

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_context_default_params_by_ref();

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_context_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_init_from_file_with_params(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        WhisperContextParams cparams);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_default_params_by_ref(int strategy);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full(IntPtr ctx, WhisperFullParams wparams, float[] samples, int n_samples);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_segments(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t0(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t1(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern float whisper_full_get_segment_no_speech_prob(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_tokens(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_get_token_id(IntPtr ctx, int i_segment, int i_token);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern float whisper_full_get_token_p(IntPtr ctx, int i_segment, int i_token);

    // Renvoie l'id à partir duquel les tokens sont des timestamps (<|0.00|>, <|5.30|>…).
    // Tout token dont l'id est >= à cette valeur est un token de timestamp, pas du texte.
    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_token_beg(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free(IntPtr ctx);

    // ── whisper_log_set : callback global pour les logs internes ──────────────
    //
    // whisper.cpp émet en permanence des lignes de log (chargement modèle,
    // démarrage du décodage, métriques GPU, timings, etc.). Par défaut elles
    // partent sur stderr où elles sont perdues. En branchant un callback on
    // redirige tout ça vers la LogWindow.
    //
    // Signature C : void (*)(enum ggml_log_level level, const char *text, void *user_data)
    // Niveaux ggml_log_level : 0=None, 1=Info, 2=Warn, 3=Error, 4=Debug, 5=Cont.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void WhisperLogCallback(int level, IntPtr text, IntPtr user_data);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_log_set(WhisperLogCallback log_callback, IntPtr user_data);
}
