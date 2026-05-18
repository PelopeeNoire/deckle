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
        // Tolerate case differences when reading legacy settings.json
        // files written before PropertyNamingPolicy was set (or by a
        // future record-typed property whose constructor parameter
        // names aren't transformed by the naming policy). Cheap
        // insurance — adds no cost on the well-formed read path.
        PropertyNameCaseInsensitive = true,
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

    /// <summary>Copies <see cref="AmbientModePresets"/> values for the
    /// given mode onto <see cref="Current"/>, also sets
    /// <see cref="AmbientSettings.Mode"/> to the same value, then
    /// saves. Custom is a no-op : the mode flips to Custom but every
    /// other knob keeps its current value (this is the path the
    /// Playground takes when the user starts tuning by hand). Fires
    /// <see cref="Changed"/> as part of <see cref="Save"/>.</summary>
    public void ApplyPreset(AmbientMode mode)
    {
        Current.Mode = mode;
        AmbientModePresets.Apply(mode, Current);
        Save();
    }
}
