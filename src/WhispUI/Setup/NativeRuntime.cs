using System.Collections.Generic;
using System.IO;

namespace WhispUI.Setup;

// ── NativeRuntime ────────────────────────────────────────────────────────────
//
// **Single source of truth** for the whisper.cpp native runtime. This is
// the only module in the app that names libwhisper.dll, ggml-*.dll, and
// the MinGW C++ runtime DLLs the Vulkan backend links against. Anything
// else that needs to detect, install, or copy these files goes through
// the methods exposed here — no parallel knowledge of the catalog.
//
// Concrete consumers right now:
//   • NativeMethods.ResolveNativeLibrary uses EntryDll to build the
//     absolute path it hands to NativeLibrary.TryLoad. Once libwhisper
//     loads, Windows resolves its ggml-*.dll dependencies from the same
//     directory automatically — they don't need to be listed by name on
//     the load side.
//   • The future first-run wizard (Browse... flow + post-install verify)
//     uses RequiredDllNames + IsInstalled + CopyFromFolder.
//
// What's intentionally NOT here:
//   • The "libwhisper" P/Invoke key (the string in [DllImport("libwhisper")]).
//     C# requires a constant literal in DllImport attributes, so every
//     P/Invoke in NativeMethods stays hard-coded with that string. The
//     EntryDll constant below matches it ("libwhisper" + ".dll") so the
//     two stay in sync by convention.
//   • Catalog of speech models (Whisper .bin, Silero VAD). Lives in a
//     future Setup/SpeechModels module — different lifecycle, different
//     trust boundary (download from HuggingFace vs ship with redist).
internal static class NativeRuntime
{
    // Filename used by ResolveNativeLibrary as the entry point. Must match
    // the literal "libwhisper" in [DllImport(...)] attributes plus ".dll".
    public const string EntryDll = "libwhisper.dll";

    // Every DLL the runtime needs to be present in NativeDirectory. The
    // first five are whisper.cpp's Vulkan build output (libwhisper +
    // ggml backends); the last three are the MinGW C++ runtime libraries
    // the Vulkan ggml-vulkan.dll links against. Windows resolves them
    // automatically once they sit alongside libwhisper.dll.
    public static IReadOnlyList<string> RequiredDllNames { get; } = new[]
    {
        EntryDll,
        "ggml.dll",
        "ggml-base.dll",
        "ggml-cpu.dll",
        "ggml-vulkan.dll",
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libwinpthread-1.dll",
    };

    // True when libwhisper.dll is present in NativeDirectory — the
    // canonical install location populated by scripts/setup-assets.ps1
    // (or the future first-run wizard).
    //
    // The full catalog isn't checked file-by-file: if the entry point
    // is there, NativeMethods.SetDllImportResolver loads it and Windows
    // resolves the transitive ggml-*.dll dependencies from the same
    // directory automatically. A missing transitive dep would surface
    // as a DllNotFoundException on the first whisper_* call — clear
    // enough without a redundant catalog sweep here.
    public static bool IsInstalled() =>
        File.Exists(Path.Combine(AppPaths.NativeDirectory, EntryDll));

    // Copies every catalog DLL found in `sourcePath` into NativeDirectory,
    // overwriting existing files. Returns the count of DLLs actually copied.
    // Files in the source folder that are NOT in the catalog are ignored —
    // keeps NativeDirectory tidy when the user points at a build folder
    // that contains other artifacts.
    //
    // Used by the future wizard's "Browse for folder..." flow. Idempotent:
    // calling twice with the same source overwrites the same files.
    public static int CopyFromFolder(string sourcePath)
    {
        Directory.CreateDirectory(AppPaths.NativeDirectory);
        int copied = 0;
        foreach (string name in RequiredDllNames)
        {
            string from = Path.Combine(sourcePath, name);
            if (!File.Exists(from)) continue;
            string to = Path.Combine(AppPaths.NativeDirectory, name);
            File.Copy(from, to, overwrite: true);
            copied++;
        }
        return copied;
    }

    // Reports which catalog DLLs are missing from NativeDirectory. Empty
    // result = fully installed. Used by the wizard to surface a precise
    // status ("4 of 8 files installed") and by diagnostics surfaces.
    public static IReadOnlyList<string> GetMissing()
    {
        var missing = new List<string>();
        foreach (string name in RequiredDllNames)
        {
            string path = Path.Combine(AppPaths.NativeDirectory, name);
            if (!File.Exists(path)) missing.Add(name);
        }
        return missing;
    }
}
