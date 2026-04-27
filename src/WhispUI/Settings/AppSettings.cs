namespace WhispUI.Settings;

// ── AppSettings ───────────────────────────────────────────────────────────────
//
// POCO racine sérialisé en JSON vers <AppPaths.ConfigDirectory>/settings.json
// (à côté de l'exe en dev unpackaged, sous LocalState en packagé MSIX).
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
    public TelemetrySettings Telemetry { get; set; } = new();
    public PasteSettings Paste { get; set; } = new();
}

// Auto-paste after transcription. Off by default — the clipboard is the safe
// default and the user explicitly opts in to SendInput-driven paste. When
// false, the engine skips PasteFromClipboard entirely and the HUD shows the
// "Copied to clipboard" message instead of "Pasted".
public sealed class PasteSettings
{
    public bool AutoPasteEnabled { get; set; } = false;
}

// Diagnostics / telemetry: four independent opt-in streams, all off by
// default — confidentiality first. The user explicitly authorizes each
// data class through its own consent dialog before anything lands on disk.
//
// LatencyEnabled controls the per-transcription latency JSONL (vad/whisper/
// llm/clipboard/paste timings, outcome). Lightweight, timings only, no user
// text — the closest equivalent to the legacy telemetry.csv.
//
// CorpusEnabled controls the raw Whisper text capture — one JSONL per
// rewrite profile, with the audio section metadata and the exact raw
// transcription. Stronger privacy posture (the user's words land on disk).
//
// RecordAudioCorpus is a nested opt-in that additionally saves the raw
// 16 kHz mono PCM audio as a .wav per transcription, alongside the text
// JSONL. Meaningless unless CorpusEnabled is also true. Audio carries the
// strongest posture (biometric-adjacent).
//
// ApplicationLogToDisk mirrors the in-process LogService stream to a JSONL
// file on disk. All log levels (Verbose → Error), all subsystems. Useful to
// diagnose a specific issue across a restart, noisy in steady-state — so
// opt-in with line-based rotation to cap disk footprint.
//
// MicrophoneTelemetry adds a per-Recording RMS distribution summary
// (min / p10 / p25 / p50 / p75 / p90 / max in dBFS + linear mean RMS)
// to the LogWindow AND to a dedicated <telemetry>/microphone.jsonl file.
// Calibration aid for tuning the HUD level window (MinDbfs / MaxDbfs /
// DbfsCurveExponent) against the user's actual mic+DSP chain instead of
// textbook conversational levels. Off by default — the line is dense
// enough to clutter the All filter for users who aren't calibrating.
//
// StorageDirectory is the common root for latency.jsonl / app.jsonl /
// microphone.jsonl / <profile-slug>/corpus.jsonl. Empty = default resolver
// (<repo>/benchmark/telemetry/ when running from the dev tree).
public sealed class TelemetrySettings
{
    public bool   LatencyEnabled       { get; set; } = false;
    public bool   CorpusEnabled        { get; set; } = false;
    public bool   RecordAudioCorpus    { get; set; } = false;
    public bool   ApplicationLogToDisk { get; set; } = false;
    public bool   MicrophoneTelemetry  { get; set; } = false;
    public string StorageDirectory     { get; set; } = "";
}

// Paramètres d'enregistrement audio. AudioInputDeviceId = index du périphérique
// waveIn à utiliser. -1 = WAVE_MAPPER (périphérique par défaut du système).
public sealed class RecordingSettings
{
    public int AudioInputDeviceId { get; set; } = -1;

    // Hard cap on a single recording's duration, in seconds. When the capture
    // loop crosses this threshold, recording auto-stops as if the user had
    // pressed Stop — the captured audio still goes through the full
    // VAD → Whisper → (LLM) → paste pipeline. Prevents a forgotten hotkey
    // from running for hours and hitting a Whisper hallucination loop or
    // running out of RAM. 0 = no cap (legacy behaviour).
    public int MaxRecordingDurationSeconds { get; set; } = 20 * 60;

