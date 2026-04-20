using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhispUI.Logging;
using WhispUI.Settings;

namespace WhispUI.Llm;

// ─── LLM rewrite service via Ollama (RAW mode) ───────────────────────────────
//
// Calls /api/generate with raw=true and a client-side pre-formatted prompt.
// Completely bypasses Ollama's TEMPLATE system because models imported from
// HuggingFace as GGUF often come with a generic Modelfile
// (TEMPLATE {{ .Prompt }}), which silently breaks the input format expected
// by the model — typical symptom: the model produces gibberish, echoes or loops.
//
// The template is determined client-side from the model name (family
// Mistral/Llama/Qwen/Gemma/Phi/ChatML), applied manually, and sent via
// raw=true — Ollama doesn't touch the prompt.
//
// Designed to be called from a background thread — .GetAwaiter().GetResult()
// is safe here (no synchronization context on this thread).

internal class LlmService
{
    private static readonly LogService _log = LogService.Instance;

    // Default HttpClient.Timeout is 100 s — too short for large rewrites
    // (long transcriptions, big context, CPU-only Ollama). We disable the
    // built-in timeout and manage cancellation explicitly via a per-request
    // CancellationTokenSource (REWRITE_HARD_CAP) plus a /api/ps polling task
    // that keeps the user informed during the wait.
    static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Hard cap on a single Rewrite call. Generous: leaves room for slow CPU-only
    // Ollama setups on big transcripts (20 min audio, 16 K context), but still
    // guards against a stuck worker.
    static readonly TimeSpan REWRITE_HARD_CAP = TimeSpan.FromMinutes(15);

