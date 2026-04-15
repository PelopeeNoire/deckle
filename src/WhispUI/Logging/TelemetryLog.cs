namespace WhispUI.Logging;

// One row per completed transcription. Consumed by the benchmark tooling
// to chart phase costs vs audio duration. Written append-only to a CSV —
// structured data, not a log. No LogService sink on purpose.
internal sealed record TelemetrySample(
    double AudioSec,
    long   VadMs,
    long   WhisperMs,
    long   LlmMs,
    long   ClipboardMs,
    long   PasteMs,
    string Strategy,
    int    NSegments,
    int    TextChars,
    string Profile,
    bool   Pasted,
    string Outcome);

// Append-only CSV sink. Target: <repo>/benchmark/logs/telemetry.csv.
// Resolution walks up from AppContext.BaseDirectory looking for a sibling
// "benchmark" folder — kept outside %LOCALAPPDATA% on purpose while we're
// still in dev. Silent no-op if the marker can't be found, so the pipeline
// is never impacted by a misplaced exe.
internal static class TelemetryLog
{
    private const string Header =
        "timestamp,audio_sec,vad_ms,whisper_ms,llm_ms,clipboard_ms,paste_ms,"
        + "strategy,n_segments,text_chars,profile,pasted,outcome";

    private static readonly object _lock = new();
    private static readonly Lazy<string?> _path = new(ResolvePath);

    public static void Append(TelemetrySample s)
    {
        string? path = _path.Value;
        if (path is null) return;

        try
        {
            lock (_lock)
            {
                bool isNew = !File.Exists(path);
                using var w = new StreamWriter(path, append: true);
                if (isNew) w.WriteLine(Header);

                string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                w.WriteLine(
                    $"{ts},{s.AudioSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},"
                    + $"{s.VadMs},{s.WhisperMs},{s.LlmMs},{s.ClipboardMs},{s.PasteMs},"
                    + $"{Escape(s.Strategy)},{s.NSegments},{s.TextChars},"
                    + $"{Escape(s.Profile)},{s.Pasted.ToString().ToLowerInvariant()},{Escape(s.Outcome)}");
            }
        }
        catch
        {
            // Telemetry must never take down the pipeline.
        }
    }

    private static string? ResolvePath()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string bench = Path.Combine(dir.FullName, "benchmark");
                if (Directory.Exists(bench))
                {
                    string logs = Path.Combine(bench, "logs");
                    Directory.CreateDirectory(logs);
                    return Path.Combine(logs, "telemetry.csv");
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Fall through to null — telemetry disabled.
        }
        return null;
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
