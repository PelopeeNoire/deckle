using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Settings;

// ── SettingsService ───────────────────────────────────────────────────────────
//
// Singleton owner of the AppSettings POCO. Persists to settings.json under
// AppPaths.SettingsFilePath (= <UserDataRoot>/settings.json).
//
// Disk discipline (load/save/debounce/atomic write/inter-process mutex)
// is delegated to JsonSettingsStore<AppSettings> living in Deckle.Core.
// What stays here is the Settings-specific glue: which migrations apply,
// how to resolve user-overridable paths (ResolveModelsDirectory /
// ResolveBackupDirectory), and the Reload entry point used by the
// SettingsBackupService restore flow.
//
// Why the split? The same disk discipline applies module-by-module once
// per-module persistence lands (Slice C2b). Keeping the JSON store
// generic means each module's SettingsService can reuse the same
// primitive without duplicating the debounce / mutex / atomic-write code.
//
// Thread-safety: see JsonSettingsStore<T>.
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonSettingsStore<AppSettings> _store;

    public AppSettings Current => _store.Current;

    // Exposed read-only so SettingsBackupService can locate the live file
    // without re-resolving AppPaths.SettingsFilePath. Internal callers
    // only — this is not part of any settings-changing surface.
    internal string ConfigPath => _store.Path;

    /// <summary>
    /// Raised after a successful disk write. UI consumers subscribe to
    /// refresh bound state when settings.json changes externally
    /// (Restore from backup) or on an explicit Save flush.
    /// </summary>
    public event Action? Changed
    {
        add    => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private SettingsService()
    {
        // UserDataRoot is created by AppPaths static ctor; redundant
        // CreateDirectory left out on purpose so this constructor reads
        // as "AppPaths owns the location, we just consume it".
        _store = new JsonSettingsStore<AppSettings>(
            path:        AppPaths.SettingsFilePath,
            mutexName:   AppPaths.SettingsMutexName,
            jsonOptions: _jsonOptions,
            logInfo:     msg => LogService.Instance.Info(LogSource.Settings, msg),
            logVerbose:  msg => LogService.Instance.Verbose(LogSource.Settings, msg),
            logWarning:  msg => LogService.Instance.Warning(LogSource.Settings, msg),
            logError:    msg => LogService.Instance.Error(LogSource.Settings, msg),
            preDeserializeMigration: MigrateLegacyKeys,
            postLoadMigration:       MigrateProfileIds);
    }

    /// <summary>Schedule a debounced disk write (300 ms).</summary>
    public void Save() => _store.Save();

    /// <summary>Synchronous flush. Use before process exit / restart.</summary>
    public void Flush() => _store.Flush();

    /// <summary>Re-read from disk and replace the in-memory snapshot.</summary>
    public void Reload() => _store.Reload();

    // Resolves the directory containing .bin files (Whisper models + VAD
    // Silero). If the user has configured a custom path, that wins.
    // Otherwise delegates to AppPaths.ModelsDirectory (= <UserDataRoot>\models\).
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

        return AppPaths.SettingsBackupDirectory;
    }

    // ── One-shot key renames ──────────────────────────────────────────────
    //
    // Applied by JsonSettingsStore before strict deserialization so legacy
    // keys are consumed even though AppSettings no longer carries them.
    // The store flushes right after if migrated=true so the file on disk
    // ends up without the obsolete keys.
    //   • manualProfileName         → slotAProfileName            (V1 slots, 2026-04-15)
    //   • slotAProfileName          → primaryRewriteProfileName   (primary/secondary rename, 2026-04-16)
    //   • slotBProfileName          → secondaryRewriteProfileName (primary/secondary rename, 2026-04-16)
    //   • recording                 → capture                     (Capture module extraction, 2026-05-02)
    //   • corpusLogging             → telemetry                   (telemetry unification, 2026-04-21)
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

            // recording → capture (capture module extraction, 2026-05-02).
            // The settings shape stayed identical; only the JSON key changed
            // when RecordingSettings moved from Deckle.Whisp to Deckle.Capture
            // and was renamed CaptureSettings to align with its owning module.
            // Silently rebind the legacy key so existing settings.json files
            // keep their custom AudioInputDeviceId / LevelWindow values.
            if (root["recording"] is JsonNode legacyRecording &&
                root["capture"] is null)
            {
                root["capture"] = legacyRecording.DeepClone();
                root.Remove("recording");
                LogService.Instance.Info(LogSource.Settings, "migrated recording → capture");
                mutated = true;
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

    // ── Profile id reconciliation ────────────────────────────────────────
    //
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
    // Returns true if anything was mutated so the JsonSettingsStore can
    // flush the rewritten config to disk on first launch after upgrade.
    //
    // Public (not private) so page-level resets can re-run the migration
    // against a freshly-instantiated LlmSettings block. Public — not
    // internal — because the Settings pages live in the App assembly while
    // SettingsService now ships in Deckle.Settings; cross-assembly callers
    // need a public surface.
    public static bool MigrateProfileIds(AppSettings s)
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
}
