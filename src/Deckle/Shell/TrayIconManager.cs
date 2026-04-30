using System.Runtime.InteropServices;
using WhispUI.Interop;
using WhispUI.Localization;

namespace WhispUI.Shell;

// ─── Icône dans la zone de notification système ───────────────────────────────
//
// Implémentation via Shell_NotifyIcon (Shell32) — pas de dépendance WinForms.
//
// Flux des événements :
//   Shell32 → WM_TRAY (WM_USER+1) envoyé au HWND principal
//   SubclassCallback dans HotkeyManager intercepte WM_TRAY
//   → clic droit → TrackPopupMenu → commande choisie
//
// Register() doit être appelé depuis OnRootLoaded (après que le message pump
// WinUI 3 est en place) pour que les messages WM_TRAY soient bien acheminés.

public sealed class TrayIconManager : IDisposable
{
    // IDs des commandes du menu contextuel
    private const uint CMD_LOGS       = 1;
    private const uint CMD_SETTINGS   = 3;
    private const uint CMD_PLAYGROUND = 5;
    private const uint CMD_RESTART    = 4;
    private const uint CMD_QUIT       = 2;

    private IntPtr _hwnd;
    private IntPtr _hIconIdle;
    private IntPtr _hIconRecording;
    private bool   _iconsOwned;   // false si fallback LoadIcon (icône partagée — pas de DestroyIcon)
    private bool   _iconAdded;
    private bool   _disposed;

    // Délégué SubclassProc — doit vivre dans un champ pour éviter la collecte GC
    private NativeMethods.SubclassProc? _subclassDelegate;
    private static readonly UIntPtr SubclassId = new(0x5752_4159); // "WRAY"

    // Callbacks vers l'app (marshaling UI déjà fait par l'abonné)
    public Action? OnShowLogs         { get; set; }
    public Action? OnShowSettings     { get; set; }
    public Action? OnShowPlayground   { get; set; }
    public Action? OnToggleRecording  { get; set; }
    public Action? OnRestart          { get; set; }
    public Action? OnQuit             { get; set; }

    // ── Initialisation ────────────────────────────────────────────────────────

    public void Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        (_hIconIdle,      _iconsOwned) = LoadIconFromFile(active: false);
        (_hIconRecording, _)           = LoadIconFromFile(active: true);

