using System;
using System.IO;
using System.Linq;

namespace WhispUI;

// ── AppPaths ───────────────────────────────────────────────────────────────
//
// Centralized path resolution for WhispUI.
//
// Single source of truth for where the app reads and writes user data on
// disk. Branches by IsPackaged so the same call sites work in both modes:
//
//   • Packaged MSIX: paths under ApplicationData.Current.LocalFolder,
//     which Windows wipes cleanly on uninstall and exposes per-package
//     under %LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\.
//   • Unpackaged dev: paths next to the exe (config) and walked-up from
//     there (models/, benchmark/telemetry/) — preserves the dev layout
//     that existed before this refactor.
//
// IsPackaged is detected once at first access via Package.Current — the
// documented Microsoft Learn pattern for code that wants to behave
// differently with or without package identity. Memoized for the rest of
// process lifetime; identity cannot change at runtime.
//
// User overrides (PathsSettings.ModelsDirectory,
// TelemetrySettings.StorageDirectory) are intentionally NOT applied here
// — this class returns the *default* roots only. The services that own
// those settings (SettingsService.ResolveModelsDirectory,
// CorpusPaths.GetDirectoryPath) layer their own override logic on top of
// these defaults. Same goes for WHISP_MODEL_PATH which lives in WhispEngine.
public static class AppPaths
{
    // True when the process runs under a packaged identity (MSIX side-load
    // or Microsoft Store). False when running as a plain Win32 exe (dev
    // build, publish ZIP, etc.). Used as a routing flag throughout the
    // app for any decision that differs across the two modes (paths,
    // autostart mechanism, update channel, etc.).
    public static bool IsPackaged { get; }

    // Where settings.json lives. Always non-null and the directory is
    // created on first access — safe to write to without a separate
    // existence check.
    public static string ConfigDirectory { get; }

    // Default location for Whisper .bin and Silero VAD .bin files.
    // Callers that support a user override (SettingsService) consult
    // their own setting first and fall back to this.
    public static string ModelsDirectory { get; }

    // Default root for app.jsonl, latency.jsonl, microphone.jsonl, and
    // per-profile corpus folders. May be null in dev when no benchmark/
    // sibling exists in the walk-up — telemetry persistence is then
    // disabled silently (matches the pre-refactor behaviour). Always
    // non-null in packaged mode (LocalFolder is guaranteed to exist).
    public static string? TelemetryDirectory { get; }

    static AppPaths()
    {
        IsPackaged = DetectPackaged();

        if (IsPackaged)
        {
            // ApplicationData.Current.LocalFolder is per-package, owned
            // by the user's profile, and removed on package uninstall.
            // Standard Windows location for any app data that should not
            // roam (large files, caches, models).
            string localState = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            ConfigDirectory     = Path.Combine(localState, "config");
            ModelsDirectory     = Path.Combine(localState, "models");
            TelemetryDirectory  = Path.Combine(localState, "telemetry");

            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(TelemetryDirectory);
        }
        else
        {
            string baseDir = AppContext.BaseDirectory;
            ConfigDirectory    = Path.Combine(baseDir, "config");
            ModelsDirectory    = ResolveDevModelsDirectory(baseDir);
            TelemetryDirectory = ResolveDevTelemetryDirectory(baseDir);

            // Only ConfigDirectory is guaranteed creatable here. Models
            // and Telemetry are passive lookups in dev — the walk-up
            // either finds an existing folder or returns a path that
            // doesn't exist (callers validate before writing).
            Directory.CreateDirectory(ConfigDirectory);
        }
    }

    // Identity check via Package.Current. The CsWinRT projection throws
    // InvalidOperationException ("The process has no package identity")
    // when called from an unpackaged process. Documented Microsoft Learn
    // pattern: catch and return false. Once Windows App SDK exposes a
    // first-class IsPackaged check (Microsoft.Windows.ApplicationModel),
    // this can be swapped without changing the public surface.
    private static bool DetectPackaged()
    {
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current;
            return pkg?.Id is not null;
        }
        catch
        {
            return false;
        }
    }

    // Walk up from the exe directory (max 8 levels) looking for a
    // `models/` folder containing at least one .bin. Covers both:
    //   • publish layout: exe in publish/, models 1-2 levels up
    //   • dev layout: exe in bin/x64/Release/net10.0-*/, models 5-6 up
    // Falls back to <baseDir>/../../models so callers always get a
    // resolvable path even when nothing is found — they validate
    // existence themselves and surface the missing-model case via the
    // Settings UI / first-run wizard.
    private static string ResolveDevModelsDirectory(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.bin").Any())
                return candidate;
        }
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "models"));
    }

    // Walk up looking for a `benchmark/` sibling, then return its
    // `telemetry/` subfolder (creating it on the way so the first write
    // doesn't race). Returns null when no benchmark/ ancestor exists —
    // telemetry persistence is then disabled (pre-refactor behaviour:
    // callers null-check and skip writes silently).
    private static string? ResolveDevTelemetryDirectory(string baseDir)
    {
        try
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir is not null)
            {
                string bench = Path.Combine(dir.FullName, "benchmark");
                if (Directory.Exists(bench))
                {
                    string telemetry = Path.Combine(bench, "telemetry");
                    Directory.CreateDirectory(telemetry);
                    return telemetry;
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Filesystem error during walk-up — treat as "not found",
            // matches the pre-refactor swallow behaviour in CorpusPaths.
        }
        return null;
    }
}
