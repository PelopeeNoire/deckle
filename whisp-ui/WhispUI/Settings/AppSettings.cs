namespace WhispUI.Settings;

// ── AppSettings ───────────────────────────────────────────────────────────────
//
// POCO racine sérialisé en JSON vers ./config/settings.json (à côté de l'exe).
// Organisé par intention utilisateur, pas par groupes techniques de whisper.cpp.
// Les défauts sont ceux du rapport de cartographie des paramètres Whisper.
//
// Toute modification passe par SettingsService (Load/Save, debounced, Changed).
public sealed class AppSettings
{
    public PathsSettings Paths { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public SpeechDetectionSettings SpeechDetection { get; set; } = new();
    public ConfidenceSettings Confidence { get; set; } = new();
    public OutputFilterSettings OutputFilters { get; set; } = new();
    public DecodingSettings Decoding { get; set; } = new();
    public ContextSettings Context { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
}

// Paramètres d'enregistrement audio. AudioInputDeviceId = index du périphérique
// waveIn à utiliser. -1 = WAVE_MAPPER (périphérique par défaut du système).
public sealed class RecordingSettings
{
    public int AudioInputDeviceId { get; set; } = -1;
}

// Apparence globale. Theme = "System" | "Light" | "Dark".
public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "System";
}

// Comportement au démarrage.
public sealed class StartupSettings
{
    public bool StartMinimized { get; set; } = true;
}

// Overlay HUD affiché pendant l'enregistrement/transcription.
// Position = "BottomCenter" | "BottomRight" | "TopCenter".
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool FadeOnProximity { get; set; } = true;
    public string Position { get; set; } = "BottomCenter";
}

// Chemins utilisateur. ModelsDirectory = dossier où vivent les .bin (Whisper
// large-v3, base, Silero VAD...). Vide = résolution automatique vers
// `../../shared` relatif à l'exe (disposition historique du dépôt).
public sealed class PathsSettings
{
    public string ModelsDirectory { get; set; } = "";
}

// Choix fondamentaux du moteur. Les 3 premiers (Model / UseGpu / Language) sont
// des paramètres « lourds » — ils demandent un reload du contexte whisper.cpp.
public sealed class TranscriptionSettings
{
    public string Model { get; set; } = "ggml-large-v3.bin";
    public bool UseGpu { get; set; } = true;
    public string Language { get; set; } = "fr";
    public string InitialPrompt { get; set; } = "Transcription en français.";
}

// VAD Silero — pré-filtre qui détecte les segments de parole avant Whisper.
// Quand Enabled=false, tous les autres champs sont ignorés par l'engine.
public sealed class SpeechDetectionSettings
{
    public bool Enabled { get; set; } = true;
    public float Threshold { get; set; } = 0.5f;
    public int MinSpeechDurationMs { get; set; } = 250;
    public int MinSilenceDurationMs { get; set; } = 500;
    public float MaxSpeechDurationSec { get; set; } = 30.0f;
    public int SpeechPadMs { get; set; } = 200;
    public float SamplesOverlap { get; set; } = 0.1f;
}

// Seuils qui déclenchent le fallback température de whisper.cpp. Stockés en
// double pour l'UI (NumberBox/Slider WinUI travaillent en double) ; cast en
// float au moment du mapping vers la struct native.
public sealed class ConfidenceSettings
{
    public double EntropyThreshold { get; set; } = 2.4;
    public double LogprobThreshold { get; set; } = -1.0;
    public double NoSpeechThreshold { get; set; } = 0.6;
}

public sealed class OutputFilterSettings
{
    public bool SuppressNonSpeechTokens { get; set; } = true;
    public bool SuppressBlank { get; set; } = true;
    public string SuppressRegex { get; set; } = "";
}

public sealed class DecodingSettings
{
    public double Temperature { get; set; } = 0.0;
    public double TemperatureIncrement { get; set; } = 0.2;
}

// ── Réécriture LLM via Ollama ────────────────────────────────────────────────

// Profil de réécriture : modèle Ollama, system prompt, paramètres de génération.
// Le system prompt est envoyé per-request (pas via Modelfile) — les modèles
// viennent de HuggingFace en GGUF et Ollama ne détecte pas bien les TEMPLATE.
// Les paramètres de génération (nullable) sont envoyés dans le champ `options`
// de /api/chat et overrident les defaults du Modelfile côté Ollama.
public sealed class RewriteProfile
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "";

    // Paramètres de génération — null = default Ollama (pas envoyé).
    public double? Temperature { get; set; }
    public int? NumCtxK { get; set; }            // en K (×1024 à l'envoi)
    public double? TopP { get; set; }
    public double? RepeatPenalty { get; set; }
}