    public LevelWindowSettings LevelWindow { get; set; } = new();
}

// Persisted dBFS window the HUD chrono uses to map raw microphone RMS
// onto the [0, 1] perceptual level driving the Recording stroke. Exposed
// as Settings so the user can calibrate against their own mic+DSP chain
// without rebuilding — the values land in HudChrono.Min/MaxDbfs +
// DbfsCurveExponent statics at app startup (and on every change).
//
// Defaults match the shipping calibration:
//   Min  -55 dBFS — below typical p25 silence band, well above the
//                   -97 dBFS digital floor / DSP gate.
//   Max  -32 dBFS — measured peak ceiling for normal voice.
//   Exp   1.0    — linear response. Higher = compress low end / expand
//                  high end; lower = the opposite. The HUD reads "soit
//                  là, soit pas là" with a linear ramp.
//
// AutoCalibration runs a rolling heuristic over the last N
// `microphone.jsonl` rows: median(p10) → MinDbfs, median(p90 + 2 dB
// headroom) → MaxDbfs. Off by default — the user has to opt-in to a
// recurring tweak of their visual feedback. Triggers on every Recording
// once SamplesNeeded rows are available; the very first run after enable
// happens silently — the HUD just snaps to the new window on the next
// Recording. Manual sliders override the auto values until auto runs
// again.
public sealed class LevelWindowSettings
{
    public float MinDbfs                = -55f;
    public float MaxDbfs                = -32f;
    public float DbfsCurveExponent      = 1.0f;
    public bool  AutoCalibrationEnabled = false;
    public int   AutoCalibrationSamples = 5;
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

    // Run a silent dummy transcription at launch to warm up the Whisper model
    // (load + first inference pay the heavy cost). The real first hotkey press
    // then skips the cold start. Hidden from HUD and tray — pure background.
    public bool WarmupOnLaunch { get; set; } = true;
}

// Overlay HUD affiché pendant l'enregistrement/transcription.
// Position = "BottomCenter" | "BottomRight" | "TopCenter".
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool FadeOnProximity { get; set; } = true;
    public string Position { get; set; } = "BottomCenter";

    // Enables the 150 ms slide + fade transitions on the HUD and overlay
    // message cards. On by default — unlike chrome animations, message
    // transitions are critical for the user to track what just replaced what,
    // so we ignore SPI_GETCLIENTAREAANIMATION and only consult this toggle.
    // Windows itself does the same for load-bearing motion (Task Manager pane,
    // Settings NavigationView) when reduced-motion is enabled globally.
    public bool Animations { get; set; } = true;
}

// Chemins utilisateur. Tous vides par défaut → résolution automatique via
// AppPaths (à côté de l'exe en dev unpackaged, sous LocalState en packagé MSIX).
//
// ModelsDirectory  : dossier des .bin Whisper (large-v3, base, Silero VAD).
// BackupDirectory  : dossier où SettingsBackupService dépose les snapshots
//                    settings-YYYYMMDD-HHmmss.json. Pattern PowerToys :
//                    le user peut pointer vers un dossier OneDrive/Drive
//                    pour faire suivre ses backups entre machines.
public sealed class PathsSettings
{
    public string ModelsDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
}

// Choix fondamentaux du moteur. Les 3 premiers (Model / UseGpu / Language) sont
// des paramètres « lourds » — ils demandent un reload du contexte whisper.cpp.
public sealed class TranscriptionSettings
{
    public string Model { get; set; } = "ggml-large-v3.bin";
    public bool UseGpu { get; set; } = true;
    public string Language { get; set; } = "fr";
    public string InitialPrompt { get; set; } =
        "Bon. Je suis sur WhispUI, je continue le HUD de l'application avec le contour animé qui tourne. " +
        "C'est quand même propre, même si la fenêtre reste fragile. " +
        "Côté workflow, je gère plusieurs worktrees, je merge les branches sur main, je lance le benchmark à chaque itération. " +
        "Côté outils, Ollama, Ministral, Anytype, Continue.dev, MCP, LLM. " +
        "Ouais, c'est pas mal, même si parfois j'ai un truc fichu et il faut tout reprendre. " +
        "Voilà. Ok.";

