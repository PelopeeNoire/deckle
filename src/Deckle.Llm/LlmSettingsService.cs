using System.Text.Json;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Llm;

// ── LlmSettingsService ────────────────────────────────────────────────────
//
// Module-local persistence for LlmSettings. Twin of WhispSettingsService —
// see that file's comment for the design rationale (cycle avoidance via
// per-module ownership of settings + file).
//
// Backing file: <UserDataRoot>/modules/llm/settings.json. Migration from
// the legacy combined settings.json is handled by
// SettingsBootstrap.MigrateLegacyToPerModule().
//
// Profile-id reconciliation (filling missing 12-char Guid suffixes,
// re-pairing rules and slots against the live Profiles list) lives next
// door in LlmSettingsMigrations.RepairProfileReferences, wired here as
// the postLoadMigration callback so loads-from-disk are repaired in
// place. Page-level reset paths call the helper explicitly before
// Replace().
public sealed class LlmSettingsService
{
    private static readonly Lazy<LlmSettingsService> _instance = new(() => new LlmSettingsService());
    public static LlmSettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<LlmSettings> _store;

    public LlmSettings Current => _store.Current;

    public string Path => _store.Path;

    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private LlmSettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "llm", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<LlmSettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Llm-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[llm] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[llm] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[llm] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[llm] {msg}"),
            // Profile id reconciliation: fill missing 12-char Guid suffixes
            // and re-pair rules/slots against the live Profiles list.
            postLoadMigration: LlmSettingsMigrations.RepairProfileReferences);
    }

    public void Save()                  => _store.Save();
    public void Flush()                 => _store.Flush();
    public void Reload()                => _store.Reload();
    public void Replace(LlmSettings next) => _store.Replace(next);
}
