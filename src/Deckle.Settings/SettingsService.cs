using System;
using System.Text.Json;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Settings;

// ── SettingsService ───────────────────────────────────────────────────────────
//
// Singleton owner of the shell AppSettings POCO (Paths / Appearance /
// Startup / Overlay / Paste). Persists to <UserDataRoot>/settings.json
// via JsonSettingsStore<AppSettings>.
//
// Module-specific settings (Whisp / Llm / Capture / Telemetry) used to
// hang off this AppSettings root, but moved to per-module SettingsServices
// in slice C2b — see WhispSettingsService / LlmSettingsService /
// CaptureSettingsService / TelemetrySettingsService and
// SettingsBootstrap.MigrateLegacyToPerModule for the dispatch logic.
//
// What stays here:
//   • Storage primitive: load / save / debounce / atomic write / mutex
//     (delegated to JsonSettingsStore<T>).
//   • ResolveBackupDirectory: user override or AppPaths default.
//   • The Reload entry point used by SettingsBackupService.RestoreFromBackup.
//
// Notably absent now: ResolveModelsDirectory (moved to
// WhispSettingsService since the model directory is a Whisper-engine
// concern), MigrateProfileIds (moved to Deckle.Llm/LlmSettingsMigrations),
// MigrateLegacyKeys (moved to SettingsBootstrap so it runs before the
// per-module dispatch).
//
// Thread-safety: see JsonSettingsStore<T>.
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<AppSettings> _store;

    public AppSettings Current => _store.Current;

    // Exposed read-only so SettingsBackupService can locate the live file
    // without re-resolving AppPaths.SettingsFilePath. Internal callers
    // only — this is not part of any settings-changing surface.
    internal string ConfigPath => _store.Path;

    /// <summary>
    /// Raised after a successful disk write. UI consumers subscribe to
    /// refresh bound state when settings.json changes externally
    /// (Restore from backup) or on an explicit Save flush.
    /// </summary>
    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private SettingsService()
    {
        // UserDataRoot is created by AppPaths static ctor; redundant
        // CreateDirectory left out on purpose so this constructor reads
        // as "AppPaths owns the location, we just consume it".
        //
        // No migration hooks here: SettingsBootstrap.MigrateLegacyToPerModule
        // runs **before** this singleton initializes (App.OnLaunched first
        // step), so the legacy module sections + key renames have already
        // been processed by the time JsonSettingsStore parses the slimmed
        // shell-only file.
        _store = new JsonSettingsStore<AppSettings>(
            path:        AppPaths.SettingsFilePath,
            mutexName:   AppPaths.SettingsMutexName,
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, msg),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, msg),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, msg),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, msg));
    }

    /// <summary>Schedule a debounced disk write (300 ms).</summary>
    public void Save() => _store.Save();

    /// <summary>Synchronous flush. Use before process exit / restart.</summary>
    public void Flush() => _store.Flush();

    /// <summary>Re-read from disk and replace the in-memory snapshot.</summary>
    public void Reload() => _store.Reload();

    // Resolves the directory where settings backups (snapshots) live.
    // User override wins, otherwise a `backups/` folder next to settings.json.
    // Returned path may not exist yet — SettingsBackupService creates it on
    // the first CreateBackup call.
    public string ResolveBackupDirectory()
    {
        string user = Current.Paths.BackupDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return AppPaths.SettingsBackupDirectory;
    }
}
