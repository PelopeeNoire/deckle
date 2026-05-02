namespace Deckle.Settings;

// ── AppSettings ───────────────────────────────────────────────────────────────
//
// Shell-only settings root persisted to <UserDataRoot>/settings.json.
// Used to also aggregate every module's POCO (Whisp / Llm / Capture /
// Telemetry) but those moved to their own modules/<id>/settings.json
// files in slice C2b — see SettingsBootstrap.MigrateLegacyToPerModule
// for the dispatch logic.
//
// What stays here:
//   • Paths       — BackupDirectory only (ModelsDirectory migrated to
//                   WhispSettings since it's a Whisper-engine concern).
//   • Appearance  — global theme.
//   • Startup     — boot behaviour.
//   • Overlay     — HUD overlay system.
//   • Paste       — auto-paste vs clipboard-only.
//
// What moved out (loaded via the matching XxxSettingsService.Instance):
//   • CaptureSettings        → Deckle.Capture/CaptureSettingsService
//   • WhispSettings          → Deckle.Whisp/WhispSettingsService
//   • LlmSettings            → Deckle.Llm/LlmSettingsService
//   • TelemetrySettings      → Deckle.Logging/TelemetrySettingsService
//
// Mutations to `Current` go through SettingsService (debounced Save).
public sealed class AppSettings
{
    public PathsSettings       Paths       { get; set; } = new();
    public AppearanceSettings  Appearance  { get; set; } = new();
    public StartupSettings     Startup     { get; set; } = new();
    public OverlaySettings     Overlay     { get; set; } = new();
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

// Chemins utilisateur. Vides par défaut → résolution automatique via
// AppPaths (à côté de l'exe en dev unpackaged, sous LocalState en packagé MSIX).
//
// BackupDirectory  : dossier où SettingsBackupService dépose les snapshots
//                    settings-YYYYMMDD-HHmmss.json. Pattern PowerToys :
//                    le user peut pointer vers un dossier OneDrive/Drive
//                    pour faire suivre ses backups entre machines.
//
// ModelsDirectory used to live here too; it migrated to WhispSettings on
// 2026-05-02 (per-module persistence) since it's a Whisper-engine concern.
public sealed class PathsSettings
{
    public string BackupDirectory { get; set; } = "";
}
