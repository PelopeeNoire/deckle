using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhispUI.Llm;

// ─── Service d'administration Ollama ─────────────────────────────────────────
//
// Wraps les endpoints REST d'Ollama pour la gestion des modèles :
// list, show, create (structured API), delete, health-check, blob push.
//
// Séparé de LlmService (qui ne fait que du /api/chat pour la réécriture).
//
// La base URL est dérivée de LlmSettings.OllamaEndpoint en strippant
// le path (/api/chat) pour ne garder que l'origin (http://localhost:11434).

internal sealed class OllamaService
{
    // Timeout généreux : le push de blobs volumineux (GGUF 3-9 Go) peut
    // prendre du temps, même en localhost. Les appels rapides utilisent
    // leur propre CancellationTokenSource pour un timeout plus court.
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    readonly Func<string> _getEndpoint;

    /// <param name="getEndpoint">
    /// Callback qui retourne l'endpoint courant (ex. "http://localhost:11434/api/chat").
    /// Appelé à chaque requête pour suivre les changements de config.
    /// </param>
    public OllamaService(Func<string> getEndpoint)
    {
        _getEndpoint = getEndpoint;
    }

    // ── Health check ────────────────────────────────────────────────────────

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

    // ── Model listing ───────────────────────────────────────────────────────

    /// <summary>Liste tous les modèles locaux.</summary>
    public async Task<List<OllamaModel>> ListModelsAsync()
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/tags");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OllamaTagsResponse>(json, _jsonOpts);
        return result?.Models ?? new();
    }

    // ── Model details ───────────────────────────────────────────────────────

    /// <summary>Affiche les détails d'un modèle (template, system, params).</summary>
    public async Task<OllamaModelInfo?> ShowModelAsync(string name)
    {
        var body = JsonSerializer.Serialize(new OllamaModelRequest { Model = name }, _jsonOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl}/api/show", content);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<OllamaModelInfo>(json, _jsonOpts);
    }

    // ── Model deletion ──────────────────────────────────────────────────────

    /// <summary>Supprime un modèle local.</summary>
    public async Task DeleteModelAsync(string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/delete")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new OllamaModelRequest { Model = name }, _jsonOpts),
                Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    // ── GGUF import (orchestrateur) ─────────────────────────────────────────
    //
    // Flow complet : hash SHA-256 → check blob → push blob → create model.
    // Le progress reporte (Status, Percent) où Percent ∈ [0,1] pour les
    // étapes déterminées, et -1 pour les étapes indéterminées.

    public async Task ImportGgufAsync(
        string modelName,
        string ggufPath,
        string? template,
        string? system,
        IProgress<(string Status, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Compute SHA-256
        progress?.Report(("Computing file hash...", 0));
        string digest = await ComputeFileDigestAsync(ggufPath,
            new Progress<double>(p => progress?.Report(("Computing file hash...", p))), ct);

        // 2. Check if blob already exists
        progress?.Report(("Checking existing data...", -1));
        bool exists = await BlobExistsAsync(digest);

        // 3. Push blob if needed
        if (!exists)
        {
            progress?.Report(("Uploading to Ollama...", -1));
            await PushBlobAsync(ggufPath, digest, ct);
        }

        // 4. Create model
        progress?.Report(("Creating model...", -1));
        string fileName = Path.GetFileName(ggufPath);
        var files = new Dictionary<string, string> { [fileName] = digest };
        await CreateModelAsync(modelName, files, template, system, ct);

        progress?.Report(("Done.", 1));
    }

    // ── File digest ─────────────────────────────────────────────────────────

    /// <summary>Calcule le SHA-256 d'un fichier avec report de progression.</summary>
    public static async Task<string> ComputeFileDigestAsync(
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[1024 * 1024]; // 1 Mo — bon compromis I/O disque
        long total = stream.Length;
        long processed = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            processed += read;
            progress?.Report((double)processed / total);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return "sha256:" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ── Blob management ─────────────────────────────────────────────────────

    /// <summary>Vérifie si un blob existe déjà sur le serveur Ollama.</summary>
    public async Task<bool> BlobExistsAsync(string digest)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, $"{BaseUrl}/api/blobs/{digest}");
            using var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Push un fichier comme blob vers Ollama.</summary>
    public async Task PushBlobAsync(string filePath, string digest, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(filePath);
        using var content = new StreamContent(stream, bufferSize: 1024 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await _http.PostAsync($"{BaseUrl}/api/blobs/{digest}", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string errorMsg = await ExtractErrorAsync(resp)
                              ?? $"HTTP {(int)resp.StatusCode} ({resp.StatusCode})";
            throw new OllamaApiException($"Blob upload failed: {errorMsg}");
        }
    }

    // ── Model creation (API structurée) ─────────────────────────────────────

    /// <summary>Crée un modèle via l'API structurée /api/create.</summary>
    public async Task CreateModelAsync(
        string modelName,
        Dictionary<string, string> files,
        string? template = null,
        string? system = null,
        CancellationToken ct = default)
    {
        // Dictionnaire sérialisé manuellement pour contrôler les clés exactes
        // (le PropertyNamingPolicy ne s'applique pas aux clés de dictionnaire).
        var payload = new Dictionary<string, object> { ["model"] = modelName };

        if (files.Count > 0)
            payload["files"] = files;
        if (!string.IsNullOrWhiteSpace(template))
            payload["template"] = template;
        if (!string.IsNullOrWhiteSpace(system))
            payload["system"] = system;
        payload["stream"] = false;

        var body = JsonSerializer.Serialize(payload, _jsonCreateOpts);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{BaseUrl}/api/create", content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string errorMsg = await ExtractErrorAsync(resp)
                              ?? $"HTTP {(int)resp.StatusCode} ({resp.StatusCode})";
            throw new OllamaApiException(errorMsg);
        }
    }

    // ── Interne ─────────────────────────────────────────────────────────────

    string BaseUrl
    {
        get
        {
            string endpoint = _getEndpoint();
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Authority}";
            return "http://localhost:11434";
        }
    }

    /// <summary>
    /// Extrait le champ "error" du body JSON d'une réponse Ollama en erreur.
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

    // Options pour les endpoints qui sérialisent des DTOs typés (list, show, delete).
    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Options pour /api/create — pas de naming policy, les clés du dictionnaire
    // sont déjà en snake_case et doivent rester intactes.
    static readonly JsonSerializerOptions _jsonCreateOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

// Requête générique avec champ "model" (API Ollama actuelle).
internal sealed class OllamaModelRequest
{
    public string Model { get; set; } = "";
}

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

/// <summary>Exception avec le message d'erreur extrait de la réponse API Ollama.</summary>
internal sealed class OllamaApiException : Exception
{
    public OllamaApiException(string message) : base(message) { }
}
