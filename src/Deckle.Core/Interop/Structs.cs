using System.Runtime.InteropServices;

namespace Deckle.Interop;

// ── POINT ─────────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

// ── RAWINPUTDEVICE ────────────────────────────────────────────────────────────
// Décrit un device source pour RegisterRawInputDevices.
// Pour la souris : usUsagePage=0x01 (Generic Desktop), usUsage=0x02 (Mouse).
// dwFlags=RIDEV_INPUTSINK : recevoir les events même sans focus (hwndTarget requis).
[StructLayout(LayoutKind.Sequential)]
public struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public uint   dwFlags;
    public IntPtr hwndTarget;
}

// ── NOTIFYICONDATA ────────────────────────────────────────────────────────────
//
// Structure Shell32 pour gérer l'icône dans la zone de notification.
// CharSet.Unicode : les champs szTip/szInfo sont des WCHAR[].
// cbSize doit être défini à Marshal.SizeOf<NOTIFYICONDATA>() avant tout appel.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NOTIFYICONDATA
{
    public uint   cbSize;
    public IntPtr hWnd;
    public uint   uID;
    public uint   uFlags;
    public uint   uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;
    public uint   dwState;
    public uint   dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;
    public uint   uTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;
    public uint   dwInfoFlags;
    public Guid   guidItem;
    public IntPtr hBalloonIcon;
}

// ── WNDCLASSEX ────────────────────────────────────────────────────────────────
//
// Window class descriptor passed to RegisterClassEx. cbSize must be set to
// Marshal.SizeOf<WNDCLASSEX>() before the call. lpfnWndProc is an IntPtr to a
// function pointer obtained via Marshal.GetFunctionPointerForDelegate — the
// delegate itself must be rooted in a managed field to keep it alive.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEX
{
    public uint   cbSize;
    public uint   style;
    public IntPtr lpfnWndProc;
    public int    cbClsExtra;
    public int    cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public string? lpszMenuName;
    public string  lpszClassName;
    public IntPtr hIconSm;
}

// INPUT aplati : représente un événement clavier pour SendInput.
//
// La struct Windows INPUT contient une union C (clavier, souris, matériel).
// Taille totale sur Windows 64 bits = 40 octets (MOUSEINPUT fixe la taille de l'union).
// L'union est dimensionnée par MOUSEINPUT (le plus grand membre).
// Le champ _pad à l'offset 32 force Marshal.SizeOf à retourner 40.
[StructLayout(LayoutKind.Explicit)]
public struct INPUT
{
    [FieldOffset(0)]  public uint   type;
    [FieldOffset(8)]  public ushort ki_wVk;
    [FieldOffset(10)] public ushort ki_wScan;
    [FieldOffset(12)] public uint   ki_dwFlags;
    [FieldOffset(16)] public uint   ki_time;
    [FieldOffset(24)] public IntPtr ki_dwExtraInfo;
    [FieldOffset(32)] public long   _pad;            // padding pour atteindre 40 octets
}

[StructLayout(LayoutKind.Sequential)]
public struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint   nSamplesPerSec;
    public uint   nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
public struct WAVEHDR
{
    public IntPtr lpData;           // pointeur vers le buffer de données audio
    public uint   dwBufferLength;   // taille totale du buffer (octets)
    public uint   dwBytesRecorded;  // octets effectivement écrits par le driver
    public IntPtr dwUser;           // donnée utilisateur libre (non utilisé ici)
    public uint   dwFlags;          // flags : WHDR_DONE = buffer rempli par le driver
    public uint   dwLoops;          // nombre de boucles (lecture seulement)
    public IntPtr lpNext;           // usage interne driver
    public IntPtr reserved;         // usage interne driver
}

