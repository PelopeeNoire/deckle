using System.Text.Json;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Lighting.Ambient;

// Module-local persistence for AmbientSettings. Twin of
// WhispSettingsService and CaptureSettingsService — same JsonSettingsStore
// pattern, same singleton lazy, same naming convention. Backing file:
// <UserDataRoot>/modules/ambient/settings.json.
public sealed class AmbientSettingsService
{
    private static readonly Lazy<AmbientSettingsService> _instance = new(() => new AmbientSettingsService());
    public static AmbientSettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<AmbientSettings> _store;

    public AmbientSettings Current => _store.Current;

    public string Path => _store.Path;

    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private AmbientSettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "ambient", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<AmbientSettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Ambient-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[ambient] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[ambient] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[ambient] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[ambient] {msg}"));
    }

    public void Save()                       => _store.Save();
    public void Flush()                      => _store.Flush();
    public void Reload()                     => _store.Reload();
    public void Replace(AmbientSettings next) => _store.Replace(next);
}
