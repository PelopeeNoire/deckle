using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WhispUI.Settings;

namespace WhispUI.Logging;

// ── CorpusLog ────────────────────────────────────────────────────────────────
//
// Append-only JSONL sink capturing the raw Whisper output of every
// consented transcription, for offline benchmarking of the transcription
// step itself (initial prompt, VAD thresholds, model swaps). Deliberately
// distinct from the latency CSV (TelemetryLog) — different consumer,
// different shape, different retention expectations.
//
// We don't capture the rewrite: prompts evolve constantly, which would turn
// every past sample into noise the moment the prompt is edited. Raw text
// stays useful across prompt iterations.
//
// Target: the custom path set in CorpusLogging.DataDirectory if non-empty,
// otherwise <repo>/benchmark/data/<profile-slug>.jsonl. The default resolver
// walks up from the exe looking for a sibling "benchmark" folder, same
// algorithm as TelemetryLog. Kept outside %LOCALAPPDATA% for the same
// reasons: the dev build is unpackaged, and the whole benchmark/ tree lives
// with the repo.
//
// Thread-safety: single global lock — writes are rare (one per transcription),
// locking per-file adds complexity without payoff.
// Fail-soft: any IO error is swallowed. Corpus logging must never break a
// transcription.
//
// Gated by CorpusLogging.Enabled — the caller checks the flag. This class
// assumes "if you're calling Append, you already consented".

internal sealed record CorpusWhisper(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("language")]   string Language,
    [property: JsonPropertyName("elapsed_ms")] long   ElapsedMs);

internal sealed record CorpusRaw(
    [property: JsonPropertyName("text")]       string Text,
    [property: JsonPropertyName("word_count")] int    WordCount,
    [property: JsonPropertyName("char_count")] int    CharCount);

internal sealed record CorpusMetrics(
    [property: JsonPropertyName("words_per_second")] double WordsPerSecond);

// Captures only the raw Whisper output, not the rewrite. The rewrite changes
// every time the prompt is edited, so a corpus of (raw, rewrite) pairs
// becomes stale as soon as the prompt evolves — we keep raw-only samples
// which stay useful across prompt iterations.
internal sealed record CorpusEntry(
    [property: JsonPropertyName("timestamp")]        DateTimeOffset Timestamp,
    [property: JsonPropertyName("duration_seconds")] double         DurationSeconds,
    [property: JsonPropertyName("whisper")]          CorpusWhisper  Whisper,
    [property: JsonPropertyName("raw")]              CorpusRaw      Raw,
    [property: JsonPropertyName("metrics")]          CorpusMetrics  Metrics);

internal static class CorpusLog
{
    private static readonly object _lock = new();
    private static readonly Lazy<string?> _defaultBaseDir = new(ResolveDefaultBaseDir);

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Keep non-ASCII readable in the file (JSON strings default to \uXXXX).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // slugPrefix identifies the target file. Callers build it from the rewrite
    // profile: "<name-slug>-<id-prefix>" so renaming the profile changes the
    // human-readable part but keeps the id suffix stable.
    public static void Append(string slugPrefix, CorpusEntry entry)
    {
        string? dir = GetDirectoryPath();
        if (dir is null || string.IsNullOrWhiteSpace(slugPrefix)) return;

        try
        {
            Directory.CreateDirectory(dir);
            string safe = Sanitize(slugPrefix);
            string path = Path.Combine(dir, safe + ".jsonl");
            string line = JsonSerializer.Serialize(entry, _json);

            lock (_lock)
            {
                using var w = new StreamWriter(path, append: true);
                w.WriteLine(line);
            }
        }
        catch
        {
            // Corpus logging must never take down the pipeline.
        }
    }

    // Resolved storage directory — surfaced so the Settings UI can wire up an
    // "Open storage folder" link. Returns the user-configured path if set
    // (CorpusLogging.DataDirectory), otherwise falls back to the default dev
    // resolver (<repo>/benchmark/data/). Returns null only when both are
    // unavailable — the dev layout can't be detected AND the user hasn't
    // configured a path.
    public static string? GetDirectoryPath()
    {
        string custom = "";
        try
        {
            custom = SettingsService.Instance.Current.CorpusLogging.DataDirectory ?? "";
        }
        catch
        {
            // Settings not initialized yet — fall through to the default.
        }

        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        return _defaultBaseDir.Value;
    }

    // Default resolver output only — ignores the user-configured path. Used
    // by Settings to show the auto-path as a placeholder in the TextBox.
    public static string? GetDefaultDirectoryPath() => _defaultBaseDir.Value;

    // Turns a human profile name into a safe, readable slug. Lowercase ASCII,
    // hyphens between words, no filesystem-hostile characters.
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        string lowered = name.ToLowerInvariant();
        string replaced = Regex.Replace(lowered, @"[^a-z0-9]+", "-");
        string trimmed = replaced.Trim('-');
        return string.IsNullOrEmpty(trimmed) ? "unnamed" : trimmed;
    }

    private static string Sanitize(string s)
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
            // Fall through — corpus disabled.
        }
        return null;
    }
}