// Règle d'auto-réécriture : quand la durée d'enregistrement dépasse
// MinDurationSeconds, le profil ProfileName est utilisé. Les règles sont
// évaluées dans l'ordre décroissant de MinDurationSeconds (la plus longue
// qui matche gagne).
public sealed class AutoRewriteRule
{
    public int MinDurationSeconds { get; set; } = 0;
    public string ProfileName { get; set; } = "";
}

public sealed class LlmSettings
{
    public bool Enabled { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/api/generate";

    // Profil utilisé par le raccourci manuel (Alt+Ctrl+`).
    public string ManualProfileName { get; set; } = "Prompt";

    // Bloc anti-préambule partagé par tous les profils par défaut. Répété en
    // tête de chaque system prompt parce que les modèles instruct sont
    // entraînés à saluer/introduire — sans cette contrainte explicite et
    // répétée, ils préfixent systématiquement la sortie par "Voici", "Bien
    // sûr", "La transcription corrigée est :", ou encadrent tout par des
    // guillemets ou des backticks.
    private const string AntiPreamble =
        "SORTIE : texte brut uniquement. Pas d'introduction, pas de méta-commentaire, " +
        "pas de balise, pas de guillemets ni de backticks englobants, pas de phrase " +
        "d'acquiescement. Interdit : 'Voici', 'Bien sûr', 'D'accord', 'Je vais', " +
        "'La transcription corrigée est', 'En voici une version'. Ta toute première " +
        "sortie doit être directement le premier mot du texte demandé. Ta dernière " +
        "sortie doit être le dernier mot du texte demandé.\n\n";

    public List<RewriteProfile> Profiles { get; set; } = new()
    {
        new()
        {
            Name = "Nettoyage",
            Model = "",
            Temperature = 0.15,
            NumCtxK = 2,
            SystemPrompt = AntiPreamble +
                "Tu reçois une transcription vocale brute en français. Nettoie-la minimalement : " +
                "corrige la ponctuation, les accents, les mots mal transcrits évidents et les " +
                "répétitions immédiates. Ne restructure pas. Ne reformule pas les phrases. " +
                "Conserve l'ordre, le registre, le vocabulaire et tous les éléments. Ne " +
                "supprime rien et n'ajoute rien."
        },
        new()
        {
            Name = "Restructuration",
            Model = "",
            Temperature = 0.20,
            NumCtxK = 2,
            SystemPrompt = AntiPreamble +
                "Tu reçois une transcription vocale brute en français, potentiellement longue. " +
                "Réécris-la en texte structuré et cohérent : regroupe les idées liées, fais des " +
                "paragraphes, élimine les répétitions orales, corrige la syntaxe. Tu peux " +
                "réordonner pour plus de clarté. MAIS tu dois impérativement conserver TOUS les " +
                "concepts, notions, exemples et détails mentionnés — rien ne doit être perdu. " +
                "Ne résume pas. Ne commente pas."
        },
        new()
        {
            Name = "Prompt",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 2,
            SystemPrompt = AntiPreamble +
                "Tu reçois une transcription vocale brute en français où la personne exprime une " +
                "demande, une réflexion ou un besoin qu'elle veut formuler comme prompt pour un " +
                "LLM. Réécris-la en prompt structuré et qualitatif : identifie la demande " +
                "centrale, organise les contraintes et le contexte en points clairs, explicite " +
                "ce qui est implicite dans le discours oral. Si la personne mentionne des " +
                "attentes de format (listes, code, etc.), intègre-les. Élimine le superflu oral " +
                "mais conserve toutes les nuances et tous les points évoqués."
        }
    };

    public List<AutoRewriteRule> AutoRewriteRules { get; set; } = new()
    {
        new() { MinDurationSeconds = 90, ProfileName = "Restructuration" },
        new() { MinDurationSeconds = 30, ProfileName = "Nettoyage" }
    };
}

// UseContext = inverse de no_context natif (vocabulaire orienté utilisateur).
// MaxTokens = -1 signifie « auto / illimité ».
public sealed class ContextSettings
{
    public bool UseContext { get; set; } = true;
    public int MaxTokens { get; set; } = -1;
}
