using System.Runtime.InteropServices;

namespace WhispUI;

internal static class NativeMethods
{
    // ── Hotkey ────────────────────────────────────────────────────────────────

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000; // évite les WM_HOTKEY répétés par l'auto-repeat clavier

    public const uint VK_OEM_3 = 0xC0; // touche ` (backtick) sur clavier AZERTY/QWERTY

    public const int HOTKEY_ID_TRANSCRIBE = 1; // Alt+`
    public const int HOTKEY_ID_REWRITE    = 2; // Alt+Ctrl+`

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Positionnement fenêtre ────────────────────────────────────────────────

    public static readonly IntPtr HWND_TOP      = new(0);
    public static readonly IntPtr HWND_TOPMOST  = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

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
    // WM_NCACTIVATE : DWM l'envoie pour changer l'etat actif/inactif du chrome
    // non-client (titlebar, border, shadow). Intercepte dans le subclass de la
    // HudWindow pour forcer wParam=TRUE en permanence, afin que DWM peigne la
    // HUD avec l'ombre "Shell Shadows / Active Window" meme quand elle n'a
    // pas le focus clavier (HUD est toujours SW_SHOWNOACTIVATE + WS_EX_NOACTIVATE).
    public const uint WM_NCACTIVATE  = 0x0086;
    public const uint RIDEV_INPUTSINK = 0x00000100;

    // ── DWM : system backdrop type ────────────────────────────────────────────
    // Signal canonique pour dire a DWM : "cette fenetre est un popup transient,
    // peint-la comme un menu/flyout/dialog" — y compris l'ombre Shell riche.
    // Sans ca, DWM reste en DWMSBT_AUTO et applique le rendu shell par defaut
    // (ombre aplatie). Requis Windows 11 Build 22621+.
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_TRANSIENTWINDOW    = 3;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    // ── Layered Window (alpha global, Mica inclus) ────────────────────────────
    // WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA) permet d'appliquer
    // un alpha 0-255 à la fenêtre entière, par-dessus la composition WinUI 3
    // (Mica compris). Sans ça, animer Content.Opacity ne touche pas le backdrop.

    public const int  GWL_EXSTYLE   = -20;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

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
}
