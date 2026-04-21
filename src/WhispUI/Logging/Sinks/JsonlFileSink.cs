using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhispUI.Logging.Sinks;

// ── JsonlFileSink ───────────────────────────────────────────────────────────
//
// Writes every TelemetryEvent as a JSON line to disk, routed by kind:
//
//   kind=log      → <benchmark>/data/telemetry/app.jsonl      — always
//   kind=latency  → <benchmark>/data/telemetry/latency.jsonl  — gated
//   kind=corpus   → <corpus-root>/corpus/<slug>.jsonl         — gated
//
// Everything produced at runtime lives under <benchmark>/data/. The
// <benchmark>/logs/ folder is reserved for the benchmark script's own
// step-by-step execution log, so a crashed Python run can be resumed
// without scrolling through WhispUI telemetry.
//
// Log events persist unconditionally: the app log is a dev/debug artifact,
// not sensitive user data. Latency and corpus are gated on their own
// TelemetrySettings toggles read here at write time, so flipping a toggle
// off stops new writes immediately without rebuilding the sink graph.
//
// One global lock: writes are rare on the wall-clock scale (one log entry
// per pipeline step, one latency + one corpus per transcription). Per-file
// locking would buy nothing but boilerplate.
//
// Archive pass: on construction, if the legacy telemetry.csv still exists
// (under the old <logs> layout or the current <data/telemetry> layout),
// it is moved into <data>/legacy/telemetry-YYYYMMDD.csv so the new JSONL
// pipeline starts from a clean slate without losing the CSV history Louis
// accumulated during dev.
//
// Fail-soft: any IO error is swallowed. Telemetry persistence must never
// take down the pipeline.
internal sealed class JsonlFileSink : ITelemetrySink
{
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters             = { new JsonStringEnumConverter() },
    };

    public JsonlFileSink()
    {
        ArchiveLegacyCsv();
    }

    public void Write(TelemetryEvent ev)
    {
        string? path = ResolvePath(ev);
        if (path is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // One serialized line per event. Shape:
            //     {"timestamp":"...","kind":"...","session":"...","payload":{...}}
            // The envelope is built inline here — TelemetryEvent can't be
            // a record-with-constructor because it also carries the
            // non-serialized Feedback + Text slots used by UI sinks.
            var envelope = new Envelope
            {
                Timestamp = ev.Timestamp,
                Kind      = ev.Kind,
                Session   = ev.Session,
                Payload   = ev.Payload,
            };
            string line = JsonSerializer.Serialize(envelope, _json);

            lock (_lock)
            {
                using var w = new StreamWriter(path, append: true);
                w.WriteLine(line);
            }
        }
        catch
        {
            // Persistence must never break the pipeline.
        }
    }

    private static string? ResolvePath(TelemetryEvent ev)
    {
        switch (ev.Kind)
        {
            case TelemetryKind.Log:
            {
                string? dir = ResolveTelemetryDir();
                return dir is null ? null : Path.Combine(dir, "app.jsonl");
            }

            case TelemetryKind.Latency:
            {
                if (!ReadSettingsToggle(s => s.LatencyEnabled)) return null;
                string? dir = ResolveTelemetryDir();
                return dir is null ? null : Path.Combine(dir, "latency.jsonl");
            }

            case TelemetryKind.Corpus:
            {
                if (!ReadSettingsToggle(s => s.CorpusEnabled)) return null;
                if (ev.Payload is not CorpusPayload cp) return null;
                string? root = CorpusPaths.GetDirectoryPath();
                if (root is null) return null;
                return Path.Combine(root, "corpus", CorpusPaths.Sanitize(cp.Slug) + ".jsonl");
            }
        }
        return null;
    }

    // Settings may not be initialized at the first emit (crash-in-ctor
    // path). Fall through to "disabled" so we never throw out of a sink.
    private static bool ReadSettingsToggle(Func<Settings.TelemetrySettings, bool> reader)
    {
        try
        {
            return reader(Settings.SettingsService.Instance.Current.Telemetry);
        }
        catch
        {
            return false;
        }
    }

    // Target: <benchmark>/data/telemetry/. Falls back to null when the
    // benchmark folder can't be located (shipped builds without the dev
    // tree).
    private static string? ResolveTelemetryDir()
    {
        string? dataRoot = ResolveDataDir();
        if (dataRoot is null) return null;
        try
        {
            string tele = Path.Combine(dataRoot, "telemetry");
            Directory.CreateDirectory(tele);
            return tele;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveDataDir()
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

    // Move telemetry.csv to <data>/legacy/ regardless of where we find it
    // (old <benchmark>/logs/ layout or current <data>/telemetry/ layout).
    private static void ArchiveLegacyCsv()
    {
        try
        {
            string? dataRoot = ResolveDataDir();
            if (dataRoot is null) return;

            string? csv = FindLegacyCsv(dataRoot);
            if (csv is null) return;

            string legacy = Path.Combine(dataRoot, "legacy");
            Directory.CreateDirectory(legacy);
            string stamped = Path.Combine(legacy, $"telemetry-{DateTime.Now:yyyyMMdd}.csv");

            // Don't overwrite a same-day archive: suffix with a short id.
            if (File.Exists(stamped))
            {
                stamped = Path.Combine(legacy,
                    $"telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            }

            File.Move(csv, stamped);
        }
        catch
        {
            // Archive is best-effort — don't abort startup if it fails.
        }
    }

    private static string? FindLegacyCsv(string dataRoot)
    {
        // Current layout.
        string current = Path.Combine(dataRoot, "telemetry", "telemetry.csv");
        if (File.Exists(current)) return current;

        // Old layout: sibling <benchmark>/logs/telemetry.csv.
        string? bench = Directory.GetParent(dataRoot)?.FullName;
        if (bench is not null)
        {
            string legacy = Path.Combine(bench, "logs", "telemetry.csv");
            if (File.Exists(legacy)) return legacy;
        }
        return null;
    }

    private sealed class Envelope
    {
        [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
        [JsonPropertyName("kind")]      public TelemetryKind  Kind      { get; set; }
        [JsonPropertyName("session")]   public string         Session   { get; set; } = "";
        [JsonPropertyName("payload")]   public object         Payload   { get; set; } = new();
    }
}
