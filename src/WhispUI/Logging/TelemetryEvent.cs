using System;
using System.Text.Json.Serialization;

namespace WhispUI.Logging;

// ── TelemetryEvent ──────────────────────────────────────────────────────────
//
// Unified envelope for every piece of data WhispUI emits at runtime. Three
// pipelines that used to run disjoint (logs, latency CSV, corpus JSONL) now
// produce the same event shape:
//
//     { timestamp, kind, session, payload }
//
// `kind` drives routing on the sink side: "log" fans out to LogWindow +
// JSONL app log + HUD feedback, "latency" and "corpus" land in their own
// JSONL files and render a dedicated compact row in LogWindow.
//
// `session` is the process-local session id (YYYY-MM-DD-XXXX). It ties
// every event from a single run together so the benchmark tooling can
// group latency + corpus + log rows across files.
//
// The Feedback slot is a transient UI routing hint (HudFeedbackSink reads
// it). Kept off the serialized payload via [JsonIgnore] — it doesn't belong
// in persisted records and a copy would need UI types in the JSON schema.
//
// Text is precomputed for LogWindow so the template selector doesn't
// re-format on every virtualized row realization.

// ─── Log levels ──────────────────────────────────────────────────────────────
// Verbose   : background noise (heartbeats, per-segment dumps, clipboard plumbing).
// Info      : normal workflow events (recording, return codes, text, copy, paste).
// Success   : rare verified milestones (model loaded, end-to-end OK) — green ack.
// Warning   : non-fatal issues (focus loss, empty buffers, slow dependency).
// Error     : failures (init errors, transcription failures, mic unavailable).
// Narrative : plain-language explanation of pipeline activity, written for the
//             user (Narrative view) — sits outside the technical hierarchy above.
public enum LogLevel { Verbose, Info, Success, Warning, Error, Narrative }

public enum TelemetryKind { Log, Latency, Corpus, Microphone }

public sealed class TelemetryEvent
{
    public DateTimeOffset Timestamp { get; }
    public TelemetryKind  Kind      { get; }
    public string         Session   { get; }
    public object         Payload   { get; }

    // Only meaningful when Kind == Log. Copied out of the log level so the
    // LogWindow filter can stay on the event object without peeking at the
    // payload type. Defaults to Info for non-log kinds — never used.
    public LogLevel Level { get; }

    [JsonIgnore]
    public UserFeedback? Feedback { get; }

    public string Text { get; }

    internal TelemetryEvent(TelemetryKind kind, string session, object payload, LogLevel level, UserFeedback? feedback, string text)
    {
        Timestamp = DateTimeOffset.Now;
        Kind      = kind;
        Session   = session;
        Payload   = payload;
        Level     = level;
        Feedback  = feedback;
        Text      = text;
    }
}

// ── Payloads ────────────────────────────────────────────────────────────────
//
// Each payload is a record with JsonPropertyName hints so the JSONL files
// read as idiomatic snake_case. Payloads are intentionally POCOs with no
// back-reference to the event — the envelope carries timestamp/kind/session.

public sealed record LogPayload(
    [property: JsonPropertyName("source")]  string Source,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("level")]   string Level);

public sealed record LatencyPayload(
    [property: JsonPropertyName("audio_sec")]    double AudioSec,
    [property: JsonPropertyName("vad_ms")]       long   VadMs,
    [property: JsonPropertyName("whisper_ms")]   long   WhisperMs,
    [property: JsonPropertyName("llm_ms")]       long   LlmMs,
    [property: JsonPropertyName("clipboard_ms")] long   ClipboardMs,
    [property: JsonPropertyName("paste_ms")]     long   PasteMs,
    [property: JsonPropertyName("strategy")]     string Strategy,
    [property: JsonPropertyName("n_segments")]   int    NSegments,
    [property: JsonPropertyName("text_chars")]   int    TextChars,
    [property: JsonPropertyName("text_words")]   int    TextWords,
    [property: JsonPropertyName("profile")]      string Profile,
    [property: JsonPropertyName("pasted")]       bool   Pasted,
    [property: JsonPropertyName("outcome")]      string Outcome);

// Whisper-side configuration captured alongside the raw text. InitialPrompt
// is the new knob: benchmark runs group corpus entries by prompt version to
// measure the impact of a prompt change without re-recording.
public sealed record WhisperSection(
    [property: JsonPropertyName("model")]          string  Model,
    [property: JsonPropertyName("language")]       string  Language,
    [property: JsonPropertyName("elapsed_ms")]     long    ElapsedMs,
    [property: JsonPropertyName("initial_prompt")] string? InitialPrompt);

public sealed record RawSection(
    [property: JsonPropertyName("text")]       string Text,
    [property: JsonPropertyName("word_count")] int    WordCount,
    [property: JsonPropertyName("char_count")] int    CharCount);

public sealed record CorpusMetricsSection(
    [property: JsonPropertyName("words_per_second")] double WordsPerSecond);

public sealed record CorpusPayload(
    [property: JsonPropertyName("profile")]          string               Profile,
    [property: JsonPropertyName("profile_id")]       string               ProfileId,
    [property: JsonPropertyName("slug")]             string               Slug,
    [property: JsonPropertyName("duration_seconds")] double               DurationSeconds,
    [property: JsonPropertyName("whisper")]          WhisperSection       Whisper,
    [property: JsonPropertyName("raw")]              RawSection           Raw,
    [property: JsonPropertyName("metrics")]          CorpusMetricsSection Metrics,
    [property: JsonPropertyName("audio_file")]       string?              AudioFile);

// One row per Recording when TelemetrySettings.MicrophoneTelemetry is on.
// dBFS percentile sweep over the 50 ms sub-window RMS series, plus the
// linear mean RMS (the value worth comparing against MaxDbfs window when
// calibrating). MeanDbfs is derived from the linear mean — log of the
// mean, not mean of the log, since arithmetic mean of dBFS values gets
// pulled too low by the silence floor.
public sealed record MicrophoneTelemetryPayload(
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    [property: JsonPropertyName("samples")]          int    Samples,
    [property: JsonPropertyName("min_dbfs")]         double MinDbfs,
    [property: JsonPropertyName("p10_dbfs")]         double P10Dbfs,
    [property: JsonPropertyName("p25_dbfs")]         double P25Dbfs,
    [property: JsonPropertyName("p50_dbfs")]         double P50Dbfs,
    [property: JsonPropertyName("p75_dbfs")]         double P75Dbfs,
    [property: JsonPropertyName("p90_dbfs")]         double P90Dbfs,
    [property: JsonPropertyName("max_dbfs")]         double MaxDbfs,
    [property: JsonPropertyName("mean_rms")]         double MeanRms,
    [property: JsonPropertyName("mean_dbfs")]        double MeanDbfs,
    [property: JsonPropertyName("tail_rms")]         double TailRms,
    [property: JsonPropertyName("tail_dbfs")]        double TailDbfs,
    [property: JsonPropertyName("tail_state")]       string TailState);
