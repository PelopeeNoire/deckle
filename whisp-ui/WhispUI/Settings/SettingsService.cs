using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using WhispUI;

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
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            return parsed ?? new AppSettings();
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"load failed ({ex.GetType().Name}: {ex.Message}) — fallback defaults");
            return new AppSettings();
        }
    }

    // Appelé par l'UI après chaque mutation. Ne bloque pas — debounce 300 ms
    // puis écriture effective sur le thread pool via Flush().
    public void Save()
    {
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    // Résout le dossier où chercher les .bin (modèles Whisper + VAD Silero).
    // Si l'utilisateur n'a rien défini (défaut), on retombe sur la disposition
    // historique du dépôt : `../../shared` relatif à l'exe. Centralisé ici pour
    // qu'un seul endroit connaisse la convention de fallback.
    public string ResolveModelsDirectory()
    {
        string user = Current.Paths.ModelsDirectory;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shared"));
    }

    private void Flush()
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
