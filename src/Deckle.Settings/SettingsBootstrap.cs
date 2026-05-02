using System.Text.Json;
using System.Text.Json.Nodes;
using Deckle.Core;
using Deckle.Logging;

namespace Deckle.Settings;

// ── SettingsBootstrap ─────────────────────────────────────────────────────
//
// One-shot migration step called at App startup, before any module
// SettingsService is constructed. Detects the legacy combined
// settings.json layout and dispatches each module section into its own
// modules/<id>/settings.json file.
//
// Why a separate static rather than a hook inside SettingsService.Load?
// Because the migration must complete before any module's
// JsonSettingsStore opens its file: the module store would otherwise
// see no file → write defaults → and on the next launch we'd have
// already-defaulted module files that the legacy dispatch would skip
// (the file exists). Doing the dispatch in App.OnLaunched as the very
// first step keeps the ordering bullet-proof: the legacy file is
// drained and the module files are populated before any module
// singleton has a chance to open them.
//
// The migration is idempotent: on subsequent launches the legacy file
// no longer carries module sections (they were stripped on first run)
// so DispatchSection finds nothing and returns early.
//
// Only writes module files when they do NOT exist. If a user already
// has a populated modules/<id>/settings.json from a previous launch,
// the legacy section is dropped without overwriting (preserves the
// per-module file as source of truth).
public static class SettingsBootstrap
{
    private static bool _migrated;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Runs the legacy → per-module dispatch once per process. Idempotent
    /// across launches via a check on each module's target file.
    /// </summary>
    public static void MigrateLegacyToPerModule()
    {
        if (_migrated) return;
        _migrated = true;

        string legacyPath = AppPaths.SettingsFilePath;
        if (!File.Exists(legacyPath))
        {
            // Fresh install — no legacy file to drain. Per-module files
            // get created with defaults by their respective services.
            return;
        }

        try
        {
            string json = File.ReadAllText(legacyPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return;

            bool mutated = false;

            // Pre-dispatch root-level renames so the dispatcher finds the
            // current key names. Both renames are old (April–May 2026) so
            // most users won't carry the legacy keys, but the migration
            // stays for safety.
            mutated |= RenameRootKey(root, "recording", "capture");
            mutated |= MigrateCorpusLoggingToTelemetry(root);

            // Pre-dispatch llm-internal renames so the dispatched llm file
            // already carries the canonical key names.
            if (root["llm"] is JsonObject llm)
                mutated |= MigrateLlmLegacyKeys(llm);

            // Each module's legacy section dispatched to modules/<id>/settings.json.
            mutated |= DispatchSection(root, "whisp",     "whisp");
            mutated |= DispatchSection(root, "llm",       "llm");
            mutated |= DispatchSection(root, "capture",   "capture");
            mutated |= DispatchSection(root, "telemetry", "telemetry");

            // ModelsDirectory: the legacy paths.modelsDirectory key migrated
            // to whispSettings.modelsDirectory in 2026-05-02 (it's a
            // Whisper-engine concern). Read the legacy value, inject it
            // into the dispatched whisp file (which we just wrote above
            // OR which already existed from a partial migration), then
            // strip the key from paths.
            if (root["paths"] is JsonObject paths
                && paths["modelsDirectory"] is JsonValue modelsDirNode
                && modelsDirNode.TryGetValue<string>(out string? modelsDir)
                && !string.IsNullOrEmpty(modelsDir))
            {
                InjectIntoModuleFile("whisp", "modelsDirectory", JsonValue.Create(modelsDir));
                paths.Remove("modelsDirectory");
                LogService.Instance.Info(LogSource.Settings,
                    "migrated paths.modelsDirectory → modules/whisp/modelsDirectory");
                mutated = true;
            }
            else if (root["paths"] is JsonObject pathsB
                     && pathsB["modelsDirectory"] is not null)
            {
                // The key exists but is empty — just strip it.
                pathsB.Remove("modelsDirectory");
                mutated = true;
            }

            if (mutated)
            {
                File.WriteAllText(legacyPath, root.ToJsonString(_jsonOptions));
                LogService.Instance.Info(LogSource.Settings,
                    "Settings split into per-module files");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(LogSource.Settings,
                $"per-module migration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Move root[jsonKey] into <UserDataRoot>/modules/<moduleId>/settings.json.
    // Returns true if the legacy key was present (mutates root either way:
    // strips the key whether or not the target file already existed).
    private static bool DispatchSection(JsonObject root, string jsonKey, string moduleId)
    {
        if (root[jsonKey] is not JsonNode section) return false;

        string targetPath = ModuleSettingsFilePath(moduleId);

        if (File.Exists(targetPath))
        {
            // Target already populated by a previous launch (or by a partial
            // migration that crashed midway). Don't overwrite — the per-
            // module file is canonical now. Just strip the legacy key.
            root.Remove(jsonKey);
            LogService.Instance.Verbose(LogSource.Settings,
                $"dispatch {jsonKey} skipped (target exists) | path={targetPath}");
            return true;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, section.ToJsonString(_jsonOptions));
        root.Remove(jsonKey);
        LogService.Instance.Info(LogSource.Settings,
            $"migrated {jsonKey} → modules/{moduleId}/settings.json");
        return true;
    }

    // Inject `value` under `key` into modules/<moduleId>/settings.json.
    // Used by the ModelsDirectory migration: at this point the dispatch
    // step above has either just created the whisp file from the legacy
    // section (so we add the new key), or the file pre-existed (we still
    // add the key if it's missing — preserves existing module data).
    private static void InjectIntoModuleFile(string moduleId, string key, JsonNode value)
    {
        string targetPath = ModuleSettingsFilePath(moduleId);
        try
        {
            JsonObject moduleRoot;
            if (File.Exists(targetPath))
            {
                string moduleJson = File.ReadAllText(targetPath);
                moduleRoot = JsonNode.Parse(moduleJson) as JsonObject ?? new JsonObject();
            }
            else
            {
                moduleRoot = new JsonObject();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
            }

            if (moduleRoot[key] is null)
            {
                moduleRoot[key] = value.DeepClone();
                File.WriteAllText(targetPath, moduleRoot.ToJsonString(_jsonOptions));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Settings,
                $"inject {key} into {moduleId} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ModuleSettingsFilePath(string moduleId) =>
        System.IO.Path.Combine(AppPaths.UserDataRoot, "modules", moduleId, "settings.json");

    // recording → capture (capture module extraction, 2026-05-02). The
    // settings shape stayed identical; only the JSON key changed when
    // RecordingSettings moved from Deckle.Whisp to Deckle.Capture and was
    // renamed CaptureSettings to align with its owning module. Silently
    // rebind the legacy key so existing settings.json files keep their
    // custom AudioInputDeviceId / LevelWindow values.
    private static bool RenameRootKey(JsonObject root, string oldKey, string newKey)
    {
        if (root[oldKey] is not JsonNode legacy || root[newKey] is not null) return false;
        root[newKey] = legacy.DeepClone();
        root.Remove(oldKey);
        LogService.Instance.Info(LogSource.Settings, $"migrated {oldKey} → {newKey}");
        return true;
    }

    // corpusLogging → telemetry (telemetry unification, 2026-04-21).
    // Field shape transforms: `enabled` becomes `corpusEnabled` (the
    // corpus is one of two opt-in streams now; the other is
    // `latencyEnabled`, which defaults to off and stays off through this
    // migration). `dataDirectory` becomes `storageDirectory`, reused as
    // the common root for all three files.
    private static bool MigrateCorpusLoggingToTelemetry(JsonObject root)
    {
        if (root["corpusLogging"] is not JsonObject legacyCorpus
            || root["telemetry"] is not null)
            return false;

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
        return true;
    }

    // Llm-internal legacy key renames. All three are old (April 2026) so
    // most users won't carry the legacy keys, but they stay for safety.
    //   • manualProfileName  → slotAProfileName            (V1 slots)
    //   • slotAProfileName   → primaryRewriteProfileName   (primary/secondary rename)
    //   • slotBProfileName   → secondaryRewriteProfileName (primary/secondary rename)
    private static bool MigrateLlmLegacyKeys(JsonObject llm)
    {
        bool mutated = false;

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

        return mutated;
    }
}
