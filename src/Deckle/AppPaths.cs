using System;
using System.IO;

namespace Deckle;

// ── AppPaths ───────────────────────────────────────────────────────────────
//
// Centralized path resolution. Single source of truth for where the app
// reads and writes user data on disk. All mutable per-user state lives
// under <UserDataRoot>:
//
//   • settings.json — single config file, sits at the root
//   • backups/      — settings backup snapshots
//   • telemetry/    — JSONL files (app, latency, microphone) + per-profile corpus
//   • models/       — Whisper ggml-*.bin
//   • native/       — libwhisper.dll, ggml*.dll
//   • benchmark/    — optional, installed on demand from Settings
//
// Default <UserDataRoot> = %LOCALAPPDATA%\<AppFolderName>\, the canonical
// per-user data root on Windows (Settings Win11, PowerToys, every
// first-party Microsoft desktop app). Override with the WHISP_DATA_ROOT
// env var to keep %LOCALAPPDATA% clean during development.
//
// The application binary itself stays read-only and Program Files-friendly:
// it ships with Assets but no models, no native DLLs, no config.
// scripts/setup-assets.ps1 populates <UserDataRoot>\models\ and \native\
// before first run; the future first-run wizard will replace it from inside
// the app (see Shell/WelcomeWizardWindow).
public static class AppPaths
{
    // Filesystem-safe folder name. Single source of truth for filesystem
    // paths and the inter-process settings mutex. Swapped to the final
    // user-facing brand in Lot C; until then the working title doubles as
    // the folder name.
    public const string AppFolderName = "WhispUI";

    // Inter-process mutex name used by SettingsService to serialize writes
    // across concurrent app instances. Derived from AppFolderName so the
    // single rename in Lot C carries through.
    public const string SettingsMutexName = $"{AppFolderName}-Settings-Save";

    // Override env var. Pointed at a freshly-organized dev folder so
    // user data ends up there instead of polluting %LOCALAPPDATA%.
    // Empty/unset → default location.
    public const string DataRootEnvVar = "WHISP_DATA_ROOT";

    public static string UserDataRoot            { get; }
    public static string SettingsFilePath        { get; }
    public static string SettingsBackupDirectory { get; }
    public static string TelemetryDirectory      { get; }
    public static string ModelsDirectory         { get; }
    public static string NativeDirectory         { get; }
    public static string BenchmarkDirectory      { get; }

    static AppPaths()
    {
        UserDataRoot            = ResolveUserDataRoot();
        SettingsFilePath        = Path.Combine(UserDataRoot, "settings.json");
        SettingsBackupDirectory = Path.Combine(UserDataRoot, "backups");
        TelemetryDirectory      = Path.Combine(UserDataRoot, "telemetry");
        ModelsDirectory         = Path.Combine(UserDataRoot, "models");
        NativeDirectory         = Path.Combine(UserDataRoot, "native");
        BenchmarkDirectory      = Path.Combine(UserDataRoot, "benchmark");

        // UserDataRoot + telemetry are created eagerly — those are the two
        // locations the app writes to during normal operation. Models,
        // native, and benchmark are populated by the wizard or the user;
        // creating them empty here would mask the "missing dependencies"
        // detection done by Setup/NativeRuntime and Setup/SpeechModels.
        // Backups dir is created on first write by SettingsBackupService.
        Directory.CreateDirectory(UserDataRoot);
        Directory.CreateDirectory(TelemetryDirectory);

        TryMigrateLegacySettingsLayout();
    }

    // Best-effort migration of the previous layout where settings lived in a
    // dedicated <UserDataRoot>\settings\ subfolder. The new layout puts the
    // single settings.json at the root and the backups\ folder beside it.
    // No-op once the migration has run; failures are swallowed so a quirky
    // filesystem state doesn't bring the app down at start-up.
    private static void TryMigrateLegacySettingsLayout()
    {
        try
        {
            string legacyDir  = Path.Combine(UserDataRoot, "settings");
            string legacyFile = Path.Combine(legacyDir, "settings.json");

            if (File.Exists(legacyFile) && !File.Exists(SettingsFilePath))
                File.Move(legacyFile, SettingsFilePath);

            string legacyBackups = Path.Combine(legacyDir, "backups");
            if (Directory.Exists(legacyBackups) && !Directory.Exists(SettingsBackupDirectory))
                Directory.Move(legacyBackups, SettingsBackupDirectory);

            if (Directory.Exists(legacyDir) &&
                Directory.GetFileSystemEntries(legacyDir).Length == 0)
                Directory.Delete(legacyDir);
        }
        catch
        {
            // best-effort; the app keeps booting against the new layout
        }
    }

    // <UserDataRoot> resolution order:
    //   1. WHISP_DATA_ROOT env var (dev override)
    //   2. %LOCALAPPDATA%\<AppFolderName>\        ← canonical Windows location
    //   3. <exeDir>\<AppFolderName>\              ← portable fallback
    //
    // Step 3 covers sandboxed runs where LOCALAPPDATA isn't available
    // (rare, but a USB-stick portable mode is a plausible future use).
    private static string ResolveUserDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(DataRootEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, AppFolderName);

        return Path.Combine(AppContext.BaseDirectory, AppFolderName);
    }
}
