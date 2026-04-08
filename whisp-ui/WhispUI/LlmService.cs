using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WhispUI;

// ─── Service de réécriture LLM via Ollama ─────────────────────────────────────
//
// Appel POST bloquant vers l'API locale d'Ollama.
// Conçu pour être appelé depuis un thread de fond — .GetAwaiter().GetResult()
// est sûr ici (pas de contexte de synchronisation sur ce thread).
//
// En cas d'erreur (Ollama absent, timeout, etc.), retourne null et notifie
// l'appelant via le callback _onError.

internal class LlmService
{
    const string OLLAMA_MODEL = "ministral-3:3b--instruct--96k";
    const string OLLAMA_URL   = "http://localhost:11434/api/chat";

    const string SYSTEM_PROMPT =
        "Tu reçois une transcription vocale brute en français. Réécris-la en texte fluide " +
        "et cohérent : corrige les erreurs de transcription, les répétitions, les sauts de " +
        "phrases, la syntaxe orale. Conserve le sens exact, tous les points abordés et le " +
        "registre de la personne. Ne résume pas. Ne commente pas. Ne reformule pas avec moins " +
        "d'informations. Ne commence pas ta réponse par 'Voici', un titre ou une introduction. " +
        "Ta réponse commence directement par la première phrase du texte réécrit.";

    static readonly HttpClient _http = new();

    readonly Action<string>? _onWarn;
    readonly Action<string>? _onStep;
    readonly Action<string>? _onInfo;

    public LlmService(Action<string>? onWarn = null, Action<string>? onStep = null, Action<string>? onInfo = null)
    {
        _onWarn = onWarn;
        _onStep = onStep;
        _onInfo = onInfo;
    }

    public string? Rewrite(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _onInfo?.Invoke($"[LLM] requête {text.Length} chars → {OLLAMA_MODEL}");

            var body = new
            {
                model      = OLLAMA_MODEL,
                stream     = false,
                keep_alive = "5m",
                messages   = new[]
                {
                    new { role = "system", content = SYSTEM_PROMPT },
                    new { role = "user",   content = text }
                }
            };

            string json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = _http.PostAsync(OLLAMA_URL, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            string responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(responseJson);
            string? rewritten = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            sw.Stop();
            string trimmed = rewritten?.Trim() ?? "";
            _onStep?.Invoke($"Réécriture OK ({sw.ElapsedMilliseconds} ms, {text.Length}→{trimmed.Length} chars)");
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
