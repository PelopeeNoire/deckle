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
        "Bon. Je suis en train de coder une application Windows, je continue l'interface avec un contour animé qui tourne. " +
        "C'est plutôt propre, même si certaines parties restent fragiles. " +
        "Côté workflow, je travaille avec plusieurs branches Git, je merge sur la branche principale, je lance les tests à chaque itération. " +
        "Côté outils, .NET, Visual Studio, Python, Whisper, le shell. " +
        "Ouais, ça avance bien, même si parfois j'ai un truc cassé et il faut tout reprendre. " +
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
    // null = primary rewrite disabled (hotkey fires but rewriting is skipped).
    // Symmetric with Secondary — both slots are opt-in by default; the three
    // bracket profiles (Lissage/Affinage/Arrangement) are picked by
    // AutoRewriteRules on the plain transcribe shortcut.
    public string? PrimaryRewriteProfileName { get; set; }

    // Profile used by the Secondary Rewrite shortcut (Ctrl+Win+`).
    // null = secondary rewrite disabled (hotkey fires but rewriting is skipped).
    public string? SecondaryRewriteProfileName { get; set; }

    // Stable companions to the *ProfileName* fields above — resolved to
    // RewriteProfile.Id. Lookup at runtime prefers Id, falls back to Name
    // for legacy configs. Filled by SettingsService.MigrateProfileIds.
    public string? PrimaryRewriteProfileId { get; set; }
    public string? SecondaryRewriteProfileId { get; set; }

    // Trois profils alignés sur les brackets de cleanup (lib/corpus.py:38-47).
    // Gradient d'intervention : lissage (disfluences) → affinage (oral → écrit)
    // → arrangement (regroupement thématique). Le SystemPrompt est laissé vide
    // par défaut — l'utilisateur écrit le sien. Le couple (Temperature, NumCtxK)
    // reste pré-réglé par bracket : NumCtxK suit la longueur cible du bracket
    // (8K court, 16K long), Temperature à 0.30 = compromis fidélité / cohérence
    // observé en bench. Model laissé vide aussi : à choisir une fois Ollama
    // configuré.
    public List<RewriteProfile> Profiles { get; set; } = new()
    {
        new()
        {
            Name = "Lissage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 8,
            // Bracket 60–300 s. SystemPrompt vide par défaut — l'utilisateur
            // écrit le sien selon ses goûts (lissage des disfluences, ordre du
            // locuteur préservé, etc.).
            SystemPrompt = ""
        },
        new()
        {
            Name = "Affinage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 300–600 s. SystemPrompt vide par défaut — l'utilisateur
            // écrit le sien (recompose phrases hachées en prose écrite, etc.).
            SystemPrompt = ""
        },
        new()
        {
            Name = "Arrangement",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 600 s+. SystemPrompt vide par défaut — l'utilisateur
            // écrit le sien (regroupement par thème, voix 1ère personne, etc.).
            SystemPrompt = ""
        }
    };

    // Auto-rules alignées sur les bornes des brackets cleanup. Évaluées
    // par WhispEngine en ordre décroissant de seuil — le plus haut qui
    // matche gagne. Plancher à 60 s : en dessous, aucune règle ne matche,
    // le profil reste null et la réécriture LLM est skipée (comportement
    // no-op, le texte brut Whisper part au clipboard tel quel — Whisper
    // sort déjà du texte propre sur les dictées courtes, un cycle Ollama
    // serait gratuit).
    public List<AutoRewriteRule> AutoRewriteRules { get; set; } = new()
    {
        new() { MinDurationSeconds = 600, ProfileName = "Arrangement" },
        new() { MinDurationSeconds = 300, ProfileName = "Affinage"    },
        new() { MinDurationSeconds = 60,  ProfileName = "Lissage"     }
    };

    // Which metric drives auto-rule selection. Default "Duration" — the
    // rule thresholds the user reasons about are in minutes (60s / 300s /
    // 600s, mapped to the cleanup brackets). Switch to "Words" to index on
    // LLM context load instead.
    public string RuleMetric { get; set; } = "Duration";

    // Word-based equivalents — calibrated on 88 corpus samples (median
    // 115 wpm globally, range 47–205). The bracket boundaries 1/5/10 min
    // map to ~115/575/1150 words at that median, rounded to multiples of
    // 50: 150/600/1200. Plancher à 150 mots — symétrique avec la règle
    // duration de 60 s : en dessous, aucune règle ne matche, pas de
    // cycle Ollama gratuit sur une dictée courte.
    public List<AutoRewriteRuleByWords> AutoRewriteRulesByWords { get; set; } = new()
    {
        new() { MinWordCount = 1200, ProfileName = "Arrangement" },
        new() { MinWordCount = 600,  ProfileName = "Affinage"    },
        new() { MinWordCount = 150,  ProfileName = "Lissage"     }
    };
}

// UseContext = inverse de no_context natif (vocabulaire orienté utilisateur).
// MaxTokens = -1 signifie « auto / illimité ».
public sealed class ContextSettings
{
    public bool UseContext { get; set; } = true;
    public int MaxTokens { get; set; } = -1;
}
