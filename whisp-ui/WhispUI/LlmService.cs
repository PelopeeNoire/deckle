using System.Net.Http;
using System.Text;
using System.Text.Json;
using WhispUI.Settings;

namespace WhispUI;

// ─── Service de réécriture LLM via Ollama ─────────────────────────────────────
//
// Appel POST bloquant vers l'API locale d'Ollama.
// Conçu pour être appelé depuis un thread de fond — .GetAwaiter().GetResult()
// est sûr ici (pas de contexte de synchronisation sur ce thread).
//
// Le modèle et le system prompt sont déterminés par le RewriteProfile passé
// par l'appelant. L'endpoint est lu depuis LlmSettings.
//
// En cas d'erreur (Ollama absent, timeout, etc.), retourne null et notifie
// l'appelant via le callback _onWarn.

internal class LlmService
{
    static readonly HttpClient _http = new();

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
            _onInfo?.Invoke($"[LLM] requête {text.Length} chars → {profile.Model} (profil: {profile.Name})");

            var body = new
            {
                model      = profile.Model,
                stream     = false,
                keep_alive = "5m",
                messages   = new[]
                {
                    new { role = "system", content = profile.SystemPrompt },
                    new { role = "user",   content = text }
                }
            };

            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = _http.PostAsync(endpoint, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            string responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(responseJson);
            string? rewritten = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            sw.Stop();
            string trimmed = rewritten?.Trim() ?? "";
            _onInfo?.Invoke($"[LLM] Réécriture OK ({sw.ElapsedMilliseconds} ms, {text.Length}→{trimmed.Length} chars, profil: {profile.Name})");
            return trimmed;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _onWarn?.Invoke($"LLM indisponible : {ex.GetType().Name} {ex.Message} — texte brut conservé");
            return null;
        }
    }
}
