using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhispUI.Logging.Sinks;

// ── JsonlFileSink ───────────────────────────────────────────────────────────
//
// Writes every TelemetryEvent as a JSON line to disk, routed by kind:
//
//   kind=log         → <telemetry>/app.jsonl           — gated, rotated
//   kind=latency     → <telemetry>/latency.jsonl       — gated
//   kind=corpus      → <telemetry>/<slug>/corpus.jsonl — gated
//   kind=microphone  → <telemetry>/microphone.jsonl    — gated
//
// <telemetry> resolves to AppPaths.TelemetryDirectory (= <UserDataRoot>\
// telemetry\) by default, or the user-configured StorageDirectory when set.
//
// All four streams are opt-in: toggles are read at write time, so flipping
// a toggle off stops new writes immediately without rebuilding the sink
// graph. This is the confidentiality-first posture — nothing lands on disk
// unless the user explicitly enabled that stream through the Settings UI
// (each behind its own consent dialog on first opt-in).
//
// app.jsonl is additionally rotated every 5000 lines (logrotate-style:
// app.jsonl → app-1.jsonl → app-2.jsonl … ). No cap on archives — the
// user prunes manually if it ever runs away.
//
// One global lock: writes are rare on the wall-clock scale (one log entry
// per pipeline step, one latency + one corpus per transcription). Per-file
// locking would buy nothing but boilerplate.
//
// Archive pass: on construction, if the legacy telemetry.csv still exists
// (under one of the pre-refonte layouts — <data>/telemetry/, <bench>/logs/,
// <data>/legacy/), it's moved into <telemetry>/legacy/telemetry-YYYYMMDD.csv
// so the new JSONL pipeline starts from a clean slate without losing the
// CSV history accumulated during dev.
//
// Fail-soft: any IO error is swallowed. Telemetry persistence must never
// take down the pipeline.
internal sealed class JsonlFileSink : ITelemetrySink
{
    private const int AppLogRotationLineThreshold = 5000;

    private readonly object _lock = new();

    // Lazy line counter for app.jsonl — reads once at first write (or on
    // construction if the file already exists) and is incremented in-band.
    // Re-read from disk if the file gets externally truncated? No: rotation
    // is our own action and we reset the counter when we do it. External
    // edits to the live log file aren't a concern.
    private int  _appLogLineCount  = -1;
    private bool _appLogCountKnown = false;

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
                if (ev.Kind == TelemetryKind.Log)
                    RotateAppLogIfNeeded(path);

                using var w = new StreamWriter(path, append: true);
                w.WriteLine(line);

                if (ev.Kind == TelemetryKind.Log && _appLogCountKnown)
                    _appLogLineCount++;
            }
        }
        catch
        {
            // Persistence must never break the pipeline.
        }
    }

    private static string? ResolvePath(TelemetryEvent ev)
    {
        string root = CorpusPaths.GetDirectoryPath();

        switch (ev.Kind)
        {
            case TelemetryKind.Log:
                if (!ReadSettingsToggle(s => s.ApplicationLogToDisk)) return null;
                return Path.Combine(root, "app.jsonl");

            case TelemetryKind.Latency:
                if (!ReadSettingsToggle(s => s.LatencyEnabled)) return null;
                return Path.Combine(root, "latency.jsonl");

            case TelemetryKind.Corpus:
                if (!ReadSettingsToggle(s => s.CorpusEnabled)) return null;
                if (ev.Payload is not CorpusPayload cp) return null;
                string profileDir = Path.Combine(root, CorpusPaths.Sanitize(cp.Slug));
                return Path.Combine(profileDir, "corpus.jsonl");

            case TelemetryKind.Microphone:
                if (!ReadSettingsToggle(s => s.MicrophoneTelemetry)) return null;
                return Path.Combine(root, "microphone.jsonl");
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

    // ── App log rotation ────────────────────────────────────────────────────
    //
    // Called under the write lock, right before appending to app.jsonl. On
    // first call, counts the existing lines once so we know where we stand
    // after a restart. Once the count crosses the threshold, shifts the
    // archives down (app-N → app-(N+1)) and renames the live file to app-1,
    // then resets the counter to zero so the next write creates a fresh
    // app.jsonl.

    private void RotateAppLogIfNeeded(string livePath)
    {
        if (!_appLogCountKnown)
        {
            _appLogLineCount = File.Exists(livePath)
                ? CountLines(livePath)
                : 0;
            _appLogCountKnown = true;
        }

        if (_appLogLineCount < AppLogRotationLineThreshold) return;
        if (!File.Exists(livePath)) { _appLogLineCount = 0; return; }

        try
        {
            string dir = Path.GetDirectoryName(livePath)!;
            int highest = HighestExistingArchiveIndex(dir);

            // Shift descending: app-N → app-(N+1), …, app-1 → app-2.
            for (int i = highest; i >= 1; i--)
            {
                string from = Path.Combine(dir, $"app-{i}.jsonl");
                string to   = Path.Combine(dir, $"app-{i + 1}.jsonl");
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }

            // Promote live file to app-1.
            string firstArchive = Path.Combine(dir, "app-1.jsonl");
            File.Move(livePath, firstArchive, overwrite: true);
        }
        catch
        {
            // Rotation failed — best-effort, continue writing to the live
            // file. Worst case: the file grows past the threshold, user
            // sees a larger log and prunes by hand.
        }
        finally
        {
            _appLogLineCount = 0;
        }
    }

    private static int HighestExistingArchiveIndex(string dir)
    {
        try
        {
            int max = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "app-*.jsonl"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 4 && int.TryParse(name.AsSpan(4), out int n) && n > max)
                    max = n;
            }
            return max;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountLines(string path)
    {
        try
        {
            return File.ReadLines(path).Count();
        }
        catch
        {
            return 0;
        }
    }

    // ── Legacy CSV archival ─────────────────────────────────────────────────
    //
    // Pre-refonte, telemetry.csv could live in three places:
    //   • <benchmark>/logs/telemetry.csv         (first layout)
    //   • <data>/telemetry/telemetry.csv         (second layout)
    //   • <data>/legacy/telemetry-YYYYMMDD.csv   (already archived, sibling
    //                                             of the new telemetry root)
    // The data→telemetry rename itself is handled out of band (git commit
    // moves the folder), so once the rename lands the legacy CSV is already
    // at <telemetry>/legacy/. This pass still handles a stray live CSV if
    // one reappears — e.g. if the user points StorageDirectory at an older
    // tree manually.

    private static void ArchiveLegacyCsv()
    {
        try
        {
            string root = CorpusPaths.GetDirectoryPath();

            string? csv = FindLegacyCsv(root);
            if (csv is null) return;

            string legacy = Path.Combine(root, "legacy");
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

    private static string? FindLegacyCsv(string telemetryRoot)
    {
        // Stray CSV at the current telemetry root.
        string direct = Path.Combine(telemetryRoot, "telemetry.csv");
        if (File.Exists(direct)) return direct;

        // Second legacy layout: <data>/telemetry/telemetry.csv. At this
        // point telemetryRoot is <bench>/telemetry, so walking up one level
        // and looking for `data/telemetry/telemetry.csv` would catch the
        // pre-rename case — but that case is handled by the git rename, not
        // here. The only remaining real-world path is the very old first
        // layout sibling to the new root.
        string? bench = Directory.GetParent(telemetryRoot)?.FullName;
        if (bench is not null)
        {
            string oldLogs = Path.Combine(bench, "logs", "telemetry.csv");
            if (File.Exists(oldLogs)) return oldLogs;
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
