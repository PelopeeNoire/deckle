using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhispUI.Settings;

namespace WhispUI;

// ─── Service de réécriture LLM via Ollama (mode RAW) ──────────────────────────
//
// Appelle /api/generate avec raw=true et un prompt pré-formaté côté client.
// On contourne complètement le système TEMPLATE d'Ollama parce que les modèles
// importés depuis HuggingFace en GGUF arrivent souvent avec un Modelfile
// générique (TEMPLATE {{ .Prompt }}), ce qui casse silencieusement le format
// d'entrée attendu par le modèle — symptôme typique : le modèle produit du
// charabia, des échos ou boucle.
//
// Le template est déterminé côté client à partir du nom du modèle (famille
// Mistral/Llama/Qwen/Gemma/Phi/ChatML), appliqué manuellement, et envoyé via
// raw=true — Ollama ne touche pas au prompt.
//
// Conçu pour être appelé depuis un thread de fond — .GetAwaiter().GetResult()
// est sûr ici (pas de contexte de synchronisation sur ce thread).

internal class LlmService
{
    static readonly HttpClient _http = new();

    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly Action<string>? _onWarn;
    readonly Action<string>? _onInfo;

    public LlmService(Action<string>? onWarn = null, Action<string>? onInfo = null)
    {
        _onWarn = onWarn;
        _onInfo = onInfo;
    }

    public string? Rewrite(string text, string endpoint, RewriteProfile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (prompt, stops, family) = PromptTemplates.Build(profile.Model, profile.SystemPrompt, text);
            string generateUrl = NormalizeGenerateUrl(endpoint);
            var options = BuildOptions(profile, stops);

            _onInfo?.Invoke($"[LLM] requête {text.Length} chars → {profile.Model} (profil: {profile.Name}, famille: {family}) | {FormatOptions(options)}");

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
            _onInfo?.Invoke($"[LLM] Réécriture OK ({sw.ElapsedMilliseconds} ms, {text.Length}→{trimmed.Length} chars, profil: {profile.Name})");
            _onInfo?.Invoke($"[LLM] {FormatMetrics(doc.RootElement)}");
            return trimmed;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _onWarn?.Invoke($"LLM indisponible : {ex.GetType().Name} {ex.Message} — texte brut conservé");
            return null;
        }
    }

    /// <summary>
    /// Convertit un endpoint configuré (qui peut pointer historiquement sur
    /// /api/chat ou déjà sur /api/generate) vers /api/generate. Toute autre
    /// forme est laissée telle quelle — l'utilisateur a peut-être un reverse
    /// proxy exotique.
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
    /// Format lisible des options génération envoyées à Ollama. Retourne une
    /// chaîne du type "temp=0.15 ctx=32768 top_p=0.90 rep=1.10". Seules les
    /// options non-null sont affichées.
    /// </summary>
    static string FormatOptions(Dictionary<string, object>? opts)
    {
        if (opts is null || opts.Count == 0) return "defaults";
        var parts = new List<string>(opts.Count);
        foreach (var kv in opts)
        {
            if (kv.Key == "stop") continue; // bruit dans les logs
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
    /// Extrait et formate les métriques retournées par Ollama dans la réponse
    /// /api/generate. Même sémantique que /api/chat (champs en nanosecondes).
    ///   - total_duration    : temps total serveur
    ///   - load_duration     : temps de chargement modèle (0 si déjà chaud)
    ///   - prompt_eval_count : nb tokens du prompt (entrée)
    ///   - prompt_eval_duration : temps d'évaluation du prompt
    ///   - eval_count        : nb tokens générés (sortie)
    ///   - eval_duration     : temps de génération (utile pour tok/s)
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
    /// Construit le dictionnaire d'options de génération à partir des champs
    /// nullable du profil. NumCtxK est stocké en K et multiplié par 1024.
    /// Les stops de la famille sont ajoutés systématiquement pour éviter que
    /// le modèle continue après son token de fin-de-tour.
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
