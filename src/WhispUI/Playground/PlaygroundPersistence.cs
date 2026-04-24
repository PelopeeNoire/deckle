using System;
using System.IO;
using System.Text.Json;

namespace WhispUI.Playground;

// ─── Playground tuning persistence ──────────────────────────────────────────
//
// JSON-backed store for the single custom profile the playground exposes.
//
// Model: one user profile at most. Shipping defaults live in the
// TuningModel field initialisers (+ the sim* defaults on PlaygroundWindow
// itself) — if the JSON file doesn't exist or fails to parse, we fall back
// to those defaults, which is the "default profile" in the UX sense.
// When the user hits Save, the current TuningModel + sim state are dumped
// to the file; next launch rehydrates from it.
//
// Location: %LocalAppData%\WhispUI\playground-tunings.json. LocalAppData
// (not Roaming) because the tuning is machine-specific — DPI, colour
// calibration, refresh rate all influence what reads well. This matches
// Windows 11 Settings' own per-machine persistence convention.
//
// Multi-profile is explicitly out of scope for v1: the file holds one
// profile, selection in the UI is implicit (default vs. modified). If
// Louis later wants named profiles + a dropdown, the natural extension is
// a `Profiles` dictionary keyed by name, with one entry always named
// "Default" and reset to shipping values. Deferred until the single-slot
// flow has been lived with.

internal static class PlaygroundPersistence
{
    // Bundle of everything the playground UI can mutate. A record /
    // mutable class (not the shipping `ConicArcStrokeConfig` struct)
    // because the JSON round-trip is bidirectional and struct init-only
    // properties don't deserialize cleanly.
    internal sealed class Profile
    {
        // TuningModel's fields are `public` (not properties) — see the
        // doc on that class for why. JsonSerializer.IncludeFields below
        // is what makes those round-trip.
        public TuningModel Tuning { get; set; } = new();

        // Simulation knobs — live on PlaygroundWindow directly and don't
        // belong in TuningModel (they have no counterpart in the shipping
        // ConicArcStrokeConfig).
        public float SimRmsMin             { get; set; } = 0.013f;
        public float SimRmsMax             { get; set; } = 0.100f;
        public float SimRmsPeriodSeconds   { get; set; } = 2.0f;
        public bool  SimManualOverride     { get; set; } = false;
        public float SimManualValue        { get; set; } = 0.012f;
        public bool  SimulateChangedDigits { get; set; } = true;
    }

    // IncludeFields is required: TuningModel exposes state as public
    // fields (not properties) so we can grep the file for one name and
    // land on the single source of truth. Without IncludeFields the
    // TuningModel round-trip silently produces all defaults.
    //
    // WriteIndented for readability — Louis wants to be able to open the
    // JSON and copy values into TuningModel's initialisers by hand when
    // he decides his current tuning is the new default.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented               = true,
        IncludeFields               = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "WhispUI", "playground-tunings.json");
        }
    }

    // Returns null if the file doesn't exist or can't be parsed — callers
    // fall back to TuningModel's compiled-in defaults silently. A corrupt
    // file is treated identically to "no file": the next Save will
    // overwrite it cleanly.
    public static Profile? TryLoad()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Profile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Returns true on successful write. Only failure paths are disk
    // pressure / perms / antivirus holding the file open — rare on a
    // single-user box. Caller decides whether to surface the failure.
    public static bool TrySave(Profile profile)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Delete-if-exists. Called by "Reset all" — when the user wipes the
    // playground back to shipping defaults we also drop the on-disk copy,
    // so the next launch truly starts pristine (not "defaults loaded from
    // a default-valued file"). Absent file is treated as success.
    public static bool TryDelete()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
