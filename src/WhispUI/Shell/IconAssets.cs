using Microsoft.UI.Windowing;

namespace WhispUI.Shell;

// ─── Icon asset resolution ───────────────────────────────────────────────────
//
// Single source of truth for .ico file paths.
// Shared between TrayIconManager (Win32 LoadImage) and LogWindow
// (BitmapImage + AppWindow.SetIcon).
//
// Icons are copied to the output directory by the .csproj Content items,
// so they always live next to the exe at Assets/Icons/.

public static class IconAssets
{
    private const string FileIdle      = "recording--indicator--false--32px.ico";
    private const string FileRecording = "recording--indicator--true--32px.ico";

    public static string? ResolvePath(bool recording)
    {
        string fileName = recording ? FileRecording : FileIdle;

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    // Helper pour les fenêtres qui n'ont pas besoin de swap dynamique
    // (HUD, Anchor) — pose juste l'icône idle une fois en constructeur.
    public static void ApplyToWindow(AppWindow appWindow, bool recording = false)
    {
        var path = ResolvePath(recording);
        if (path is not null) appWindow.SetIcon(path);
    }
}
