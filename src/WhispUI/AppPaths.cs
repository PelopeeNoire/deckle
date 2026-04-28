using System;
using System.IO;
using System.Linq;

namespace WhispUI;

// ── AppPaths ───────────────────────────────────────────────────────────────
//
// Centralized path resolution. Single source of truth for where the app
// reads and writes user data on disk. All mutable per-user state lives
// under <UserDataRoot>:
//
//   • settings/   — settings.json + backups
//   • telemetry/  — JSONL files (app, latency, microphone) + per-profile corpus
//   • models/     — Whisper ggml-*.bin
//   • native/     — libwhisper.dll, ggml*.dll
//   • benchmark/  — optional, installed on demand from Settings
//
// Default <UserDataRoot> = %LOCALAPPDATA%\<AppFolderName>\, the canonical
// per-user data root on Windows (Settings Win11, PowerToys, every
// first-party Microsoft desktop app). Override with the WHISP_DATA_ROOT
// env var to keep %LOCALAPPDATA% clean during development.
//
// The application binary itself stays read-only and Program Files-friendly:
// it ships with Assets but no models, no native DLLs, no config. The
// first-run wizard populates <UserDataRoot>\models\ and \native\ on the
// first launch (see Shell/WelcomeWizardWindow).
public static class AppPaths
{
    // Filesystem-safe folder name. Single source of truth for filesystem
    // paths and the inter-process settings mutex. Swapped to the final
    // user-facing brand in Lot C; until then the working title doubles as
    // the folder name.
    public const string AppFolderName = "WhispUI";

    // Inter-process mutex name used by SettingsService to serialize writes
    // across concurrent app instances. Derived from AppFolderName so the
    // single rename in Lot C carries through.
    public const string SettingsMutexName = $"{AppFolderName}-Settings-Save";

    // Override env var. Pointed at a freshly-organized dev folder so
    // user data ends up there instead of polluting %LOCALAPPDATA%.
    // Empty/unset → default location.
    public const string DataRootEnvVar = "WHISP_DATA_ROOT";

    public static string UserDataRoot       { get; }
    public static string SettingsDirectory  { get; }
    public static string TelemetryDirectory { get; }
    public static string ModelsDirectory    { get; }
    public static string NativeDirectory    { get; }
    public static string BenchmarkDirectory { get; }

    static AppPaths()
    {
        UserDataRoot       = ResolveUserDataRoot();
        SettingsDirectory  = Path.Combine(UserDataRoot, "settings");
        TelemetryDirectory = Path.Combine(UserDataRoot, "telemetry");
        ModelsDirectory    = ResolveModelsDirectory();
        NativeDirectory    = Path.Combine(UserDataRoot, "native");
        BenchmarkDirectory = Path.Combine(UserDataRoot, "benchmark");

        // Settings + telemetry are the two dirs the app writes to during
        // normal operation — created eagerly so call sites don't need
        // existence checks. Models, native, and benchmark are populated
        // by the wizard or the user; creating them empty here would mask
        // the "missing dependencies" detection done by Setup/NativeRuntime
        // and (later) Setup/SpeechModels.
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(TelemetryDirectory);
    }

    // <UserDataRoot> resolution order:
    //   1. WHISP_DATA_ROOT env var (dev override)
    //   2. %LOCALAPPDATA%\<AppFolderName>\        ← canonical Windows location
    //   3. <exeDir>\<AppFolderName>\              ← portable fallback
    //
    // Step 3 covers sandboxed runs where LOCALAPPDATA isn't available
    // (rare, but a USB-stick portable mode is a plausible future use).
    private static string ResolveUserDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(DataRootEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, AppFolderName);

        return Path.Combine(AppContext.BaseDirectory, AppFolderName);
    }

    // ModelsDirectory resolution:
    //   1. Canonical: <UserDataRoot>\models\ if it holds at least one .bin
    //      (= the user — or the future wizard — has populated it).
    //   2. Dev fallback: walk up from the exe (max 8 levels) looking for a
    //      `models/` folder with .bin files. Lets a fresh dev build run
    //      against the in-repo `models/` without copying anything to
    //      <UserDataRoot> first.
    //   3. Default: the canonical path even when empty, so the wizard has
    //      somewhere consistent to write into and Setup/SpeechModels can
    //      surface the missing state.
    //
    // TODO (wizard): drop the dev fallback once the first-run wizard
    // populates <UserDataRoot>\models\ from a known source on first launch.
    private static string ResolveModelsDirectory()
    {
        string canonical = Path.Combine(UserDataRoot, "models");

        if (Directory.Exists(canonical) &&
            Directory.EnumerateFiles(canonical, "*.bin").Any())
            return canonical;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.bin").Any())
                return candidate;
        }

        return canonical;
    }
}
