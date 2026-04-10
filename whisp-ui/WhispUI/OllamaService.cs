using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhispUI;

// ─── Service d'administration Ollama ─────────────────────────────────────────
//
// Wraps les endpoints REST d'Ollama pour la gestion des modèles :
// list, show, create, delete, health-check.
//
// Séparé de LlmService (qui ne fait que du /api/chat pour la réécriture).
// Les deux partagent le même HttpClient statique.
//
// La base URL est dérivée de LlmSettings.OllamaEndpoint en strippant
// le path (/api/chat) pour ne garder que l'origin (http://localhost:11434).

internal sealed class OllamaService
{
    static readonly HttpClient _http = new();

    readonly Func<string> _getEndpoint;

    /// <param name="getEndpoint">
    /// Callback qui retourne l'endpoint courant (ex. "http://localhost:11434/api/chat").
    /// Appelé à chaque requête pour suivre les changements de config.
    /// </param>
    public OllamaService(Func<string> getEndpoint)
    {
        _getEndpoint = getEndpoint;
    }

    // ── API publique ─────────────────────────────────────────────────────────

    /// <summary>Vérifie qu'Ollama est joignable (timeout court).</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{BaseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Liste tous les modèles locaux.</summary>
    public async Task<List<OllamaModel>> ListModelsAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/tags");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OllamaTagsResponse>(json, _jsonOpts);
        return result?.Models ?? new();
    }

    /// <summary>Affiche les détails d'un modèle (Modelfile, template, params).</summary>
    public async Task<OllamaModelInfo?> ShowModelAsync(string name)
    {
        var body = JsonSerializer.Serialize(new { name }, _jsonOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl}/api/show", content);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<OllamaModelInfo>(json, _jsonOpts);
    }

    /// <summary>Crée un modèle à partir d'un contenu Modelfile.</summary>
    /// <exception cref="OllamaApiException">Thrown with the server error message on failure.</exception>
    public async Task CreateModelAsync(string name, string modelfile)
    {
        var body = JsonSerializer.Serialize(new { name, modelfile, stream = false }, _jsonOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var resp = await _http.PostAsync($"{BaseUrl}/api/create", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            string errorMsg = await ExtractErrorAsync(resp)
                              ?? $"HTTP {(int)resp.StatusCode} ({resp.StatusCode})";
            throw new OllamaApiException(errorMsg);
        }
    }

    /// <summary>Supprime un modèle local.</summary>
    public async Task DeleteModelAsync(string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/delete")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { name }, _jsonOpts),
                Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    string BaseUrl
    {
        get
        {
            string endpoint = _getEndpoint();
            // Strip le path pour ne garder que l'origin.
            // "http://localhost:11434/api/chat" → "http://localhost:11434"
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Authority}";
            return "http://localhost:11434";
        }
    }

    /// <summary>
    /// Extrait le champ "error" du body JSON d'une réponse Ollama en erreur.
    /// Retourne null si le body n'est pas exploitable.
    /// </summary>
    static async Task<string?> ExtractErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
                return errProp.GetString();
        }
        catch { }
        return null;
    }

    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed class OllamaTagsResponse
{
    public List<OllamaModel>? Models { get; set; }
}

internal sealed class OllamaModel
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string ModifiedAt { get; set; } = "";
}

internal sealed class OllamaModelInfo
{
    public string Modelfile { get; set; } = "";
    public string Template { get; set; } = "";
    public string System { get; set; } = "";
    public Dictionary<string, string>? Parameters { get; set; }
}

/// <summary>Exception with the error message extracted from the Ollama API response body.</summary>
internal sealed class OllamaApiException : Exception
{
    public OllamaApiException(string message) : base(message) { }
}
