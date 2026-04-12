using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhispUI.Logging;
using WhispUI.Settings;

namespace WhispUI;

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
    static readonly HttpClient _http = new();

    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? Rewrite(string text, string endpoint, RewriteProfile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            using var response = _http.PostAsync(generateUrl, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            string responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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
        catch (Exception ex)
        {
            sw.Stop();
            _log.Warning(LogSource.Llm, $"unavailable: {ex.GetType().Name} {ex.Message} — raw text preserved");
            return null;
        }
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
