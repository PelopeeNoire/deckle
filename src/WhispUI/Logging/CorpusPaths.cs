using System;
using System.IO;
using System.Text.RegularExpressions;
using WhispUI.Settings;

namespace WhispUI.Logging;

// ── CorpusPaths ─────────────────────────────────────────────────────────────
//
// Storage layout helper — resolves the base directory for corpus JSONL
// and audio WAV files, and normalizes profile names into filesystem-safe
// slugs. Shared by Settings UI, consent dialogs, the JSONL sink, and the
// WAV writer so there's a single source of truth for the storage layout.
//
// Resolution order:
//   1. User-configured TelemetrySettings.StorageDirectory (absolute path),
//      when non-empty.
//   2. The dev fallback: walk up from the exe directory, looking for a
//      sibling "benchmark" folder. Returns "<benchmark>/data".
//
// Returns null when both paths fail — callers skip persistence.
public static class CorpusPaths
{
    private static readonly Lazy<string?> _defaultBaseDir = new(ResolveDefaultBaseDir);

    public static string? GetDirectoryPath()
    {
        string custom = "";
        try
        {
            custom = SettingsService.Instance.Current.Telemetry.StorageDirectory ?? "";
        }
        catch
        {
            // Settings not initialized yet — fall through to the default.
        }

        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        return _defaultBaseDir.Value;
    }

    public static string? GetDefaultDirectoryPath() => _defaultBaseDir.Value;

    // Lowercase ASCII, hyphen-separated slug. Non-ASCII characters collapse
    // into hyphens; empty input returns "unnamed". Stable across the text
    // corpus JSONL path and the audio WAV subfolder name.
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        string lowered = name.ToLowerInvariant();
        string replaced = Regex.Replace(lowered, @"[^a-z0-9]+", "-");
        string trimmed = replaced.Trim('-');
        return string.IsNullOrEmpty(trimmed) ? "unnamed" : trimmed;
    }

    public static string Sanitize(string s)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            s = s.Replace(invalid, '-');
        return s;
    }

    private static string? ResolveDefaultBaseDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string bench = Path.Combine(dir.FullName, "benchmark");
                if (Directory.Exists(bench))
                {
                    string data = Path.Combine(bench, "data");
                    Directory.CreateDirectory(data);
                    return data;
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Fall through — persistence disabled.
        }
        return null;
    }
}
