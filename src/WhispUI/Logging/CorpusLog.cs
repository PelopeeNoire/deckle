using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WhispUI.Logging;

// ── CorpusLog ────────────────────────────────────────────────────────────────
//
// Append-only JSONL sink, one file per rewrite profile, for long-horizon
// offline iteration on rewrite prompts. Deliberately distinct from the
// latency CSV (TelemetryLog) — different consumer, different shape, different
// retention expectations.
//
// Target: <repo>/benchmark/data/<profile-slug>.jsonl. Resolution walks up
// from the exe looking for a sibling "benchmark" folder, same algorithm as
// TelemetryLog. Kept outside %LOCALAPPDATA% for the same reasons: the dev
// build is unpackaged, and the whole benchmark/ tree lives with the repo.
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

internal sealed record CorpusRewrite(
    [property: JsonPropertyName("prompt_id")]   string PromptId,
    [property: JsonPropertyName("prompt_name")] string PromptName,
    [property: JsonPropertyName("model")]       string Model,
    [property: JsonPropertyName("elapsed_ms")]  long   ElapsedMs,
    [property: JsonPropertyName("text")]        string Text,
    [property: JsonPropertyName("word_count")]  int    WordCount,
    [property: JsonPropertyName("char_count")]  int    CharCount);

internal sealed record CorpusMetrics(
    [property: JsonPropertyName("words_ratio")]      double? WordsRatio,
    [property: JsonPropertyName("chars_ratio")]      double? CharsRatio,
    [property: JsonPropertyName("words_per_second")] double  WordsPerSecond);

internal sealed record CorpusEntry(
    [property: JsonPropertyName("timestamp")]        DateTimeOffset Timestamp,
    [property: JsonPropertyName("duration_seconds")] double         DurationSeconds,
    [property: JsonPropertyName("whisper")]          CorpusWhisper  Whisper,
    [property: JsonPropertyName("raw")]              CorpusRaw      Raw,
    [property: JsonPropertyName("rewrite")]          CorpusRewrite? Rewrite,
    [property: JsonPropertyName("metrics")]          CorpusMetrics  Metrics);

internal static class CorpusLog
{
    private static readonly object _lock = new();
    private static readonly Lazy<string?> _baseDir = new(ResolveBaseDir);

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
        string? dir = _baseDir.Value;
        if (dir is null || string.IsNullOrWhiteSpace(slugPrefix)) return;

        try
        {
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
    // "Open storage folder" link. Returns null if the benchmark marker can't
    // be found (dev layout is detected by walking up from the exe looking for
    // a sibling "benchmark" folder).
    public static string? GetDirectoryPath() => _baseDir.Value;

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

    private static string? ResolveBaseDir()
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