        // Add the icon in the notification area.
        // Neutral placeholder: UpdateStatus("Ready") from App.OnLaunched
        // replaces this moments later. Keeping the string aligned with
        // UpdateStatus avoids flashing a stale "loading" message since the
        // model is lazy-loaded on first hotkey, not at boot.
        var data = BuildNotifyIconData(Loc.Format("Tray_Tooltip_Format", Loc.Get("Status_Ready")), _hIconIdle);
        bool ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!ok)
            throw new InvalidOperationException(
                $"Shell_NotifyIcon(NIM_ADD) échoué — hWnd={_hwnd}, hIcon={_hIconIdle}");
        _iconAdded = true;

        // Subclasser le HWND pour intercepter WM_TRAY
        _subclassDelegate = SubclassCallback;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);
    }

    // ── Status update ───────────────────────────────────────────────────────
    //
    // Wired in App.OnLaunched as the unique sink of WhispEngine.StatusChanged:
    // every transition emitted by the engine (Loading model… → Ready →
    // Recording… → Transcribing… → Rewriting (...)… → Ready) lands here AND
    // in LogService.Status, so the tooltip is by construction in sync with
    // the live pipeline state visible in the LogWindow / app.jsonl.
    //
    // Tip caps at 127 chars (Shell_NotifyIcon szTip limit). Icon swaps to
    // the recording variant whenever the status starts with "Recording" —
    // StartsWith covers both the bare and the ellipsis form ("Recording…").
    public void UpdateStatus(string status)
    {
        bool isRecording = status.StartsWith("Recording");
        IntPtr icon = isRecording ? _hIconRecording : _hIconIdle;

        string tip = Loc.Format("Tray_Tooltip_Format", status);
        if (tip.Length > 127) tip = tip[..127];

        var data = BuildNotifyIconData(tip, icon);
        data.uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    // ── Interception des messages tray ────────────────────────────────────────

    private IntPtr SubclassCallback(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_TRAY)
        {
            uint mouseEvent = (uint)(lParam.ToInt64() & 0xFFFF);

            if (mouseEvent == NativeMethods.WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }

            if (mouseEvent == NativeMethods.WM_LBUTTONUP)
            {
                // Clic gauche = toggle transcription (équivalent hotkey standard).
                // Permet de lancer/arrêter à la souris quand une seule main est
                // disponible. Logs et Settings passent par le clic droit.
                OnToggleRecording?.Invoke();
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── Menu contextuel natif ─────────────────────────────────────────────────

    private void ShowContextMenu()
    {
        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING,    CMD_LOGS,       "Logs");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING,    CMD_SETTINGS,   "Settings");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING,    CMD_PLAYGROUND, "Playground");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0,              null);
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING,    CMD_RESTART,    "Restart");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING,    CMD_QUIT,       "Quit");

        NativeMethods.GetCursorPos(out POINT pt);

        // SetForegroundWindow est requis avant TrackPopupMenu pour que le menu
        // se ferme correctement quand l'utilisateur clique ailleurs.
        NativeMethods.SetForegroundWindow(_hwnd);

        uint cmd = NativeMethods.TrackPopupMenu(
            hMenu,
            NativeMethods.TPM_LEFTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_BOTTOMALIGN,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(hMenu);

        switch (cmd)
        {
            case CMD_LOGS:       OnShowLogs?.Invoke();       break;
            case CMD_SETTINGS:   OnShowSettings?.Invoke();   break;
            case CMD_PLAYGROUND: OnShowPlayground?.Invoke(); break;
            case CMD_RESTART:    OnRestart?.Invoke();        break;
            case CMD_QUIT:       OnQuit?.Invoke();           break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NOTIFYICONDATA BuildNotifyIconData(string tip, IntPtr hIcon)
    {
        return new NOTIFYICONDATA
        {
            cbSize          = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd            = _hwnd,
            uID             = 1,
            uFlags          = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAY,
            hIcon           = hIcon,
            szTip           = tip,
            szInfo          = "",
            szInfoTitle     = "",
        };
    }

    // Retourne (hIcon, owned) : owned=true si chargé depuis fichier (→ DestroyIcon requis),
    // owned=false si icône partagée système (→ NE PAS appeler DestroyIcon).
    // Le chemin du .ico vient de IconAssets, source de vérité partagée avec LogWindow.
    private static (IntPtr hIcon, bool owned) LoadIconFromFile(bool active)
    {
        string? path = IconAssets.ResolvePath(recording: active);
        if (path is not null)
        {
            IntPtr hIcon = NativeMethods.LoadImage(
                IntPtr.Zero, path,
                NativeMethods.IMAGE_ICON, 32, 32,
                NativeMethods.LR_LOADFROMFILE);

            if (hIcon != IntPtr.Zero)
                return (hIcon, owned: true);
        }

        // Fallback : icône Windows générique (IDI_APPLICATION = 32512).
        // LoadIcon retourne une icône partagée — NE PAS appeler DestroyIcon dessus.
        // Garantit un item tray visible même si les assets ne sont pas copiés.
        const nint IDI_APPLICATION = 32512;
        return (NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION)), owned: false);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_subclassDelegate is not null && _hwnd != IntPtr.Zero)
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);

        if (_iconAdded)
        {
            var data = BuildNotifyIconData("", IntPtr.Zero);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        }

        // Libérer uniquement les icônes chargées depuis fichier (owned).
        // Les icônes système (LoadIcon) sont partagées — DestroyIcon interdit.
        if (_iconsOwned)
        {
            if (_hIconIdle != IntPtr.Zero)      NativeMethods.DestroyIcon(_hIconIdle);
            if (_hIconRecording != IntPtr.Zero) NativeMethods.DestroyIcon(_hIconRecording);
        }
    }
}
