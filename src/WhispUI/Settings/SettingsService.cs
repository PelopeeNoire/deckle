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

        _current = Load();
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // Charge depuis le disque. En cas d'absence, d'erreur JSON ou de fichier
    // tronqué, retourne des défauts et réécrit un fichier propre.
    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaults = new AppSettings();
                File.WriteAllText(_configPath, JsonSerializer.Serialize(defaults, _jsonOptions));
                return defaults;
            }

            string json = File.ReadAllText(_configPath);

            // One-shot migration: legacy "manualProfileName" → "slotAProfileName"
            // (hotkey slots V1, 2026-04-15). Applied before strict deserialization
            // so the legacy key is consumed even if AppSettings no longer carries
            // it. Next Save() rewrites the file without the old key.
            json = MigrateLegacyKeys(json);

            var parsed = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            return parsed ?? new AppSettings();
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"load failed ({ex.GetType().Name}: {ex.Message}) — fallback defaults");
            return new AppSettings();
        }
    }

    // One-shot rename of legacy JSON keys before strict deserialization.
    // Non-destructive: if the legacy key is missing or the new key is already
    // present, returns the input unchanged.
    private static string MigrateLegacyKeys(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;

            if (root["llm"] is JsonObject llm &&
                llm["manualProfileName"] is JsonNode legacy &&
                llm["slotAProfileName"] is null)
            {
                llm["slotAProfileName"] = legacy.DeepClone();
                llm.Remove("manualProfileName");
                DebugLog.Write("SETTINGS", "migrated llm.manualProfileName → llm.slotAProfileName");
                return root.ToJsonString();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"migration skipped: {ex.GetType().Name}: {ex.Message}");
        }
        return json;
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

            DebugLog.Write("SETTINGS", "saved to disk");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
