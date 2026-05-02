using System.Text.Json;

namespace Deckle.Core;

// ── JsonSettingsStore<T> ──────────────────────────────────────────────────
//
// Generic JSON persistence primitive: load on demand, save with debounce,
// flush synchronously, atomic write via temp + Move, and a per-store
// process mutex so two concurrent app instances don't clobber each
// other's writes.
//
// Why a generic helper rather than re-implementing this for each module?
// Each module that owns settings (Whisp, Llm, Capture, Telemetry, the
// Settings shell itself) needs the same disk discipline:
//   • read defaults on a missing or unparseable file (warn, don't crash)
//   • debounce a stream of UI-driven mutations (slider drag at 60 Hz)
//   • survive a process kill mid-write (atomic Move)
//   • coordinate two app instances (mutex per file)
//
// Logging is delegate-injected (logInfo / logVerbose / logWarning /
// logError), not via a hard reference to LogService — so this class
// stays in Deckle.Core (the lowest-level lib, no Logging dependency).
// Each caller passes lambdas that route to LogService with the
// caller's LogSource constant.
//
// Two optional migration hooks let callers patch the on-disk format
// before strict deserialization (preDeserializeMigration: e.g. legacy
// JSON key renames) and rewrite the in-memory POCO after load
// (postLoadMigration: e.g. fill missing ids on first launch). Either
// returning `mutated == true` causes Load to flag the load as migrated
// so the caller can flush the cleanup back to disk.
//
// Thread-safety: Current is read under a lock; mutations to the POCO
// graph itself are the caller's responsibility (the typical pattern is
// to mutate then call Save — the debounce + serialize-under-lock is
// what keeps the on-disk file consistent).
public sealed class JsonSettingsStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly string _mutexName;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logVerbose;
    private readonly Action<string>? _logWarning;
    private readonly Action<string>? _logError;
    private readonly Func<string, (string json, bool migrated)>? _preDeserializeMigration;
    private readonly Func<T, bool>? _postLoadMigration;

    private readonly object _lock = new();
    private readonly System.Threading.Timer _debounceTimer;
    private T _current;

    /// <summary>Snapshot of the in-memory POCO under the store's lock.</summary>
    public T Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>The on-disk JSON file backing this store (resolved at construction).</summary>
    public string Path => _path;

    /// <summary>
    /// Raised after a successful disk write (Save → debounce → Flush, or
    /// Reload). UI consumers subscribe to refresh bound state.
    /// </summary>
    public event Action? Changed;

    public JsonSettingsStore(
        string path,
        string mutexName,
        JsonSerializerOptions jsonOptions,
        Action<string>? logInfo = null,
        Action<string>? logVerbose = null,
        Action<string>? logWarning = null,
        Action<string>? logError = null,
        Func<string, (string json, bool migrated)>? preDeserializeMigration = null,
        Func<T, bool>? postLoadMigration = null)
    {
        _path = path;
        _mutexName = mutexName;
        _jsonOptions = jsonOptions;
        _logInfo = logInfo;
        _logVerbose = logVerbose;
        _logWarning = logWarning;
        _logError = logError;
        _preDeserializeMigration = preDeserializeMigration;
        _postLoadMigration = postLoadMigration;

        _current = Load(out bool migrated);
        _debounceTimer = new System.Threading.Timer(
            _ => Flush(), null,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);

        // If the on-disk file carried legacy keys or required post-load
        // patching, rewrite it now so the obsolete shape is gone after
        // first launch — no need to wait for the user to mutate a setting.
        if (migrated) Flush();
    }

    /// <summary>
    /// Load from disk. Missing file or parse failure falls back to defaults
    /// (also written to disk for the missing case so the next launch sees a
    /// well-formed file). `migrated` is true when the on-disk JSON was
    /// rewritten in memory by either of the migration hooks.
    /// </summary>
    public T Load(out bool migrated)
    {
        migrated = false;
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = new T();
                _postLoadMigration?.Invoke(defaults);
                File.WriteAllText(_path, JsonSerializer.Serialize(defaults, _jsonOptions));
                _logInfo?.Invoke("Settings initialized (defaults)");
                _logVerbose?.Invoke($"load complete | source=defaults | path={_path} | reason=file_missing");
                return defaults;
            }

            string json = File.ReadAllText(_path);

            if (_preDeserializeMigration is not null)
            {
                (json, bool preMigrated) = _preDeserializeMigration(json);
                if (preMigrated) migrated = true;
            }

            var parsed = JsonSerializer.Deserialize<T>(json, _jsonOptions) ?? new T();

            if (_postLoadMigration is not null && _postLoadMigration(parsed))
                migrated = true;

            _logInfo?.Invoke(migrated ? "Settings loaded (migrated)" : "Settings loaded");
            _logVerbose?.Invoke($"load complete | source=disk | path={_path} | bytes={json.Length} | migrated={migrated}");
            return parsed;
        }
        catch (Exception ex)
        {
            // Parse failure falls back to defaults — the app still runs, so
            // this is a Warning rather than an Error. Includes the path +
            // exception type so the broken file is easy to locate.
            _logWarning?.Invoke(
                $"parse failed, fallback to defaults | path={_path} | error={ex.GetType().Name}: {ex.Message}");
            return new T();
        }
    }

    /// <summary>
    /// Schedule a disk write 300 ms from now. Subsequent calls within the
    /// window reset the timer — typical UI pattern (slider drag fires
    /// dozens of mutations per second; we only persist the final value).
    /// </summary>
    public void Save()
    {
        _debounceTimer.Change(300, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// Synchronous write. Used directly when a debounce window is
    /// inappropriate (process exit, restart, restore from backup).
    /// Holds a per-store named mutex so two concurrent app instances
    /// can't write to the same file at the same time.
    /// </summary>
    public void Flush()
    {
        using var processMutex = new System.Threading.Mutex(initiallyOwned: false, _mutexName);
        bool acquired = false;
        try
        {
            try
            {
                // Short timeout: if another instance lingers we log and skip
                // rather than block the UI. The next Save (debounce) retries.
                acquired = processMutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (System.Threading.AbandonedMutexException)
            {
                // The other instance crashed while holding the mutex; we
                // inherited it (WaitOne succeeded despite the exception).
                // Recoverable, but log it.
                acquired = true;
                _logWarning?.Invoke("settings mutex was abandoned (other instance crashed?) — recovering");
            }

            if (!acquired)
            {
                _logWarning?.Invoke("save skipped: another instance holds the settings mutex");
                return;
            }

            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_current, _jsonOptions);
            }

            // Atomic write: temp file then Move. Avoids a truncated JSON
            // on disk if the process is killed mid-write.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);

            _logVerbose?.Invoke("saved to disk");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"save failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (acquired) processMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Re-read from disk and replace the in-memory snapshot. Bypasses the
    /// debounce timer — explicit user actions (Restore from backup) want
    /// the new state visible immediately, not 300 ms later. Any in-flight
    /// Save loses to this reload by design.
    /// </summary>
    public void Reload()
    {
        var fresh = Load(out bool migrated);
        lock (_lock)
        {
            _current = fresh;
        }
        if (migrated) Flush();
        _logInfo?.Invoke("reloaded from disk");
        Changed?.Invoke();
    }
}
