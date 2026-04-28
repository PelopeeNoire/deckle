using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ── SettingsService ───────────────────────────────────────────────────────────
//
// Singleton. Charge AppSettings depuis settings.json au démarrage, expose
// l'instance courante, et écrit le fichier sur demande avec un léger
// debounce (300 ms) pour ne pas réécrire à chaque tick de slider.
//
// Emplacement résolu via AppPaths.SettingsDirectory : sous-dossier
// `settings/` du UserDataRoot (par défaut %LOCALAPPDATA%\<AppFolderName>\,
// override via WHISP_DATA_ROOT en dev). Le binaire reste read-only et
// Program Files-friendly ; la config vit dans le profil utilisateur.
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

    // Exposed read-only so SettingsBackupService can locate the live file
    // without duplicating the AppPaths.SettingsDirectory + "settings.json"
    // resolution. Internal callers only — this is not part of any
    // settings-changing surface.
    internal string ConfigPath => _configPath;

    // Levé après une écriture disque réussie. Les consommateurs (UI, engine)
    // peuvent s'abonner pour réagir à un changement externe au fichier ou à
    // un Save explicite. L'engine, lui, re-lit Current au début de chaque
    // Transcribe(), donc il n'a pas besoin de s'abonner pour les params
    // hot-reload — seulement pour les réglages lourds (reload modèle).
    public event Action? Changed;

    private SettingsService()
    {
        // SettingsDirectory is created by AppPaths static ctor; redundant
        // CreateDirectory left out on purpose so this constructor reads
        // as "AppPaths owns the location, we just consume it".
        _configPath = Path.Combine(AppPaths.SettingsDirectory, "settings.json");

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

    // Reconciles profile references across the LlmSettings graph. Two jobs:
    //
    //   1. Fill missing stable ids — each RewriteProfile gets a 12-char Guid
    //      suffix on first encounter (legacy configs, freshly-instantiated
    //      defaults).
    //   2. Re-pair ProfileId/ProfileName on rules and slots when the live
    //      Profiles list still contains a match. Three legitimate cases:
    //        - id resolves → sync the cached name in case the profile was
    //          renamed since the rule was last saved
    //        - id is empty but name resolves → fill id from name (post-
    //          migration of an older config that never had ids)
    //        - id is stale but name resolves → rewire id from name
    //
    // **Never deletes a rule and never clears a slot.** Orphan references
    // (id+name both unresolvable) are left untouched: the UI surfaces them
    // as a blank ComboBox SelectedItem, and the user picks a replacement
    // or deletes the rule manually. This is intentional — Reset Rules with
    // no Profiles in the list still shows three placeholder rules to fill
    // in, which would silently disappear if we swept orphans here.
    //
    // The delete-cascade for "remove a profile, drop its dependants" lives
    // in LlmProfilesSection.DeleteProfile_Click, which clears references
    // explicitly **before** the profile is removed.
    //
    // Returns true if anything was mutated so the caller can flush the
    // rewritten config to disk on first launch after upgrade.
    //
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

        string? NameForId(string? id) =>
            string.IsNullOrEmpty(id)
                ? null
                : s.Llm.Profiles.Find(p => p.Id == id)?.Name;

        // Re-pair (id, name) against the live Profiles list. Mutates the
        // ref arguments only when a live profile matches; leaves them
        // untouched (orphan kept as-is) otherwise. Returns true if anything
        // changed so the caller can flag the parent as mutated.
        bool RepairPair(ref string id, ref string name)
        {
            string? nameFromId = NameForId(id);
            if (nameFromId is not null)
            {
                if (name == nameFromId) return false;
                name = nameFromId;
                return true;
            }

            string? idFromName = IdForName(name);
            if (idFromName is not null)
            {
                if (id == idFromName) return false;
                id = idFromName;
                return true;
            }

            // Neither resolves — orphan, leave both alone for the UI to
            // surface and the user to fix.
            return false;
        }

        foreach (var rule in s.Llm.AutoRewriteRules)
        {
            string id = rule.ProfileId ?? "";
            string name = rule.ProfileName ?? "";
            if (RepairPair(ref id, ref name))
            {
                rule.ProfileId = id;
                rule.ProfileName = name;
                mutated = true;
            }
        }

        foreach (var rule in s.Llm.AutoRewriteRulesByWords)
        {
            string id = rule.ProfileId ?? "";
            string name = rule.ProfileName ?? "";
            if (RepairPair(ref id, ref name))
            {
                rule.ProfileId = id;
                rule.ProfileName = name;
                mutated = true;
            }
        }

        // Slots: same re-pair logic, but the storage uses nullable strings
        // (null = "(None)"). A repair flips empty strings back to null so
        // the JSON stays clean, and an orphan slot is left as-is — the user
        // sees the stale name in the ComboBox and reassigns or clears it.
        bool RepairSlot(ref string? id, ref string? name)
        {
            string idVal = id ?? "";
            string nameVal = name ?? "";
            // Nothing set: nothing to do.
            if (idVal.Length == 0 && nameVal.Length == 0) return false;
            if (RepairPair(ref idVal, ref nameVal))
            {
                id = string.IsNullOrEmpty(idVal) ? null : idVal;
                name = string.IsNullOrEmpty(nameVal) ? null : nameVal;
                return true;
            }
            return false;
        }

        string? primaryId = s.Llm.PrimaryRewriteProfileId;
        string? primaryName = s.Llm.PrimaryRewriteProfileName;
        if (RepairSlot(ref primaryId, ref primaryName))
        {
            s.Llm.PrimaryRewriteProfileId = primaryId;
            s.Llm.PrimaryRewriteProfileName = primaryName;
            mutated = true;
        }

        string? secondaryId = s.Llm.SecondaryRewriteProfileId;
        string? secondaryName = s.Llm.SecondaryRewriteProfileName;
        if (RepairSlot(ref secondaryId, ref secondaryName))
        {
            s.Llm.SecondaryRewriteProfileId = secondaryId;
            s.Llm.SecondaryRewriteProfileName = secondaryName;
            mutated = true;
        }

        return mutated;
    }

    // Appelé par l'UI après chaque mutation. Ne bloque pas — debounce 300 ms
    // puis écriture effective sur le thread pool via Flush().
    public void Save()
    {
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    // Resolves the directory containing .bin files (Whisper models + VAD
    // Silero). If the user has configured a custom path, that wins. Otherwise
    // delegates to AppPaths.ModelsDirectory (= <UserDataRoot>\models\).
    // Layered this way so the user override stays reachable from the
    // Settings UI without leaking the resolution policy into AppPaths.
    public string ResolveModelsDirectory()
    {
        string user = Current.Paths.ModelsDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return AppPaths.ModelsDirectory;
    }

    // Resolves the directory where settings backups (snapshots) live. Same
    // layered pattern as ResolveModelsDirectory: user override wins, otherwise
    // a `backups/` folder next to settings.json. Returned path may not exist
    // yet — SettingsBackupService creates it on the first CreateBackup call.
    public string ResolveBackupDirectory()
    {
        string user = Current.Paths.BackupDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return Path.Combine(AppPaths.SettingsDirectory, "backups");
    }

    // Re-reads settings.json from disk and replaces the in-memory snapshot.
    // Used by SettingsBackupService.RestoreFromBackup after it has overwritten
    // the live settings.json with the contents of a snapshot file. Mutates
    // Current under the lock and raises Changed so subscribed UI pages
    // refresh their bound state.
    //
    // Bypasses the debounce timer on purpose: we want the new state visible
    // immediately, not 300 ms later. Any in-flight Save() (a slider mutation
    // happening at the same time) loses to this reload — that's acceptable
    // because Restore is an explicit user action that supersedes any pending
    // edit.
    public void Reload()
    {
        var fresh = Load(out bool migrated);
        lock (_lock)
        {
            _current = fresh;
        }
        if (migrated) Flush();
        LogService.Instance.Info(LogSource.Settings, "reloaded from disk");
        Changed?.Invoke();
    }

    // Mutex inter-process : si une autre instance de l'app tourne en
    // parallèle (double-clic accidentel, login script qui relance), évite
    // les écritures concurrentes sur settings.json qui causeraient une
    // perte de configuration silencieuse (dernier writer gagne sans
    // warning, modifs de l'instance "perdante" effacées). Scope local
    // (per-session Terminal Services) — pas besoin de Global\, settings
    // est per-user. Nom dérivé de AppPaths.AppFolderName pour suivre le
    // rename du Lot C en un seul endroit.

    // Public pour permettre un flush synchrone avant un arrêt du process
    // (RestartApp, QuitApp) — le debounce timer ne survivrait pas à Environment.Exit.
    public void Flush()
    {
        using var processMutex = new Mutex(initiallyOwned: false, AppPaths.SettingsMutexName);
        bool acquired = false;
        try
        {
            try
            {
                // Timeout court : on n'attend pas indéfiniment qu'une autre
                // instance finisse — si elle tarde, on logue et on skip
                // plutôt que de bloquer. La modif sera retentée au prochain
                // Save (debounce), donc pas de perte structurelle.
                acquired = processMutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                // L'autre instance a crashé en tenant le mutex — on l'a
                // hérité (WaitOne a réussi malgré l'exception). État anormal
                // mais récupérable, on continue avec l'écriture.
                acquired = true;
                LogService.Instance.Warning(LogSource.Settings, "settings mutex was abandoned (other WhispUI instance crashed?) — recovering");
            }

            if (!acquired)
            {
                LogService.Instance.Warning(LogSource.Settings, "save skipped: another WhispUI instance holds the settings mutex");
                return;
            }

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
        finally
        {
            if (acquired) processMutex.ReleaseMutex();
        }
    }
}
