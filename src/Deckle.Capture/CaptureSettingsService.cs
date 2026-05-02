using System.Text.Json;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Capture;

// ── CaptureSettingsService ────────────────────────────────────────────────
//
// Module-local persistence for CaptureSettings. Twin of
// WhispSettingsService — see that file's comment for the design rationale.
//
// Backing file: <UserDataRoot>/modules/capture/settings.json. Migration
// from the legacy combined settings.json is handled by
// SettingsBootstrap.MigrateLegacyToPerModule(). The legacy `recording` →
// `capture` JSON key rename (2026-05-02 module extraction) stays inside
// Deckle.Settings.SettingsService.MigrateLegacyKeys for now — it runs
// against the legacy combined file before per-module dispatch picks up
// the (now-renamed) `capture` key.
public sealed class CaptureSettingsService
{
    private static readonly Lazy<CaptureSettingsService> _instance = new(() => new CaptureSettingsService());
    public static CaptureSettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<CaptureSettings> _store;

    public CaptureSettings Current => _store.Current;

    public string Path => _store.Path;

    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private CaptureSettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "capture", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<CaptureSettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Capture-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[capture] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[capture] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[capture] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[capture] {msg}"));
    }

    public void Save()                      => _store.Save();
    public void Flush()                     => _store.Flush();
    public void Reload()                    => _store.Reload();
    public void Replace(CaptureSettings next) => _store.Replace(next);
}
