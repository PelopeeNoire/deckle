using System.Net.Http;
using System.Text;
using System.Text.Json;

// ─── Service de réécriture LLM via Ollama ─────────────────────────────────────
//
// Appel POST bloquant vers l'API locale d'Ollama.
// Conçu pour être appelé depuis un thread de fond — .GetAwaiter().GetResult()
// est sûr ici (pas de contexte de synchronisation WinForms sur ce thread).
//
// En cas d'erreur (Ollama absent, timeout, etc.), retourne null et notifie
// l'appelant via le callback _onError. L'appelant décide quoi en faire
// (mise à jour tray, log, etc.) — LlmService ne dépend pas de WinForms.

class LlmService
{
    const string OLLAMA_MODEL = "ministral-3:3b--instruct--96k";
    const string OLLAMA_URL   = "http://localhost:11434/api/chat";  // /api/chat : messages structurés (system + user)

    // Instructions envoyées explicitement dans chaque requête (role "system").
    // Plus robuste que le Modelfile Ollama : Ollama détecte mal le TEMPLATE des GGUF locaux,
    // ce qui peut faire ignorer le system prompt du Modelfile. /api/chat contourne ce problème.
    const string SYSTEM_PROMPT =
        "Tu reçois une transcription vocale brute en français. Réécris-la en texte fluide " +
        "et cohérent : corrige les erreurs de transcription, les répétitions, les sauts de " +
        "phrases, la syntaxe orale. Conserve le sens exact, tous les points abordés et le " +
        "registre de la personne. Ne résume pas. Ne commente pas. Ne reformule pas avec moins " +
        "d'informations. Ne commence pas ta réponse par 'Voici', un titre ou une introduction. " +
        "Ta réponse commence directement par la première phrase du texte réécrit.";

    // Une seule instance partagée — HttpClient est thread-safe et conçu pour être réutilisé.
    static readonly HttpClient _http = new();

    // _onError : appelé avec un message court si l'appel échoue.
    // Appelé sur le thread de fond — l'appelant est responsable du marshaling UI si nécessaire.
    readonly Action<string>? _onError;

    public LlmService(Action<string>? onError = null)
    {
        _onError = onError;
    }

    // Rewrite : envoie text à Ollama et retourne le texte réécrit.
    // Retourne null si Ollama est indisponible ou si la réponse est vide.
    public string? Rewrite(string text)
    {
        try
        {
            // Corps de la requête Ollama /api/chat
            // messages : tableau de rôles (system + user). Ollama applique le bon template
            //            de prompt quelle que soit la détection automatique du GGUF.
            // stream: false    → Ollama attend la fin de la génération avant de répondre
            // keep_alive: "5m" → le modèle reste en VRAM 5 minutes, puis Ollama le vide
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

            // StringContent : enveloppe la chaîne JSON dans un objet que HttpClient peut envoyer.
            // Encoding.UTF8 + "application/json" : en-tête Content-Type attendu par Ollama.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // PostAsync bloquant. Timeout par défaut de HttpClient = 100 secondes.
            using var response = _http.PostAsync(OLLAMA_URL, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            string responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // /api/chat retourne { "message": { "role": "assistant", "content": "..." }, ... }
            using var doc = JsonDocument.Parse(responseJson);
            string? rewritten = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return rewritten?.Trim();
        }
        catch
        {
            _onError?.Invoke("LLM indisponible");
            return null;
        }
    }
}