    // /api/ps probe cadence while waiting for /api/generate to return.
    static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(60);

    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? Rewrite(string text, string endpoint, RewriteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            _log.Warning(LogSource.Llm,
                $"profile '{profile.Name}' has no model configured — rewrite skipped. " +
                $"Set it in Settings → LLM.");
            return null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(REWRITE_HARD_CAP);
        try
        {
            var (prompt, stops, family) = PromptTemplates.Build(profile.Model, profile.SystemPrompt, text);
            string generateUrl = NormalizeGenerateUrl(endpoint);
            var options = BuildOptions(profile, stops);

            _log.Info(LogSource.Llm, $"request {text.Length} chars → {profile.Model} (profile: {profile.Name}, family: {family}) | {FormatOptions(options)}");

            var body = new
            {
                model      = profile.Model,
                prompt,
                raw        = true,
                stream     = false,
                keep_alive = "5m",
                options
            };

            string json = JsonSerializer.Serialize(body, _jsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Polling task: hits /api/ps every 60 s while we wait for
            // /api/generate to return — turns the silent wait into a visible
            // warning ("Ollama busy — model X resident...") so the user
            // knows the engine is still working. Classified Warning (not
            // Narrative) because the polling only fires at all if /api/generate
            // hasn't returned after POLL_INTERVAL — by definition an unusually
            // long wait the user should notice. Cancelled via pollDone in the
            // finally block as soon as the request settles.
            using var pollDone = new CancellationTokenSource();
            var pollingTask = Task.Run(() => PollOllamaWhileBusy(endpoint, sw, pollDone.Token));

            HttpResponseMessage response;
            try
            {
                response = _http.PostAsync(generateUrl, content, cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                pollDone.Cancel();
                try { pollingTask.GetAwaiter().GetResult(); }
                catch { /* polling errors are surfaced by their own warnings */ }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();

                string responseJson = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(responseJson);
                string? rewritten = doc.RootElement
                    .GetProperty("response")
                    .GetString();

                sw.Stop();
                string trimmed = PromptTemplates.StripStops(rewritten ?? "", family).Trim();
                _log.Info(LogSource.Llm, $"Rewrite OK ({sw.ElapsedMilliseconds} ms, {text.Length}→{trimmed.Length} chars, profile: {profile.Name})");
                _log.Info(LogSource.Llm, FormatMetrics(doc.RootElement));
                return trimmed;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _log.Warning(LogSource.Llm, $"Ollama took longer than {REWRITE_HARD_CAP.TotalMinutes:F0} min — giving up, raw text preserved");
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Warning(LogSource.Llm,
                $"unavailable: {ex.GetType().Name} {ex.Message} " +
                $"(profile: {profile.Name}, model: {profile.Model}) — raw text preserved");
            return null;
        }
    }

    /// <summary>
    /// Periodically probes Ollama's /api/ps while a /api/generate call is in
    /// flight. Emits a Warning every POLL_INTERVAL describing the resident
    /// model and the elapsed wait — gives the user feedback during a long
    /// wait. Warning (not Narrative) because the polling only starts after
    /// POLL_INTERVAL has elapsed without a response, which is by definition
    /// an unusual delay. Stops cleanly when <paramref name="ct"/> is cancelled
    /// by the caller.
    /// </summary>
    static async Task PollOllamaWhileBusy(string endpoint, System.Diagnostics.Stopwatch requestElapsed, CancellationToken ct)
    {
        string psUrl = NormalizePsUrl(endpoint);
        using var timer = new PeriodicTimer(POLL_INTERVAL);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var resp = await _http.GetAsync(psUrl, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _log.Warning(LogSource.Llm, $"Ollama /api/ps unreachable (HTTP {(int)resp.StatusCode}) — model may have crashed");
                        continue;
                    }

                    string body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("models", out var modelsArr) ||
                        modelsArr.ValueKind != JsonValueKind.Array ||
                        modelsArr.GetArrayLength() == 0)
                    {
                        _log.Warning(LogSource.Llm, "Ollama /api/ps reports no resident model — request may be stuck");
                        continue;
                    }

                    var first = modelsArr[0];
                    string name = first.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? "?"
                        : "?";
                    long sizeVram = first.TryGetProperty("size_vram", out var vramProp) && vramProp.ValueKind == JsonValueKind.Number
                        ? vramProp.GetInt64()
                        : 0;
                    double vramGb = sizeVram / 1e9;

                    // `expires_at` is Ollama's keep_alive countdown for the
                    // resident model (= 5 min after the last request here).
                    // Rendered as "unloads in Xs" to keep it distinct from
                    // the 15-min REWRITE_HARD_CAP on our side — both are
                    // durations but they mean different things.
                    string unloadSuffix = "";
                    if (first.TryGetProperty("expires_at", out var exp) &&
                        exp.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(exp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expAt))
                    {
                        var rem = expAt.ToUniversalTime() - DateTime.UtcNow;
                        if (rem.TotalSeconds > 0)
                            unloadSuffix = $", unloads in {rem.TotalSeconds:F0}s";
                    }

                    double waitedSeconds = requestElapsed.Elapsed.TotalSeconds;
                    double capMinutes    = REWRITE_HARD_CAP.TotalMinutes;
                    _log.Warning(
                        LogSource.Llm,
                        $"Ollama busy — {name} resident ({vramGb:F1} GB{unloadSuffix}). Waited {waitedSeconds:F0}s so far (giving up at {capMinutes:F0} min).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Warning(LogSource.Llm, $"Ollama /api/ps probe failed: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the caller cancels pollDone as soon as the main request returns.
        }
    }

    /// <summary>
    /// Derives the /api/ps URL from a /api/generate or /api/chat endpoint. If
    /// the endpoint shape is unknown, treat it as the base and append /api/ps.
    /// </summary>
    static string NormalizePsUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        string trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/api/generate".Length] + "/api/ps";
        if (trimmed.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/api/chat".Length] + "/api/ps";
        return trimmed + "/api/ps";
    }

    /// <summary>
    /// Converts a configured endpoint (which may historically point to
    /// /api/chat or already to /api/generate) to /api/generate. Any other
    /// form is left as-is — the user may have an exotic reverse proxy.
    /// </summary>
    static string NormalizeGenerateUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        if (endpoint.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
            return endpoint[..^"/api/chat".Length] + "/api/generate";
        if (endpoint.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        return endpoint;
    }

    /// <summary>
    /// Human-readable format of generation options sent to Ollama. Returns a
    /// string like "temp=0.15 ctx=32768 top_p=0.90 rep=1.10". Only non-null
    /// options are displayed.
    /// </summary>
    static string FormatOptions(Dictionary<string, object>? opts)
    {
        if (opts is null || opts.Count == 0) return "defaults";
        var parts = new List<string>(opts.Count);
        foreach (var kv in opts)
        {
            if (kv.Key == "stop") continue; // noise in logs
            string key = kv.Key switch
            {
                "temperature"    => "temp",
                "num_ctx"        => "ctx",
                "top_p"          => "top_p",
                "repeat_penalty" => "rep",
                _                => kv.Key
            };
            parts.Add($"{key}={kv.Value}");
        }
        return parts.Count == 0 ? "defaults" : string.Join(" ", parts);
    }

    /// <summary>
    /// Extracts and formats metrics returned by Ollama in the /api/generate
    /// response. Same semantics as /api/chat (fields in nanoseconds).
    ///   - total_duration    : total server time
    ///   - load_duration     : model load time (0 if already warm)
    ///   - prompt_eval_count : prompt token count (input)
    ///   - prompt_eval_duration : prompt evaluation time
    ///   - eval_count        : generated token count (output)
    ///   - eval_duration     : generation time (useful for tok/s)
    /// </summary>
    static string FormatMetrics(JsonElement root)
    {
        long total = GetLong(root, "total_duration");
        long load  = GetLong(root, "load_duration");
        long pCnt  = GetLong(root, "prompt_eval_count");
        long pDur  = GetLong(root, "prompt_eval_duration");
        long eCnt  = GetLong(root, "eval_count");
        long eDur  = GetLong(root, "eval_duration");

        double totalMs = total / 1e6;
        double loadMs  = load  / 1e6;
        double pMs     = pDur  / 1e6;
        double eMs     = eDur  / 1e6;

        double pTokPerSec = pDur > 0 ? pCnt * 1e9 / pDur : 0;
        double eTokPerSec = eDur > 0 ? eCnt * 1e9 / eDur : 0;

        return $"metrics: total={totalMs:F0}ms load={loadMs:F0}ms | "
             + $"prompt {pCnt}tok en {pMs:F0}ms ({pTokPerSec:F1} tok/s) | "
             + $"output {eCnt}tok en {eMs:F0}ms ({eTokPerSec:F1} tok/s)";
    }

    static long GetLong(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();
        return 0;
    }

    /// <summary>
    /// Builds the generation options dictionary from the profile's nullable
    /// fields. NumCtxK is stored in K and multiplied by 1024. Family stops
    /// are always added to prevent the model from continuing past its
    /// end-of-turn token.
    /// </summary>
    static Dictionary<string, object>? BuildOptions(RewriteProfile p, string[] stops)
    {
        Dictionary<string, object>? opts = null;

        void Add(string key, object value)
        {
            opts ??= new();
            opts[key] = value;
        }

        if (p.Temperature.HasValue) Add("temperature", p.Temperature.Value);
        if (p.NumCtxK.HasValue)     Add("num_ctx",     p.NumCtxK.Value * 1024);
        if (p.TopP.HasValue)        Add("top_p",       p.TopP.Value);
        if (p.RepeatPenalty.HasValue) Add("repeat_penalty", p.RepeatPenalty.Value);

        if (stops.Length > 0) Add("stop", stops);

        return opts;
    }
}
