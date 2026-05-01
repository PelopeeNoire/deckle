using Deckle.Logging;
using Deckle.Whisp;

namespace Deckle.Settings;

// ── AppSettings ───────────────────────────────────────────────────────────────
//
// POCO racine sérialisé en JSON vers AppPaths.SettingsFilePath
// (= <UserDataRoot>\settings.json, par défaut %LOCALAPPDATA%\<AppFolderName>\).
// Organisé par intention utilisateur, pas par groupes techniques de whisper.cpp.
// Les défauts sont ceux du rapport de cartographie des paramètres Whisper.
//
// Toute modification passe par SettingsService (Load/Save, debounced, Changed).
//
// Chaque module porte son propre POCO de settings dans son projet :
//   RecordingSettings, LevelWindowSettings, WhispSettings (et ses 6 sections)
//                                                          → Deckle.Whisp
//   TelemetrySettings                                       → Deckle.Logging
//   LlmSettings                                             → Deckle.Llm
// Les sections shell (Paths / Appearance / Startup / Overlay / Paste) restent
// ici, dans le projet App.
public sealed class AppSettings
{
    public PathsSettings       Paths       { get; set; } = new();
    public RecordingSettings   Recording   { get; set; } = new();
    public AppearanceSettings  Appearance  { get; set; } = new();
    public StartupSettings     Startup     { get; set; } = new();
    public OverlaySettings     Overlay     { get; set; } = new();
    public WhispSettings       Whisp       { get; set; } = new();
    public LlmSettings         Llm         { get; set; } = new();
    public TelemetrySettings   Telemetry   { get; set; } = new();
    public PasteSettings       Paste       { get; set; } = new();
}

// Auto-paste after transcription. Off by default — the clipboard is the safe
// default and the user explicitly opts in to SendInput-driven paste. When
// false, the engine skips PasteFromClipboard entirely and the HUD shows the
// "Copied to clipboard" message instead of "Pasted".
public sealed class PasteSettings
{
    public bool AutoPasteEnabled { get; set; } = false;
}

// Apparence globale. Theme = "System" | "Light" | "Dark".
public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "System";
}

// Comportement au démarrage.
public sealed class StartupSettings
{
    // Run a silent dummy transcription at launch to warm up the Whisper model
    // (load + first inference pay the heavy cost). The real first hotkey press
    // then skips the cold start. Hidden from HUD and tray — pure background.
    public bool WarmupOnLaunch { get; set; } = true;
}

// Overlay HUD affiché pendant l'enregistrement/transcription.
// Position = "BottomCenter" | "BottomRight" | "TopCenter".
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool FadeOnProximity { get; set; } = true;
    public string Position { get; set; } = "BottomCenter";

    // Enables the 150 ms slide + fade transitions on the HUD and overlay
    // message cards. On by default — unlike chrome animations, message
    // transitions are critical for the user to track what just replaced what,
    // so we ignore SPI_GETCLIENTAREAANIMATION and only consult this toggle.
    // Windows itself does the same for load-bearing motion (Task Manager pane,
    // Settings NavigationView) when reduced-motion is enabled globally.
    public bool Animations { get; set; } = true;
}

// Chemins utilisateur. Tous vides par défaut → résolution automatique via
// AppPaths (à côté de l'exe en dev unpackaged, sous LocalState en packagé MSIX).
//
// ModelsDirectory  : dossier des .bin Whisper (large-v3, base, Silero VAD).
// BackupDirectory  : dossier où SettingsBackupService dépose les snapshots
//                    settings-YYYYMMDD-HHmmss.json. Pattern PowerToys :
//                    le user peut pointer vers un dossier OneDrive/Drive
//                    pour faire suivre ses backups entre machines.
public sealed class PathsSettings
{
    public string ModelsDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
}
