// Alt+`       → transcription → clipboard + collage auto
// Alt+Ctrl+`  → transcription → réécriture LLM → clipboard + collage auto (TODO)
//
// UX toggle : premier appui = démarrer, deuxième appui = arrêter + transcrire

using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

// ─── Configuration ────────────────────────────────────────────────────────────

const string MODEL_FILE = "ggml-large-v3.bin";

string modelPath = Environment.GetEnvironmentVariable("WHISP_MODEL_PATH")
                   ?? Path.Combine(@"D:\projects\ai\transcription\shared", MODEL_FILE);

if (!File.Exists(modelPath))
{
    MessageBox.Show($"Modèle introuvable :\n{modelPath}", "Whisp — Erreur",
        MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}

// Intercepter les exceptions non gérées avant qu'elles tuent le process silencieusement.
// ThreadException : exceptions sur le thread UI.
// UnhandledException : exceptions sur les threads de fond.
Application.ThreadException += (_, e) =>
    MessageBox.Show(e.Exception.ToString(), "Whisp — Erreur UI thread");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    MessageBox.Show(e.ExceptionObject.ToString(), "Whisp — Erreur thread de fond");

// PerMonitorV2 : le process déclare qu'il gère lui-même le DPI par moniteur.
// Windows cesse alors de virtualiser les coordonnées et envoie des notifications
// WM_DPICHANGED quand la fenêtre change de moniteur. WinForms .NET 6+ applique
// automatiquement le rescaling lors de ces notifications — plus de flou bitmap.
// Doit être appelé avant EnableVisualStyles() pour être pris en compte.
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// System : WinForms suit le thème Windows (clair ou sombre) pour les contrôles
// qui utilisent les couleurs système (SystemColors). N'écrase pas les couleurs
// définies explicitement (BackColor/ForeColor codées en dur).
Application.SetColorMode(SystemColorMode.System);

Application.EnableVisualStyles();
Application.Run(new WhispForm(modelPath));

// ─── Form caché — reçoit WM_HOTKEY, gère le tray ─────────────────────────────

partial class WhispForm : Form
{
    // ── Debug ─────────────────────────────────────────────────────────────────

    // Mettre à false pour désactiver tous les logs de debug sans recompiler.
    const bool DEBUG_LOG = true;

    // ── Constantes Ollama ─────────────────────────────────────────────────────

    const string OLLAMA_MODEL = "ministral-3:3b--instruct--96k";   // modèle local pour la réécriture
    const string OLLAMA_URL   = "http://localhost:11434/api/chat";  // /api/chat : messages structurés (system + user)

    // Instructions envoyées explicitement dans chaque requête (role "system").
    // Plus robuste que le Modelfile Ollama : Ollama détecte mal le TEMPLATE des GGUF locaux,
    // ce qui peut faire ignorer le system prompt du Modelfile. /api/chat contourne ce problème.
    const string OLLAMA_SYSTEM_PROMPT =
        "Tu reçois une transcription vocale brute en français. Réécris-la en texte fluide " +
        "et cohérent : corrige les erreurs de transcription, les répétitions, les sauts de " +
        "phrases, la syntaxe orale. Conserve le sens exact, tous les points abordés et le " +
        "registre de la personne. Ne résume pas. Ne commente pas. Ne reformule pas avec moins " +
        "d'informations. Ne commence pas ta réponse par 'Voici', un titre ou une introduction. " +
        "Ta réponse commence directement par la première phrase du texte réécrit.";

    // ── Constantes Windows ────────────────────────────────────────────────────

    // Identifiants arbitraires pour nos 2 hotkeys (doivent être uniques par process)
    const int HOTKEY_ID_ALT      = 1; // Alt+`          → transcription + collage auto
    const int HOTKEY_ID_ALT_CTRL = 3; // Alt+Ctrl+`     → transcription + LLM + collage auto

    // Masques de modificateurs (se combinent avec | dans RegisterHotKey)
    const uint MOD_ALT     = 0x0001; // touche Alt
    const uint MOD_CONTROL = 0x0002; // touche Ctrl

    // VK_OEM_3 : code virtuel du ` (backtick) sur clavier QWERTY US
    const uint VK_OEM_3 = 0xC0;

    // WM_HOTKEY : message Windows envoyé à notre fenêtre quand le raccourci est pressé
    const int WM_HOTKEY = 0x0312;

    // ── État ──────────────────────────────────────────────────────────────────

    readonly string    _modelPath;
    readonly NotifyIcon _trayIcon;

    // volatile : interdit au compilateur de mettre ces valeurs en cache CPU.
    // Sans volatile, le thread d'enregistrement pourrait lire une valeur obsolète
    // et ne jamais voir le signal d'arrêt.
    volatile bool   _isRecording  = false;
    volatile bool   _stopRecording = false;

    // Flags déterminant le comportement après transcription.
    // Fixés au premier appui (démarrage), lus à la fin de Transcribe().
    // volatile : écrits sur le thread UI, lus sur le thread de transcription.
    volatile bool _shouldPaste = false; // vrai si le hotkey demande le collage auto
    volatile bool _useLlm      = false; // vrai si le hotkey demande la réécriture LLM (TODO)

    // Handle de la fenêtre cible pour le collage.
    // Capturé au moment où l'utilisateur arrête l'enregistrement (deuxième appui).
    // volatile : accédé depuis le thread UI (écriture) et le thread de transcription (lecture).
    volatile IntPtr _pasteTarget = IntPtr.Zero;

    // Contexte Whisper chargé une seule fois au démarrage (thread de fond).
    // volatile : écrit depuis le thread de chargement, lu depuis le thread de transcription.
    // IntPtr.Zero tant que le chargement n'est pas terminé.
    volatile IntPtr _ctx = IntPtr.Zero;

    DebugForm _debugForm = null!;

    // ── Constructeur ──────────────────────────────────────────────────────────

    public WhispForm(string modelPath)
    {
        _modelPath = modelPath;

        // Rendre la fenêtre complètement invisible.
        // La fenêtre doit quand même exister (handle Windows valide)
        // pour que RegisterHotKey puisse lui envoyer des messages.
        this.Text            = "Whisp";
        this.ShowInTaskbar   = false;   // absent de la barre des tâches
        this.FormBorderStyle = FormBorderStyle.None;
        this.Size            = new System.Drawing.Size(1, 1);
        this.Opacity         = 0;       // transparent (invisible même si affiché)

        // Icône dans la barre système (zone de notification en bas à droite)
        _trayIcon = new NotifyIcon
        {
            Icon    = SystemIcons.Application,
            Text    = "Whisp — Chargement du modèle...",
            Visible = true
        };

        // Menu contextuel : clic droit sur l'icône
        var menu = new ContextMenuStrip();
        menu.Items.Add("Quitter", null, (_, _) => Application.Exit());
        _trayIcon.ContextMenuStrip = menu;

        // Clic gauche sur l'icône tray : ramener la fenêtre de debug au premier plan.
        // Click est déclenché sur le clic gauche uniquement (contrairement à MouseClick).
        // Show() est sans effet si la fenêtre est déjà visible. Activate() la met au premier
        // plan et lui donne le focus, qu'elle soit visible ou cachée.
        _trayIcon.Click += (_, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
            {
                _debugForm.Show();
                _debugForm.Activate();
            }
        };

        // Enregistrer les 2 raccourcis globaux.
        // this.Handle force la création du handle Windows (identifiant numérique
        // que Windows utilise pour envoyer les messages WM_HOTKEY à notre fenêtre).
        RegisterHotKey(this.Handle, HOTKEY_ID_ALT,      MOD_ALT,                 VK_OEM_3);
        RegisterHotKey(this.Handle, HOTKEY_ID_ALT_CTRL, MOD_ALT | MOD_CONTROL,   VK_OEM_3);

        _debugForm = new DebugForm();

        // Charger le modèle whisper en arrière-plan dès le démarrage.
        // Le chargement de ggml-large-v3.bin prend plusieurs secondes — le faire ici
        // plutôt que dans Transcribe() évite cette latence au moment de l'utilisation.
        var loadThread = new Thread(() =>
        {
            var swLoad = System.Diagnostics.Stopwatch.StartNew();
            DbgLog("INIT", $"Chargement du modèle ({Path.GetFileName(_modelPath)})...");

            IntPtr ctxParamsPtr = whisper_context_default_params_by_ref();
            WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
            whisper_free_context_params(ctxParamsPtr);
            ctxParams.use_gpu = 1;

            _ctx = whisper_init_from_file_with_params(_modelPath, ctxParams);
            swLoad.Stop();

            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    if (_ctx == IntPtr.Zero)
                    {
                        _trayIcon.Text = "Whisp — Erreur : modèle non chargé";
                        MessageBox.Show($"Impossible de charger le modèle :\n{_modelPath}",
                            "Whisp — Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else
                    {
                        DbgLog("INIT", $"Modèle chargé en {swLoad.ElapsedMilliseconds} ms — prêt");
                        _trayIcon.Text = "Whisp — En attente";
                    }
                });
        });
        loadThread.IsBackground = true;
        loadThread.Start();
    }

    // ── Ne jamais afficher la fenêtre ────────────────────────────────────────
    //
    // SetVisibleCore est la méthode interne que WinForms appelle pour afficher
    // ou cacher une fenêtre. On la surcharge pour toujours passer false.
    // C'est le pattern standard pour les apps tray-only.
    // Hide() dans OnLoad peut déclencher la fermeture de l'app dans certains cas.

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    // ── Intercepter tous les messages Windows destinés à cette fenêtre ────────

    protected override void WndProc(ref Message m)
    {
        // m.Msg    : type de message (WM_HOTKEY = 0x0312)
        // m.WParam : identifiant du hotkey (1, 2, 3 ou 4)
        if (m.Msg == WM_HOTKEY)
        {
            int id = (int)m.WParam;
            bool isOurHotkey = id == HOTKEY_ID_ALT || id == HOTKEY_ID_ALT_CTRL;

            if (isOurHotkey)
            {
                if (!_isRecording)
                {
                    // Premier appui : déterminer le comportement souhaité d'après le hotkey,
                    // puis démarrer l'enregistrement.
                    _shouldPaste = true;                        // les deux raccourcis collent toujours
                    _useLlm      = id == HOTKEY_ID_ALT_CTRL;   // LLM uniquement pour Alt+Ctrl+`
                    StartRecording();
                }
                else
                {
                    // Deuxième appui (n'importe lequel des 2) : arrêter.
                    // Capturer la fenêtre au premier plan MAINTENANT, pendant que l'utilisateur
                    // vient juste d'appuyer sur le raccourci : c'est la fenêtre cible du collage.
                    // Dans quelques secondes (transcription), on la remettra au premier plan
                    // avant d'injecter Ctrl+V (si _shouldPaste est vrai).
                    _pasteTarget   = GetForegroundWindow();
                    _stopRecording = true;
                }
            }
        }
        base.WndProc(ref m); // laisser WinForms traiter les autres messages
    }

    // ── Helper log de debug ──────────────────────────────────────────────────
    //
    // Envoie une ligne horodatée à la fenêtre de debug si DEBUG_LOG est actif.
    // Thread-safe : DebugForm.Log() achemine via BeginInvoke si nécessaire.

    void DbgLog(string phase, string message)
    {
        if (!DEBUG_LOG) return;
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        _debugForm.Log($"[{ts}] [{phase}] {message}");
    }

    // ── Démarrer l'enregistrement sur un thread de fond ───────────────────────

    void StartRecording()
    {
        _isRecording   = true;
        _stopRecording = false;

        // Ouvrir la fenêtre de debug dès le premier appui pour que les logs
        // de Record() soient visibles (Clear + Show ici, plus dans OnRecordingDone).
        _debugForm.Clear();
        _debugForm.Show();
        _debugForm.TopMost = true; // premier plan pendant toute la session enregistrement + transcription
        DbgLog("RECORD", "Démarrage de l'enregistrement");

        _trayIcon.Text = "Whisp — Enregistrement...  (appuyer à nouveau pour arrêter)";
        _trayIcon.Icon = SystemIcons.Shield; // icône différente = feedback visuel

        // Thread séparé : l'enregistrement audio est bloquant.
        // Si on le faisait sur le thread UI, la fenêtre ne répondrait plus
        // (plus de WM_HOTKEY, plus de menu tray).
        var thread = new Thread(() =>
        {
            byte[] rawPcm = Record();

            // BeginInvoke : poste un appel sur le thread UI.
            // Obligatoire : NotifyIcon et les contrôles WinForms ne sont
            // accessibles que depuis le thread qui les a créés.
            if (!this.IsDisposed)
                this.BeginInvoke(() => OnRecordingDone(rawPcm));
        });
        thread.IsBackground = true; // le thread ne bloque pas la fermeture de l'appli
        thread.Start();
    }

    // ── Fin d'enregistrement : lancer la transcription ────────────────────────

    void OnRecordingDone(byte[] rawPcm)
    {
        // Cette méthode s'exécute sur le thread UI (via BeginInvoke)
        _isRecording = false;

        if (rawPcm.Length == 0)
        {
            _trayIcon.Text = "Whisp — En attente";
            _trayIcon.Icon = SystemIcons.Application;
            return;
        }

        _trayIcon.Text = "Whisp — Transcription en cours...";

        // La transcription whisper est bloquante (plusieurs secondes).
        // On la lance aussi sur un thread de fond.
        var thread = new Thread(() => Transcribe(rawPcm));
        thread.IsBackground = true;
        thread.Start();
    }

    // ── Transcription whisper + copie clipboard ───────────────────────────────

    void Transcribe(byte[] rawPcm)
    {
        double audioSec = rawPcm.Length / 32000.0; // 16000 Hz × 2 octets = 32000 octets/s
        DbgLog("TRANSCRIBE", $"rawPcm reçu — {rawPcm.Length} octets ({audioSec:F1}s d'audio)");

        // Écrire le WAV sur disque (utile pour vérifier l'enregistrement si besoin)
        string tempWav = Path.Combine(AppContext.BaseDirectory, "temp.wav");
        WriteWav(tempWav, rawPcm);

        float[] allSamples = PcmToFloat(rawPcm);
        DbgLog("TRANSCRIBE", $"Conversion float[] terminée — {allSamples.Length} samples");

        // Le contexte Whisper est chargé une seule fois au démarrage (champ _ctx).
        // Si l'utilisateur transcrit avant la fin du chargement, on refuse poliment.
        IntPtr ctx = _ctx;
        if (ctx == IntPtr.Zero)
        {
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — Modèle non prêt";
                    _trayIcon.Icon = SystemIcons.Application;
                    MessageBox.Show("Le modèle est encore en cours de chargement.\nRéessaie dans quelques secondes.",
                        "Whisp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            return;
        }

        // Paramètres de transcription — créés une fois, réutilisés pour chaque chunk.
        IntPtr fullParamsPtr = whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        whisper_free_params(fullParamsPtr);

        // StringToHGlobalAnsi : alloue une chaîne C (bytes ASCII) en mémoire non managée.
        // Obligatoire pour passer une chaîne à une DLL C. Libérés après la boucle de chunks.
        IntPtr langPtr         = Marshal.StringToHGlobalAnsi("fr");
        IntPtr promptPtr       = Marshal.StringToHGlobalAnsi("Transcription en français.");
        wparams.language       = langPtr;
        wparams.initial_prompt = promptPtr;
        // entropy_thold : défaut 2.4, descendu à 1.9.
        // Dans ce build whisper.cpp, joue le rôle du compression_ratio_threshold d'OpenAI.
        // Mesure le désordre de la distribution de probabilité sur les tokens.
        // Une valeur haute tolère les sorties incohérentes (répétitions, hallucinations).
        wparams.entropy_thold   = 1.9f;
        // no_speech_thold : monté à 0.7 (défaut 0.6).
        // Quand la probabilité que le segment soit du silence dépasse ce seuil, Whisper rejette le segment.
        wparams.no_speech_thold = 0.7f;
        wparams.print_progress  = 0;

        // Patterns parasites injectés par Whisper sur silence ou fin d'audio.
        static bool IsHallucinatedOutput(string text) =>
            string.IsNullOrWhiteSpace(text) ||
            text.Contains("Sous-titrage", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radio-Canada",  StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Sous-titres",   StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SRC",           StringComparison.Ordinal);

        // Transcription par chunks de 30 secondes.
        // Whisper est entraîné sur des fenêtres de 30s max — traiter plus long en une passe
        // favorise les répétitions et les hallucinations.
        // Chaque chunk est transcrit et filtré indépendamment. Les chunks propres s'accumulent
        // dans le buffer. Le clipboard est mis à jour après chaque chunk propre.
        const int CHUNK_SAMPLES = 30 * 16_000; // 30s × 16 000 Hz = 480 000 floats
        var chunkBuffer = new List<string>();
        int totalSamples = allSamples.Length;
        int chunkIndex   = 0;
        int nChunks      = (int)Math.Ceiling((double)totalSamples / CHUNK_SAMPLES);
        DbgLog("TRANSCRIBE", $"{nChunks} chunk(s) à traiter — {totalSamples} samples au total");

        for (int offset = 0; offset < totalSamples; offset += CHUNK_SAMPLES)
        {
            chunkIndex++;
            int   count     = Math.Min(CHUNK_SAMPLES, totalSamples - offset);
            float chunkSec  = (float)count / 16_000f;
            float[] chunk   = allSamples[offset..(offset + count)];

            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex}/{nChunks} — offset {offset}, {count} samples ({chunkSec:F1}s) → whisper_full...");

            var swChunk = System.Diagnostics.Stopwatch.StartNew();
            int result = whisper_full(ctx, wparams, chunk, chunk.Length);
            swChunk.Stop();
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex}/{nChunks} — whisper_full terminé en {swChunk.ElapsedMilliseconds} ms, code retour={result}");

            if (result != 0)
            {
                _debugForm.Log("→ whisper_full a retourné une erreur, chunk ignoré.");
                continue;
            }

            int nSeg = whisper_full_n_segments(ctx);
            var parts = new List<string>();
            for (int i = 0; i < nSeg; i++)
                parts.Add(Marshal.PtrToStringUTF8(whisper_full_get_segment_text(ctx, i)) ?? "");
            string chunkText = string.Join(" ", parts).Trim();

            _debugForm.Log($"Brut    : {chunkText}");

            if (IsHallucinatedOutput(chunkText))
            {
                _debugForm.Log("→ filtré (hallucination détectée)");
                continue;
            }

            chunkBuffer.Add(chunkText);
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex}/{nChunks} → accepté ({chunkText.Length} chars). Buffer : {chunkBuffer.Count} chunk(s)");
        }

        Marshal.FreeHGlobal(langPtr);
        Marshal.FreeHGlobal(promptPtr);
        // ctx = _ctx : partagé entre les transcriptions, libéré dans Dispose().

        string fullText = string.Join(" ", chunkBuffer).Trim();

        if (string.IsNullOrWhiteSpace(fullText))
        {
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — En attente";
                    _trayIcon.Icon = SystemIcons.Application;
                    _debugForm.TopMost = false; // transcription terminée — fenêtre repasse en arrière-plan
                });
            return;
        }

        // Copie systématique du texte brut — filet de sécurité : le presse-papier est
        // toujours alimenté même si le LLM échoue ou n'est pas demandé.
        CopyToClipboard(fullText);

        // Réécriture LLM : point d'accroche pour les raccourcis Alt+Ctrl+` et Alt+Ctrl+Shift+`
        // Si le LLM répond, sa version remplace le brut dans le presse-papier.
        if (_useLlm)
        {
            string? rewritten = RewriteWithLlm(fullText);
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                fullText = rewritten;
                CopyToClipboard(fullText);
            }
        }

        if (_shouldPaste)
            PasteFromClipboard();

        if (!this.IsDisposed)
            this.BeginInvoke(() =>
            {
                _trayIcon.Text = "Whisp — En attente";
                _trayIcon.Icon = SystemIcons.Application;
                _debugForm.TopMost = false; // transcription terminée — fenêtre repasse en arrière-plan
            });
    }

    // ── Réécriture du texte via Ollama ───────────────────────────────────────
    //
    // Appel POST bloquant vers l'API locale d'Ollama.
    // On est déjà sur un thread de fond (lancé par OnRecordingDone),
    // donc .GetAwaiter().GetResult() est sûr ici : pas de deadlock possible.
    // En cas d'erreur (Ollama absent, timeout, etc.), retourne null →
    // l'appelant conserve le texte original.

    static readonly HttpClient _http = new();   // une seule instance partagée (bonne pratique)

    string? RewriteWithLlm(string text)
    {
        try
        {
            // Texte brut à réécrire. Les instructions sont dans OLLAMA_SYSTEM_PROMPT,
            // passées explicitement via le rôle "system" dans chaque requête.
            string prompt = text;

            // Corps de la requête Ollama /api/chat
            // messages : tableau de rôles (system + user). Ollama applique le bon template
            //            de prompt quelle que soit la détection automatique du GGUF.
            // stream: false   → Ollama attend la fin de la génération avant de répondre
            // keep_alive: "5m" → le modèle reste en VRAM 5 minutes, puis Ollama le vide
            var body = new
            {
                model      = OLLAMA_MODEL,
                stream     = false,
                keep_alive = "5m",
                messages   = new[]
                {
                    new { role = "system", content = OLLAMA_SYSTEM_PROMPT },
                    new { role = "user",   content = prompt }
                }
            };

            string json = JsonSerializer.Serialize(body);

            // StringContent : enveloppe la chaîne JSON dans un objet que HttpClient peut envoyer
            // Encoding.UTF8 + "application/json" : en-tête Content-Type attendu par Ollama
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // PostAsync bloquant. Timeout par défaut de HttpClient = 100 secondes.
            // Pour les gros modèles, augmenter si nécessaire.
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
        catch (Exception ex)
        {
            // Notifier discrètement sans bloquer ni crasher
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — LLM indisponible";
                    _trayIcon.Icon = SystemIcons.Warning;
                });
            _ = ex; // silence du compilateur (ex non utilisé intentionnellement)
            return null;
        }
    }

    // ── Nettoyage à la fermeture ──────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Libérer les 4 raccourcis. Un hotkey non libéré reste bloqué pour
            // toutes les autres applis tant que le process tourne.
            UnregisterHotKey(this.Handle, HOTKEY_ID_ALT);
            UnregisterHotKey(this.Handle, HOTKEY_ID_ALT_CTRL);
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _debugForm.Dispose();
            // Libérer le contexte Whisper chargé au démarrage.
            if (_ctx != IntPtr.Zero)
            {
                whisper_free(_ctx);
                _ctx = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }

    // ─── Fonctions audio ─────────────────────────────────────────────────────

    byte[] Record()
    {
        // Constantes waveIn
        const uint WAVE_MAPPER    = 0xFFFFFFFF; // device audio par défaut du système
        const uint CALLBACK_EVENT = 0x00050000; // signaler un event quand un buffer est prêt
        const uint WHDR_DONE      = 0x00000001; // flag : le driver a fini d'écrire ce buffer
        const int  N_BUFFERS      = 4;          // nombre de buffers en rotation
        const int  BYTES_PER_BUF  = 16000 * 2 * 500 / 1000; // 500ms × 16kHz × 2 octets/sample

        // Format audio : PCM 16kHz mono 16 bits
        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,     // 1 = PCM non compressé
            nChannels       = 1,     // mono
            nSamplesPerSec  = 16000,
            nAvgBytesPerSec = 32000, // 16000 samples × 2 octets
            nBlockAlign     = 2,     // 1 canal × 2 octets
            wBitsPerSample  = 16,
            cbSize          = 0,
        };

        IntPtr hEvent = CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, null);

        uint err = waveInOpen(out IntPtr hWaveIn, WAVE_MAPPER, ref wfx, hEvent, IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            CloseHandle(hEvent);
            return [];
        }

        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        IntPtr[] hdrPtrs = new IntPtr[N_BUFFERS];
        IntPtr[] bufPtrs  = new IntPtr[N_BUFFERS];

        for (int i = 0; i < N_BUFFERS; i++)
        {
            bufPtrs[i] = Marshal.AllocHGlobal(BYTES_PER_BUF);
            hdrPtrs[i] = Marshal.AllocHGlobal((int)hdrSize);
            Marshal.StructureToPtr(new WAVEHDR
            {
                lpData         = bufPtrs[i],
                dwBufferLength = BYTES_PER_BUF,
            }, hdrPtrs[i], fDeleteOld: false);
            waveInPrepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
        }

        waveInStart(hWaveIn);
        var allBytes = new List<byte>();
        DbgLog("RECORD", "Boucle waveIn démarrée");

        // Boucle d'enregistrement.
        // Avant : s'arrêtait sur Console.ReadKey.
        // Maintenant : s'arrête quand _stopRecording passe à true
        // (déclenché par un deuxième appui sur Alt+`).
        while (!_stopRecording)
        {
            WaitForSingleObject(hEvent, 100); // attendre max 100ms qu'un buffer se remplisse

            for (int i = 0; i < N_BUFFERS; i++)
            {
                WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    var data = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                    allBytes.AddRange(data);
                    DbgLog("RECORD", $"Buffer[{i}] récolté — {hdr.dwBytesRecorded} octets (total : {allBytes.Count})");

                    // Remettre le buffer dans la file du driver.
                    // On efface WHDR_DONE (0x1) mais on garde WHDR_PREPARED (0x2).
                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }
        }
        DbgLog("RECORD", $"Boucle terminée — {allBytes.Count} octets accumulés ({allBytes.Count / 32000.0:F1}s)");

        waveInStop(hWaveIn);
        Thread.Sleep(100); // laisser le driver retourner les buffers en cours

        // Récolter les buffers partiellement remplis
        for (int i = 0; i < N_BUFFERS; i++)
        {
            WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
            if ((hdr.dwFlags & WHDR_DONE) != 0 && hdr.dwBytesRecorded > 0)
            {
                var data = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                allBytes.AddRange(data);
            }
            waveInUnprepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            Marshal.FreeHGlobal(bufPtrs[i]);
            Marshal.FreeHGlobal(hdrPtrs[i]);
        }

        waveInClose(hWaveIn);
        CloseHandle(hEvent);

        return allBytes.ToArray();
    }

    static float[] PcmToFloat(byte[] pcm)
    {
        // pcm = octets Int16 little-endian (2 octets par sample)
        // Whisper attend des floats normalisés [-1.0, 1.0]
        int n = pcm.Length / 2;
        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            result[i] = s / 32768.0f;
        }
        return result;
    }

    void PasteFromClipboard()
    {
        const uint   INPUT_KEYBOARD  = 1;       // type = clavier (pas souris ni matériel)
        const uint   KEYEVENTF_KEYUP = 0x0002;  // flag : relâcher la touche (sans ce flag = enfoncer)
        const ushort VK_CONTROL      = 0x11;    // code virtuel Windows de la touche Ctrl
        const ushort VK_V            = 0x56;    // code virtuel Windows de la touche V

        // Remettre la fenêtre cible au premier plan avant d'injecter les touches.
        // Sans ça, Ctrl+V part dans la fenêtre active au moment de l'appel,
        // qui peut être n'importe quoi après plusieurs secondes de transcription.
        if (_pasteTarget != IntPtr.Zero)
        {
            SetForegroundWindow(_pasteTarget);
            Thread.Sleep(50); // laisser Windows traiter le changement de focus
        }

        int cbSize = Marshal.SizeOf<INPUT>(); // taille d'un INPUT, passée à SendInput pour validation

        var inputs = new INPUT[]
        {
            // 1. Ctrl enfoncé
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL },
            // 2. V enfoncé  (Windows voit Ctrl+V → déclenche le collage dans l'app active)
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V },
            // 3. V relâché
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V,       ki_dwFlags = KEYEVENTF_KEYUP },
            // 4. Ctrl relâché
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL, ki_dwFlags = KEYEVENTF_KEYUP },
        };

        SendInput((uint)inputs.Length, inputs, cbSize);
    }

    static void CopyToClipboard(string text)
    {
        const uint GMEM_MOVEABLE  = 0x0002;
        const uint CF_UNICODETEXT = 13; // format UTF-16LE, null-terminé

        int byteCount = (text.Length + 1) * 2; // +1 char pour '\0', × 2 pour UTF-16

        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hMem == IntPtr.Zero) return;

        IntPtr ptr = GlobalLock(hMem);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
        GlobalUnlock(hMem);

        if (!OpenClipboard(IntPtr.Zero)) return;
        EmptyClipboard();
        SetClipboardData(CF_UNICODETEXT, hMem);
        CloseClipboard();
    }

    static void WriteWav(string path, byte[] pcmData)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w  = new BinaryWriter(fs);

        int dataSize = pcmData.Length;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);           // taille du chunk fmt (toujours 16 pour PCM)
        w.Write((short)1);     // format : PCM
        w.Write((short)1);     // canaux : mono
        w.Write(16000);        // sample rate
        w.Write(32000);        // byte rate = 16000 × 2
        w.Write((short)2);     // block align = 1 canal × 2 octets
        w.Write((short)16);    // bits par sample
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        w.Write(pcmData);
    }
}

