using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhispUI.Logging;

// ── TelemetryService ────────────────────────────────────────────────────────
//
// Singleton emission hub. Every runtime observation in WhispUI funnels
// through this — logs via Log(), per-transcription latency via Latency(),
// raw corpus rows via Corpus(), audio WAV capture via Audio(). The service
// does not persist anything itself: it builds a TelemetryEvent, stamps it
// with the session id, and dispatches to registered sinks.
//
// Session id:
//   "YYYY-MM-DD-XXXX" where XXXX is a 4-hex random suffix. Generated once
//   at service construction so every event from a single process run
//   shares the same id. Consumed by the benchmark tooling to group rows
//   across files without relying on adjacent timestamps.
//
// Thread-safety:
//   AddSink / RemoveSink lock a private list; Emit snapshots the list
//   under the same lock then releases it before dispatching. A slow sink
//   can't block other emissions, but it still runs on the caller thread.
public sealed class TelemetryService
{
    public static TelemetryService Instance { get; } = new();

    private readonly List<ITelemetrySink> _sinks = new();
    private readonly object _sinkLock = new();

    public string SessionId { get; }

    private TelemetryService()
    {
        // 4 hex chars = 65 536 distinct session slots per day — enough to
        // never collide in practice while keeping the id short for human
        // inspection (grep, file names).
        var rng = Random.Shared;
        int suffix = rng.Next(0, 0x10000);
        SessionId = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  + "-" + suffix.ToString("x4", CultureInfo.InvariantCulture);
    }

    public void AddSink(ITelemetrySink sink)
    {
        lock (_sinkLock) _sinks.Add(sink);
    }

    public void RemoveSink(ITelemetrySink sink)
    {
        lock (_sinkLock) _sinks.Remove(sink);
    }

    // ── Log ────────────────────────────────────────────────────────────────
    //
    // Used by the LogService façade for the 6 log levels. The level is
    // copied onto the event (for UI filtering) AND serialized inside the
    // payload as its enum name — the JSONL stays self-describing.
    public void Log(string source, string message, LogLevel level, UserFeedback? feedback)
    {
        var payload = new LogPayload(source, message, level.ToString());
        string text = source.Length > 0
            ? $"{DateTime.Now:HH:mm:ss.fff} [{source}] {message}"
            : $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Emit(new TelemetryEvent(TelemetryKind.Log, SessionId, payload, level, feedback, text));
    }

    // ── Latency ────────────────────────────────────────────────────────────
    //
    // One row per completed transcription (including no-speech outcomes).
    // Compact [LATENCY] rendering in LogWindow; own latency.jsonl file.
    public void Latency(LatencyPayload payload)
    {
        string text =
            $"{DateTime.Now:HH:mm:ss.fff} [LATENCY] " +
            $"audio={payload.AudioSec.ToString("F1", CultureInfo.InvariantCulture)}s " +
            $"whisper={payload.WhisperMs}ms llm={payload.LlmMs}ms paste={payload.PasteMs}ms " +
            $"outcome={payload.Outcome}";
        Emit(new TelemetryEvent(TelemetryKind.Latency, SessionId, payload, LogLevel.Info, feedback: null, text));
    }

    // ── Corpus ─────────────────────────────────────────────────────────────
    //
    // Raw Whisper output captured for offline benchmarking. Gated by the
    // caller (TelemetrySettings.CorpusEnabled); the service itself never
    // reads settings.
    public void Corpus(CorpusPayload payload)
    {
        double wps = payload.Metrics.WordsPerSecond;
        string text =
            $"{DateTime.Now:HH:mm:ss.fff} [CORPUS] " +
            $"profile={payload.Slug} " +
            $"words={payload.Raw.WordCount} " +
            $"wps={wps.ToString("F1", CultureInfo.InvariantCulture)}";
        Emit(new TelemetryEvent(TelemetryKind.Corpus, SessionId, payload, LogLevel.Info, feedback: null, text));
    }

    // ── Microphone ─────────────────────────────────────────────────────────
    //
    // One row per Recording, summarising the per-50-ms-sub-window RMS series
    // accumulated during capture. Gated by the caller (TelemetrySettings.
    // MicrophoneTelemetry) — same posture as Latency / Corpus.
    //
    // Single emission: the same event carries both the human-readable Text
    // (for LogWindow display, prefixed [RECORD] like the matching capture
    // log lines) and the structured payload (for the microphone.jsonl
    // sink). One source of truth — no parallel _log.Info on the Log
    // pipeline, no duplicate row.
    public void Microphone(MicrophoneTelemetryPayload p)
    {
        var c = CultureInfo.InvariantCulture;
        string text =
            $"{DateTime.Now:HH:mm:ss.fff} [RECORD] " +
            $"Mic telemetry over {p.DurationSeconds.ToString("F1", c)}s " +
            $"({p.Samples} samples @20Hz): " +
            $"min={p.MinDbfs.ToString("F1", c)} " +
            $"p10={p.P10Dbfs.ToString("F1", c)} " +
            $"p25={p.P25Dbfs.ToString("F1", c)} " +
            $"p50={p.P50Dbfs.ToString("F1", c)} " +
            $"p75={p.P75Dbfs.ToString("F1", c)} " +
            $"p90={p.P90Dbfs.ToString("F1", c)} " +
            $"max={p.MaxDbfs.ToString("F1", c)} dBFS " +
            $"| mean RMS={p.MeanRms.ToString("F4", c)} " +
            $"({p.MeanDbfs.ToString("F1", c)} dBFS)";
        Emit(new TelemetryEvent(TelemetryKind.Microphone, SessionId, p, LogLevel.Info, feedback: null, text));
    }

    private void Emit(TelemetryEvent ev)
    {
        ITelemetrySink[] snapshot;
        lock (_sinkLock) snapshot = _sinks.ToArray();

        foreach (var sink in snapshot)
        {
            try { sink.Write(ev); }
            catch { /* A sink must never crash the caller. */ }
        }
    }
}
