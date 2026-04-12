using Microsoft.UI.Windowing;

namespace WhispUI.Shell;

// ─── Résolution des assets icônes ─────────────────────────────────────────────
//
// Source de vérité unique pour le chemin des .ico de l'application.
// Partagé entre TrayIconManager (Win32 LoadImage) et LogWindow
// (BitmapImage + AppWindow.SetIcon).
//
// Cherche d'abord à côté de l'exe (publish/runtime), puis remonte de quatre
// niveaux pour le mode dev (bin/x64/<Config>/<TFM>/ → racine du projet).

public static class IconAssets
{
    private const string FileIdle      = "recording--indicator--false--32px.ico";
    private const string FileRecording = "recording--indicator--true--32px.ico";

    public static string? ResolvePath(bool recording)
    {
        string fileName = recording ? FileRecording : FileIdle;

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icons", fileName),
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        return null;
    }

    // Helper pour les fenêtres qui n'ont pas besoin de swap dynamique
    // (HUD, Anchor) — pose juste l'icône idle une fois en constructeur.
    public static void ApplyToWindow(AppWindow appWindow, bool recording = false)
    {
        var path = ResolvePath(recording);
        if (path is not null) appWindow.SetIcon(path);
    }
}
