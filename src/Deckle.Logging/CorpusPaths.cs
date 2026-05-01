using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Deckle.Logging;

// ── CorpusPaths ─────────────────────────────────────────────────────────────
//
// Storage layout helper — resolves the base directory for telemetry JSONL
// and audio WAV files, and normalizes profile names into filesystem-safe
// slugs. Shared by Settings UI, consent dialogs, the JSONL sink, and the
// WAV writer so there's a single source of truth for the storage layout.
//
// Resolution order:
//   1. ITelemetryGates.StorageDirectoryOverride (host-configured absolute
//      path), when non-empty. Read on every call so a host that flips the
//      override at runtime gets picked up immediately.
//   2. AppPaths.TelemetryDirectory (= <UserDataRoot>\telemetry\), always
//      present and writable.
public static class CorpusPaths
{
    public static string GetDirectoryPath()
    {
        string? custom = TelemetryGates.Current.StorageDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        return AppPaths.TelemetryDirectory;
    }

    public static string GetDefaultDirectoryPath() => AppPaths.TelemetryDirectory;

    // Lowercase ASCII, hyphen-separated slug. Accented characters are
    // transliterated via Unicode normalization (NFD + non-spacing-mark
    // strip) so "réécriture" becomes "reecriture" instead of collapsing
    // the accented bytes to hyphens. Empty input returns "unnamed".
    // Stable across the text corpus JSONL path and the audio WAV subfolder
    // name — callers rely on it to join the two sides of the corpus.
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";

        string lowered = name.ToLowerInvariant();
        string decomposed = lowered.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        string stripped = sb.ToString().Normalize(NormalizationForm.FormC);
        string replaced = Regex.Replace(stripped, @"[^a-z0-9]+", "-");
        string trimmed = replaced.Trim('-');
        return string.IsNullOrEmpty(trimmed) ? "unnamed" : trimmed;
    }

    public static string Sanitize(string s)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            s = s.Replace(invalid, '-');
        return s;
    }
}
