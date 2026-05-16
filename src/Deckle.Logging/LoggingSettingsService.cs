using System.Text.Json;
using Deckle.Core;

namespace Deckle.Logging;

// ── LoggingSettingsService ──────────────────────────────────────────────────
//
// Module-local persistence for LoggingSettings. Twin of
// TelemetrySettingsService — same JsonSettingsStore pattern, same
// singleton lazy, same naming convention. Backing file lives at
// <UserDataRoot>/modules/logging/settings.json so it's discoverable
// alongside its sibling modules instead of buried under telemetry/.
//
// Consumer side : TelemetryService.Log() reads
// Instance.Current.VerboseLoggingEnabled on every emission to filter
// Verbose-level events at the source (so neither the LogWindow nor the
// JSONL sink see them when the toggle is off). The read is wrapped in
// a try/catch fallback to false so a settings I/O failure can never
// take down the log pipeline — same fail-safe posture as
// TelemetryGates.
//
// Bootstrap note : LogService is in this same assembly. We still
// inject the log callbacks via JsonSettingsStore lambdas so the
// initialization order doesn't bite — LogService static state and
// JsonSettingsStore<T> singleton initialization happen in
// unspecified order in WinUI 3 startup, and passing closures lets
// each one fire lazily.
public sealed class LoggingSettingsService
{
    private static readonly Lazy<LoggingSettingsService> _instance = new(() => new LoggingSettingsService());
    public static LoggingSettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<LoggingSettings> _store;

    public LoggingSettings Current => _store.Current;

    public string Path => _store.Path;

    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private LoggingSettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "logging", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<LoggingSettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Logging-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[logging] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[logging] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[logging] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[logging] {msg}"));
    }

    public void Save()                      => _store.Save();
    public void Flush()                     => _store.Flush();
    public void Reload()                    => _store.Reload();
    public void Replace(LoggingSettings next) => _store.Replace(next);
}
