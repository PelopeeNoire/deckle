using System.Text.Json;
using Deckle.Core;

namespace Deckle.Logging;

// ── TelemetrySettingsService ──────────────────────────────────────────────
//
// Module-local persistence for TelemetrySettings. Twin of
// WhispSettingsService — see that file's comment for the design rationale.
//
// Lives in Deckle.Logging because the telemetry sinks are the primary
// consumer (latency.jsonl / corpus.jsonl / app.jsonl / microphone.jsonl
// gates). The Settings UI reads it through
// TelemetrySettingsService.Instance.Current the same way every other
// caller does.
//
// Special quirk: the legacy `corpusLogging` → `telemetry` JSON key
// rename (2026-04-21 telemetry unification) runs in
// Deckle.Settings.SettingsService.MigrateLegacyKeys against the combined
// settings.json before per-module dispatch picks up the now-renamed
// `telemetry` key. So an old config carrying `corpusLogging` migrates to
// `telemetry` (still in the combined file), then dispatches to
// modules/telemetry/settings.json, all on the same launch.
//
// LogService is in this same assembly, but we still inject the log
// callbacks via JsonSettingsStore so the bootstrap order doesn't bite:
// LogService static initializers and JsonSettingsStore<T> singleton
// initialization happen in unspecified order in WinUI 3 startup, and
// passing closures lets each one fire lazily.
public sealed class TelemetrySettingsService
{
    private static readonly Lazy<TelemetrySettingsService> _instance = new(() => new TelemetrySettingsService());
    public static TelemetrySettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<TelemetrySettings> _store;

    public TelemetrySettings Current => _store.Current;

    public string Path => _store.Path;

    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private TelemetrySettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "telemetry", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<TelemetrySettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Telemetry-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[telemetry] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[telemetry] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[telemetry] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[telemetry] {msg}"));
    }

    public void Save()                        => _store.Save();
    public void Flush()                       => _store.Flush();
    public void Reload()                      => _store.Reload();
    public void Replace(TelemetrySettings next) => _store.Replace(next);
}