    // Prepend initial_prompt to every 30s decode window (not just the first).
    // Stabilizes punctuation and register across long recordings.
    public bool CarryInitialPrompt { get; set; } = true;
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

    // Beam search explores multiple hypotheses in parallel, picking the
    // best sequence overall. Higher quality than greedy at the cost of
    // latency. BeamSize only used when UseBeamSearch is true.
    public bool UseBeamSearch { get; set; } = true;
    public int BeamSize { get; set; } = 5;
}

// ── Réécriture LLM via Ollama ────────────────────────────────────────────────

// Profil de réécriture : modèle Ollama, system prompt, paramètres de génération.
// Le system prompt est envoyé per-request (pas via Modelfile) — les modèles
// viennent de HuggingFace en GGUF et Ollama ne détecte pas bien les TEMPLATE.
// Les paramètres de génération (nullable) sont envoyés dans le champ `options`
// de /api/chat et overrident les defaults du Modelfile côté Ollama.
public sealed class RewriteProfile
{
    // Stable identifier across renames. 12 hex chars (Guid N format truncated).
    // Generated on first load for legacy profiles by SettingsService.MigrateProfileIds.
    // Used as the join key for corpus telemetry — survives a user renaming Name.
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "";

    // Paramètres de génération — null = default Ollama (pas envoyé).
    public double? Temperature { get; set; }
    public int? NumCtxK { get; set; }            // en K (×1024 à l'envoi)
    public double? TopP { get; set; }
    public double? RepeatPenalty { get; set; }
}

// Règle d'auto-réécriture (durée) : quand la durée d'enregistrement dépasse
// MinDurationSeconds, le profil ProfileName est utilisé. Les règles sont
// évaluées dans l'ordre décroissant de MinDurationSeconds (la plus longue
// qui matche gagne).
public sealed class AutoRewriteRule
{
    public int MinDurationSeconds { get; set; } = 0;

    // Stable reference to RewriteProfile.Id. Preferred over ProfileName for
    // lookup; ProfileName is kept so legacy configs keep resolving during
    // migration and so the JSON stays human-readable.
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
}

// Mirror of AutoRewriteRule keyed on word count instead of duration. Words
// reflect LLM context load more faithfully than recording time (a slow 10-min
// dictation does not cost the same as a rapid-fire one). Evaluated descending
// by MinWordCount, same rule as the duration list.
public sealed class AutoRewriteRuleByWords
{
    public int MinWordCount { get; set; } = 0;
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
}

