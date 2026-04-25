using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ── SettingsService ───────────────────────────────────────────────────────────
//
// Singleton. Charge AppSettings depuis ./config/settings.json au démarrage,
// expose l'instance courante, et écrit le fichier sur demande avec un léger
// debounce (300 ms) pour ne pas réécrire à chaque tick de slider.
//
// Emplacement volontairement DANS le dossier de l'application (à côté de
// l'exe via AppContext.BaseDirectory) et non dans %LOCALAPPDATA% — choix assumé
// pour cette version unpackaged. Si un jour l'app est packagée MSIX (Store),
// il faudra basculer vers ApplicationData.Current.LocalFolder (Program Files
// devient read-only en sandbox Store).
//
// Thread-safety : toutes les mutations de Current doivent passer par Save().
// Les lecteurs (WhispEngine côté thread de transcription) prennent une
// référence locale à Current au début de Transcribe() — AppSettings est un
// graphe de POCO, le snapshot est suffisant.
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configPath;
    private readonly object _lock = new();
    private readonly Timer _debounceTimer;
    private AppSettings _current;

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    // Levé après une écriture disque réussie. Les consommateurs (UI, engine)
    // peuvent s'abonner pour réagir à un changement externe au fichier ou à
    // un Save explicite. L'engine, lui, re-lit Current au début de chaque
    // Transcribe(), donc il n'a pas besoin de s'abonner pour les params
    // hot-reload — seulement pour les réglages lourds (reload modèle).
    public event Action? Changed;

    private SettingsService()
    {
        string baseDir = AppContext.BaseDirectory;
        string configDir = Path.Combine(baseDir, "config");
        _configPath = Path.Combine(configDir, "settings.json");

        Directory.CreateDirectory(configDir);

        _current = Load(out bool migrated);
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        // If the on-disk file carried legacy keys, rewrite it now so the
        // obsolete entries are gone after the first launch — no need to wait
        // for the user to mutate a setting in the UI.
        if (migrated) Flush();
    }

    // Charge depuis le disque. En cas d'absence, d'erreur JSON ou de fichier
    // tronqué, retourne des défauts et réécrit un fichier propre.
    // `migrated` out: true when the on-disk JSON carried legacy keys that
    // were rewritten in memory — the caller flushes to persist the cleanup.
    private AppSettings Load(out bool migrated)
    {
        migrated = false;
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaults = new AppSettings();
                MigrateProfileIds(defaults);
                File.WriteAllText(_configPath, JsonSerializer.Serialize(defaults, _jsonOptions));
                LogService.Instance.Info(LogSource.Settings,
                    $"load complete | source=defaults | path={_configPath} | reason=file_missing");
                return defaults;
            }

            string json = File.ReadAllText(_configPath);

            // One-shot migrations applied before strict deserialization so legacy
            // keys are consumed even though AppSettings no longer carries them.
            // The caller flushes right after if `migrated` is true so the file
            // on disk ends up without the obsolete keys.
            //   • manualProfileName         → slotAProfileName            (V1 slots, 2026-04-15)
            //   • slotAProfileName          → primaryRewriteProfileName   (primary/secondary rename, 2026-04-16)
            //   • slotBProfileName          → secondaryRewriteProfileName (primary/secondary rename, 2026-04-16)
            (json, migrated) = MigrateLegacyKeys(json);

            var parsed = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            if (MigrateProfileIds(parsed)) migrated = true;
            LogService.Instance.Info(LogSource.Settings,
                $"load complete | source=disk | path={_configPath} | bytes={json.Length} | migrated={migrated}");
            return parsed;
        }
        catch (Exception ex)
        {
            // Parse failure falls back to defaults — the app still runs, so
            // this is a Warning rather than an Error. Includes the path +
            // exception type so the broken file is easy to locate.
            LogService.Instance.Warning(LogSource.Settings,
                $"parse failed, fallback to defaults | path={_configPath} | error={ex.GetType().Name}: {ex.Message}");
            return new AppSettings();
        }
    }

    // One-shot rename of legacy JSON keys before strict deserialization.
    // Non-destructive: for each rename, if the legacy key is missing or the
    // new key is already present, that rename is skipped. Returns the input
    // unchanged (and migrated=false) when no mutation applied.
    private static (string json, bool migrated) MigrateLegacyKeys(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return (json, false);

            bool mutated = false;

            if (root["llm"] is JsonObject llm)
            {
                if (llm["manualProfileName"] is JsonNode legacyManual &&
                    llm["slotAProfileName"] is null)
                {
                    llm["slotAProfileName"] = legacyManual.DeepClone();
                    llm.Remove("manualProfileName");
                    LogService.Instance.Info(LogSource.Settings, "migrated llm.manualProfileName → llm.slotAProfileName");
                    mutated = true;
                }

                if (llm["slotAProfileName"] is JsonNode legacySlotA &&
                    llm["primaryRewriteProfileName"] is null)
                {
                    llm["primaryRewriteProfileName"] = legacySlotA.DeepClone();
                    llm.Remove("slotAProfileName");
                    LogService.Instance.Info(LogSource.Settings, "migrated llm.slotAProfileName → llm.primaryRewriteProfileName");
                    mutated = true;
                }

                if (llm["slotBProfileName"] is JsonNode legacySlotB &&
                    llm["secondaryRewriteProfileName"] is null)
                {
                    llm["secondaryRewriteProfileName"] = legacySlotB.DeepClone();
                    llm.Remove("slotBProfileName");
                    LogService.Instance.Info(LogSource.Settings, "migrated llm.slotBProfileName → llm.secondaryRewriteProfileName");
                    mutated = true;
                }
            }

            // corpusLogging → telemetry (telemetry unification, 2026-04-21).
            // `enabled` becomes `corpusEnabled` (the corpus is one of two opt-in
            // streams now; the other is `latencyEnabled`, which defaults to off
            // and stays off through this migration). `dataDirectory` becomes
            // `storageDirectory`, reused as the common root for all three files.
            if (root["corpusLogging"] is JsonObject legacyCorpus &&
                root["telemetry"] is null)
            {
                var telemetry = new JsonObject
                {
                    ["latencyEnabled"]    = false,
                    ["corpusEnabled"]     = legacyCorpus["enabled"]?.GetValue<bool>() ?? false,
                    ["recordAudioCorpus"] = legacyCorpus["recordAudioCorpus"]?.GetValue<bool>() ?? false,
                    ["storageDirectory"]  = legacyCorpus["dataDirectory"]?.GetValue<string>() ?? "",
                };
                root["telemetry"] = telemetry;
                root.Remove("corpusLogging");
                LogService.Instance.Info(LogSource.Settings, "migrated corpusLogging → telemetry");
                mutated = true;
            }

            return (mutated ? root.ToJsonString() : json, mutated);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Settings, $"migration skipped: {ex.GetType().Name}: {ex.Message}");
        }
        return (json, false);
    }

    // Fills stable ids where missing: each RewriteProfile gets a 12-char Guid
    // suffix, and AutoRewriteRules / shortcut slots get their ProfileId resolved
    // from ProfileName. Returns true if anything was mutated so the caller can
    // flush the rewritten config to disk on first launch after upgrade.
    // Internal (not private) so page-level resets can re-run the migration
    // against a freshly-instantiated LlmSettings block.
    internal static bool MigrateProfileIds(AppSettings s)
    {
        bool mutated = false;

        foreach (var p in s.Llm.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
            {
                p.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
                mutated = true;
            }
        }

        string? IdForName(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? null
                : s.Llm.Profiles.Find(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

        foreach (var rule in s.Llm.AutoRewriteRules)
        {
            if (!string.IsNullOrEmpty(rule.ProfileId)) continue;
            string? resolved = IdForName(rule.ProfileName);
            if (resolved is not null)
            {
                rule.ProfileId = resolved;
                mutated = true;
            }
        }

        foreach (var rule in s.Llm.AutoRewriteRulesByWords)
        {
            if (!string.IsNullOrEmpty(rule.ProfileId)) continue;
            string? resolved = IdForName(rule.ProfileName);
            if (resolved is not null)
            {
                rule.ProfileId = resolved;
                mutated = true;
            }
        }

        if (s.Llm.PrimaryRewriteProfileId is null)
        {
            string? resolved = IdForName(s.Llm.PrimaryRewriteProfileName);
            if (resolved is not null)
            {
                s.Llm.PrimaryRewriteProfileId = resolved;
                mutated = true;
            }
        }

        if (s.Llm.SecondaryRewriteProfileId is null)
        {
            string? resolved = IdForName(s.Llm.SecondaryRewriteProfileName);
            if (resolved is not null)
            {
                s.Llm.SecondaryRewriteProfileId = resolved;
                mutated = true;
            }
        }

        return mutated;
    }

    // Appelé par l'UI après chaque mutation. Ne bloque pas — debounce 300 ms
    // puis écriture effective sur le thread pool via Flush().
    public void Save()
    {
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    // Resolves the directory containing .bin files (Whisper models + VAD Silero).
    // If the user hasn't configured a custom path, walks up from the exe directory
    // looking for a `models/` folder with at least one .bin. Covers both the
    // publish layout (exe in `publish/`, models 2 levels up) and the dev layout
    // (exe in `bin/x64/Release/net10.0-*/`, models 6 levels up).
    public string ResolveModelsDirectory()
    {
        string user = Current.Paths.ModelsDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.bin").Any())
                return candidate;
        }

        // Nothing found: fall back to legacy path (may not exist — the scanner
        // will show an empty list, and the user will need to configure manually).
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "models"));
    }

    // Public pour permettre un flush synchrone avant un arrêt du process
    // (RestartApp, QuitApp) — le debounce timer ne survivrait pas à Environment.Exit.
    public void Flush()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_current, _jsonOptions);
            }

            // Écriture atomique : fichier temporaire puis Move. Évite un
            // settings.json tronqué si le process est tué pendant l'écriture.
            string tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _configPath, overwrite: true);

            LogService.Instance.Verbose(LogSource.Settings, "saved to disk");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(LogSource.Settings, $"save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
