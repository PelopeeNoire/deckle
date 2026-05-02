using System.Text.Json;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Whisp;

// ── WhispSettingsService ──────────────────────────────────────────────────
//
// Module-local persistence for WhispSettings. Each module that owns
// settings has its own service backed by JsonSettingsStore<T>; the JSON
// file lives at <UserDataRoot>/modules/whisp/settings.json so the file
// system layout reflects the module boundary one-to-one.
//
// Why per-module rather than the previous monolithic AppSettings root?
// So the Whisp module can move into a place where it owns its UI surface
// (slice C1: WhisperPage migrating into Deckle.Whisp) without having to
// reach back into Deckle.Settings to read its own settings — that would
// close the dependency cycle Whisp → Settings → Whisp. Owning the
// settings here instead breaks the cycle: pages from Deckle.Whisp talk
// to WhispSettingsService.Instance directly.
//
// The legacy <UserDataRoot>/settings.json file used to carry a `whisp`
// section. SettingsBootstrap.MigrateLegacyToPerModule() (called at App
// startup, before any module SettingsService is touched) extracts that
// section into the new file the first time the new build runs, so
// existing user customizations carry over silently.
public sealed class WhispSettingsService
{
    private static readonly Lazy<WhispSettingsService> _instance = new(() => new WhispSettingsService());
    public static WhispSettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<WhispSettings> _store;

    public WhispSettings Current => _store.Current;

    /// <summary>The on-disk JSON file backing this service. Diagnostic only.</summary>
    public string Path => _store.Path;

    /// <summary>Raised after a successful disk write.</summary>
    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private WhispSettingsService()
    {
        string path = System.IO.Path.Combine(
            AppPaths.UserDataRoot, "modules", "whisp", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        _store = new JsonSettingsStore<WhispSettings>(
            path:        path,
            mutexName:   $"{AppPaths.AppFolderName}-Settings-Whisp-Save",
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, $"[whisp] {msg}"),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, $"[whisp] {msg}"),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, $"[whisp] {msg}"),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, $"[whisp] {msg}"));
    }

    /// <summary>Schedule a debounced disk write (300 ms).</summary>
    public void Save() => _store.Save();

    /// <summary>Synchronous flush. Use before process exit / restart.</summary>
    public void Flush() => _store.Flush();

    /// <summary>Re-read from disk and replace the in-memory snapshot.</summary>
    public void Reload() => _store.Reload();

    /// <summary>Replace the in-memory POCO entirely (Reset to defaults).</summary>
    public void Replace(WhispSettings next) => _store.Replace(next);

    // Resolves the directory containing .bin files (Whisper models + VAD
    // Silero). User override wins; otherwise fall back to
    // AppPaths.ModelsDirectory. Layered this way so the user override
    // stays reachable from the Settings UI without leaking the resolution
    // policy into AppPaths.
    public string ResolveModelsDirectory()
    {
        string user = Current.ModelsDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return AppPaths.ModelsDirectory;
    }
}