public sealed class LlmSettings
{
    public bool Enabled { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/api/generate";

    // Profile used by the Primary Rewrite shortcut (Shift+Win+`).
    public string PrimaryRewriteProfileName { get; set; } = "Prompt";

    // Profile used by the Secondary Rewrite shortcut (Ctrl+Win+`).
    // null = secondary rewrite disabled (hotkey fires but rewriting is skipped).
    public string? SecondaryRewriteProfileName { get; set; }

    // Stable companions to the *ProfileName* fields above — resolved to
    // RewriteProfile.Id. Lookup at runtime prefers Id, falls back to Name
    // for legacy configs. Filled by SettingsService.MigrateProfileIds.
    public string? PrimaryRewriteProfileId { get; set; }
    public string? SecondaryRewriteProfileId { get; set; }

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

    // Quatre profils alignés sur les brackets de cleanup (lib/corpus.py:38-47),
    // tunés par autoresearch nuit 2026-04-25/26 sur Ministral 14B Q4 local
    // (branche autoresearch/llm-rewrite-nettoyage-20260425). Gradient strict
    // d'intervention : relecture (surface) → lissage (disfluences) → affinage
    // (oral → écrit) → arrangement (regroupement thématique). Règle commune :
    // aucune perte de mots, de sens, de nuances. Profil "Prompt" préservé pour
    // le shortcut Primary Rewrite (Shift+Win+`) — usage différent (dictée d'un
    // brief LLM), pas un bracket de durée.
    public List<RewriteProfile> Profiles { get; set; } = new()
    {
        new()
        {
            Name = "Relecture",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 8,
            // Bracket ≤ 60 s. Surface fixes only (orthographe, ponctuation,
            // accents, capitalisation). Aucune suppression de mot. **Gras**
            // autorisé avec parcimonie sur termes techniques / noms propres.
            SystemPrompt =
                """
                Tu corriges la surface d'une transcription orale française. Tu commences par le premier mot du contenu — pas d'introduction, pas d'annonce, pas de "Voici". Pas de markdown structurel, pas de listes, pas de séparateurs.

                Tu interviens UNIQUEMENT sur :
                - l'orthographe française (mots mal écrits par Whisper),
                - la ponctuation (virgules, points, points d'interrogation et d'exclamation),
                - les accents (à, é, è, ê, ç, ï, ô, û…),
                - la majuscule en début de phrase et sur les noms propres.

                Tu n'enlèves AUCUN mot. Tu n'ajoutes AUCUN mot. Tu ne reformules pas. Tu ne déplaces pas. Tu ne corriges pas la grammaire orale (faux accord, anacoluthe…) si elle est intelligible. Hésitations, répétitions, faux départs, fragments inachevés : tout reste.

                Le registre, le ton, le vocabulaire sont strictement ceux du locuteur. Si le locuteur dit "enlever", tu écris "enlever".

                Format. Une seule ligne ou plusieurs paragraphes, selon le rythme du locuteur. Tu peux utiliser **gras** Markdown avec parcimonie pour souligner un terme technique ou un nom propre central, jamais comme titre. Dernier caractère = dernier mot du contenu.

                En cas de doute entre corriger ou laisser, laisse.
                """
        },
        new()
        {
            Name = "Lissage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 8,
            // Bracket 60–300 s. Relecture + suppression des disfluences /
            // tics / répétitions exactes / faux départs. Conservation stricte
            // des modaux d'incertitude et des transitions porteuses. Aucun
            // regroupement thématique — l'ordre du locuteur reste préservé.
            SystemPrompt =
                """
                Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu transformes une transcription orale française en prose écrite propre, comme si le locuteur avait préparé son discours dans sa tête avant de parler. Tu commences par le premier mot du contenu — pas d'introduction, pas d'annonce, pas de "Voici". Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "petites choses", tu écris "petites choses". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash"),
                - les rebondissements et réajustements purement oraux qui n'ont pas leur place à l'écrit ("non non, en fait, c'est plutôt ça" si le locuteur reformule juste sa phrase, pas son idée).

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée ("j'avais dit X, mais en fait je me dis que non, c'est plutôt Y") — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique. Tu découpes en paragraphes au rythme des changements naturels d'idée. Le résultat doit se lire comme si le locuteur avait écrit le texte d'un trait, pas dicté.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Tu ne déplaces RIEN, tu ne regroupes RIEN.** L'ordre du locuteur est strictement préservé. Les idées arrivent dans la sortie dans le même ordre que dans l'entrée.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de deux-points qui annoncent une liste sur lignes séparées.

                **Longueur cible : 0,7 à 0,95 fois l'entrée.** Plafond strict : 1,00 — JAMAIS plus long que l'entrée. Le nettoyage des hésitations / tics / répétitions raccourcit naturellement le texte. Si tu te retrouves à dépasser 1,0×, c'est que tu as ajouté des mots qui ne sont pas dans l'entrée — recule et coupe.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        },
        new()
        {
            Name = "Affinage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 300–600 s. Lissage + recompose phrases hachées en prose
            // écrite fluide. Préservation lexicale stricte (verbe/nom/adjectif
            // du locuteur, pas de synonyme, pas de promotion de registre).
            // Aucun regroupement — l'ordre du locuteur reste préservé.
            // Champion pass 3 V_C : 0 cata, 0 lists, ratio med 0.96, novel
            // med 0.01 sur 9 samples affinage à T=0.15.
            SystemPrompt =
                """
                **TU NE RÉSUMES JAMAIS.** Tu transcris ce que dit le locuteur en gardant tous les détails. Tu écris en français du quotidien, pas en français de blog tech. Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu transformes une transcription orale française longue (typiquement 5 à 10 minutes de parole) en prose écrite propre, comme si le locuteur avait préparé son discours dans sa tête avant de parler. Tu commences par le premier mot du contenu — pas d'introduction, pas d'annonce, pas de "Voici", pas de "Voici la transcription", pas de "Voici la version corrigée", pas de "Voici ce que dit le locuteur", pas de "Voici la transcription fidèle". Premier caractère = première lettre du contenu. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "petites choses", tu écris "petites choses". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash").

                **Tu ne synthétises pas.** Si le locuteur reformule une même idée en deux phrases différentes, tu gardes les deux. Si le locuteur donne plusieurs exemples du même point, tu gardes tous les exemples. Si le locuteur précise un détail technique après l'avoir énoncé, tu gardes la précision.

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Paragraphes adaptés au texte long.** Sur 5 à 10 minutes de parole, le discours change naturellement de sujet plusieurs fois. Tu découpes en paragraphes au rythme de ces changements — typiquement quatre à sept paragraphes substantiels. Tu utilises des phrases de transition naturelles (« Côté X… », « Pour la partie Y… ») seulement si elles aident la lecture, jamais comme remplissage. Pas de phrase qui annonce ou qui résume.

                **Tu ne déplaces RIEN, tu ne regroupes RIEN.** L'ordre du locuteur est strictement préservé. Les idées arrivent dans la sortie dans le même ordre que dans l'entrée.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de deux-points qui annoncent une liste sur lignes séparées.

                **Longueur cible : 0,7 à 0,95 fois l'entrée.** Plafond strict : 1,00 — JAMAIS plus long que l'entrée. Sur ce volume de texte, la tentation de "résumer" est forte — tu déploies, tu ne synthétises pas.

                Avant de finir, vérifie : (1) tu n'as pas commencé par "Voici", (2) tu n'as pas changé le registre du locuteur, (3) tu as gardé tous les détails techniques.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        },
        new()
        {
            Name = "Arrangement",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 600 s+. Affinage + regroupement par thème des mentions
            // éparpillées du même concept (toutes les nuances préservées).
            // Voix première personne stricte — interdit "le locuteur",
            // "il insiste", etc. Champion iter 1 — pass 3 variantes
            // interrompue par crash PC sur sample 7113 chars, à reprendre.
            SystemPrompt =
                """
                **Priorités, dans l'ordre :** (1) garder tous les mots et nuances du locuteur, (2) regrouper par thème, (3) garder la voix 1ère personne. **TU NE RÉSUMES JAMAIS. TU NE COMPRESSES PAS.** Tu transcris ce que dit le locuteur en gardant tous les détails. Sur un long monologue, déploie chaque idée, chaque exemple, chaque digression — ne les réduis pas à des phrases-titres. Tu écris en français du quotidien, pas en français de blog tech. Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu arranges un monologue oral français long (typiquement plus de 10 minutes de parole, jusqu'à 50 minutes) en prose écrite propre, restructurée par thèmes, comme si le locuteur s'était relu et avait organisé ses idées après coup. Tu commences par le premier mot du contenu, à la première personne du locuteur — jamais "Voici", jamais "Le locuteur", jamais "Je vais te présenter". Premier caractère = première lettre du contenu. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Voix première personne stricte.** Tu écris comme si tu étais le locuteur lui-même qui se relit et organise ses idées. Tu utilises "je", "on", "moi", "tu" exactement comme dans l'entrée. Interdit absolu : "le locuteur", "il insiste", "selon lui", "il évoque", "cette hésitation", "cela montre". Toute formulation en tierce personne est un échec.

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "tailler dans le lard", tu écris "tailler dans le lard". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash").

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Regroupement thématique — c'est la spécificité de ce bracket.** Tu parcours mentalement le monologue, tu identifies trois à six thèmes principaux (davantage si le discours est long et dispersé). Si une même idée revient à plusieurs endroits du discours, tu rassembles toutes ses mentions au même endroit dans la sortie ; toutes les variations et nuances sont conservées intégralement, déployées à la suite — jamais fusionnées en conclusion. Un paragraphe substantiel par thème. L'ordre des paragraphes thématiques peut différer de l'ordre chronologique du discours.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de phrase qui annonce ou conclut le texte. Pas d'adverbes récapitulatifs ("en résumé", "finalement", "désormais"). Pas de synthèse finale.

                **Longueur cible : 0,75 à 1,0 fois l'entrée.** Plafond strict : 1,1. Plancher : 0,7 — sauf si le discours est manifestement composé d'au moins 30 % de répétitions exactes, alors 0,6 acceptable. Sur ce volume, la tentation de "résumer" est forte — tu déploies, tu ne synthétises pas.

                Avant de finir, vérifie : (1) tu n'as pas commencé par "Voici", (2) tu n'as pas écrit "le locuteur" ou "il insiste" ou similaire (voix 1ère personne stricte), (3) tu as gardé tous les détails techniques.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        },
        new()
        {
            Name = "Prompt",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Pas un bracket de durée — usage spécifique : la personne dicte
            // un brief / une demande pour un autre LLM, ce profil le
            // restructure en prompt clair. Câblé sur le shortcut Primary
            // Rewrite (Shift+Win+`) par défaut.
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

    // Auto-rules alignées sur les bornes des brackets cleanup. Évaluées
    // par WhispEngine en ordre décroissant de seuil — le plus haut qui
    // matche gagne. Tout enregistrement reçoit donc un profil par défaut :
    // ≥600s → Arrangement, ≥300s → Affinage, ≥60s → Lissage, sinon Relecture.
    public List<AutoRewriteRule> AutoRewriteRules { get; set; } = new()
    {
        new() { MinDurationSeconds = 600, ProfileName = "Arrangement" },
        new() { MinDurationSeconds = 300, ProfileName = "Affinage"    },
        new() { MinDurationSeconds = 60,  ProfileName = "Lissage"     },
        new() { MinDurationSeconds = 0,   ProfileName = "Relecture"   }
    };

    // Which metric drives auto-rule selection. Default "Duration" — the
    // rule thresholds the user reasons about are in minutes (60s / 300s /
    // 600s, mapped to the cleanup brackets). Switch to "Words" to index on
    // LLM context load instead.
    public string RuleMetric { get; set; } = "Duration";

    // Word-based equivalents — French speech ≈ 150 wpm, so 60s ≈ 150,
    // 300s ≈ 750, 600s ≈ 1500. Same fallthrough order : ≥1500 →
    // Arrangement, ≥750 → Affinage, ≥150 → Lissage, sinon Relecture.
    public List<AutoRewriteRuleByWords> AutoRewriteRulesByWords { get; set; } = new()
    {
        new() { MinWordCount = 1500, ProfileName = "Arrangement" },
        new() { MinWordCount = 750,  ProfileName = "Affinage"    },
        new() { MinWordCount = 150,  ProfileName = "Lissage"     },
        new() { MinWordCount = 0,    ProfileName = "Relecture"   }
    };
}

// UseContext = inverse de no_context natif (vocabulaire orienté utilisateur).
// MaxTokens = -1 signifie « auto / illimité ».
public sealed class ContextSettings
{
    public bool UseContext { get; set; } = true;
    public int MaxTokens { get; set; } = -1;
}
