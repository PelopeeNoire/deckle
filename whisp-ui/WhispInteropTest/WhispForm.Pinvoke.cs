using System.Runtime.InteropServices;
using System.Text;

partial class WhispForm
{
    // ─── P/Invoke : user32.dll — SendInput ───────────────────────────────────
    //
    // SendInput : injecte des événements clavier/souris dans la file de messages Windows.
    // Remplace l'API keybd_event (dépréciée depuis Windows Vista).
    //
    // nInputs  : nombre d'éléments dans le tableau pInputs
    // pInputs  : tableau de structures INPUT décrivant chaque événement
    // cbSize   : taille en octets d'une seule structure INPUT (pour validation interne)
    // Retour   : nombre d'événements effectivement injectés (0 si bloqué par UIPI)

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ─── P/Invoke : user32.dll — gestion du focus ────────────────────────────

    // GetForegroundWindow : retourne le handle de la fenêtre actuellement au premier plan
    // (celle qui reçoit les frappes clavier de l'utilisateur).
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // SetForegroundWindow : demande à Windows de mettre la fenêtre hWnd au premier plan.
    // Nécessite que le process appelant ait le droit de changer le focus
    // (accordé temporairement après traitement d'un hotkey).
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    // ─── P/Invoke : user32.dll — hotkey ──────────────────────────────────────
    //
    // RegisterHotKey : demande à Windows d'envoyer WM_HOTKEY à notre fenêtre
    // quand la combinaison de touches est pressée, quelle que soit l'application active.
    //
    // hWnd        : handle de notre fenêtre — qui recevra le message WM_HOTKEY
    // id          : identifiant arbitraire pour distinguer plusieurs hotkeys
    // fsModifiers : masque de modificateurs (MOD_ALT, MOD_CTRL, MOD_SHIFT, MOD_WIN)
    // vk          : code virtuel de la touche principale

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // UnregisterHotKey : libère le raccourci (important : un raccourci non libéré
    // reste bloqué pour toutes les autres applis tant que le process tourne)
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ─── P/Invoke : user32.dll — focus et classe de fenêtre ─────────────────
    //
    // GetWindowThreadProcessId : retourne l'identifiant du thread qui a créé la fenêtre.
    //   Nécessaire pour appeler GetGUIThreadInfo sur le bon thread.
    //   lpdwProcessId (out) reçoit l'ID du process — ignoré ici (out _).
    //
    // GetGUIThreadInfo : remplit une struct GUITHREADINFO avec l'état UI du thread indiqué.
    //   hwndFocus dans la struct = le contrôle qui a actuellement le focus clavier.
    //
    // GetClassName : copie le nom de la classe Windows du handle dans le StringBuilder.
    //   La classe identifie le type de contrôle : "Edit", "RichEdit20W", etc.
    //   nMaxCount = capacité du StringBuilder (terminateur null inclus).

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // ─── P/Invoke : user32.dll — presse-papier ───────────────────────────────

    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    // ─── P/Invoke : winmm.dll ────────────────────────────────────────────────

    [DllImport("winmm.dll")]
    static extern uint waveInOpen(
        out IntPtr phwi, uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    static extern uint waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInClose(IntPtr hwi);

    // ─── P/Invoke : kernel32.dll ─────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr hMem);

    // ─── P/Invoke : libwhisper.dll ────────────────────────────────────────────

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_context_default_params_by_ref();

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free_context_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_init_from_file_with_params(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        WhisperContextParams cparams);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_full_default_params_by_ref(int strategy);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern int whisper_full(IntPtr ctx, WhisperFullParams wparams, float[] samples, int n_samples);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern int whisper_full_n_segments(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free(IntPtr ctx);
}
