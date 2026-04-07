// Alt+`       → transcription → clipboard + collage auto
// Alt+Ctrl+`  → transcription → réécriture LLM → clipboard + collage auto (TODO)
//
// UX toggle : premier appui = démarrer, deuxième appui = arrêter + transcrire

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    readonly Icon _trayIconIdle;
    readonly Icon _trayIconRecording;
    readonly LlmService _llm;

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

    // Pipeline producteur/consommateur entre le thread d'enregistrement et le thread de transcription.
    // Le thread Record() y pousse des chunks float[] dès que 30s d'audio sont prêtes.
    // Le thread Transcribe() en consomme en continu via GetConsumingEnumerable().
    // Recréé à chaque session d'enregistrement (BlockingCollection ne peut être complété qu'une fois).
    BlockingCollection<float[]> _pipeline = null!;

    // Buffer interne des textes transcrits, chunk par chunk.
    // Champ de classe (pas variable locale) : survit à une exception non gérée
    // pendant la phase de transcription et permet de récupérer les chunks déjà traités.
    // Vidé en début de chaque session par StartRecording().
    readonly List<string> _transcribedChunks = new();

    // Chronomètre démarré au début de chaque enregistrement.
    // Null avant le premier enregistrement, Running pendant Record()+Transcribe().
    // Permet d'afficher le temps écoulé dans les logs en complément de l'heure.
    System.Diagnostics.Stopwatch? _recordingSw;

    DebugForm _debugForm = null!;

    // ── Constructeur ──────────────────────────────────────────────────────────

    public WhispForm(string modelPath)
    {
        _modelPath = modelPath;
        _trayIconIdle      = LoadTrayIcon(active: false);
        _trayIconRecording = LoadTrayIcon(active: true);

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
            Icon    = _trayIconIdle,
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

        // Le callback _onError est appelé depuis un thread de fond — BeginInvoke achemine
        // la mise à jour du tray sur le thread UI.
        _llm = new LlmService(onError: msg =>
        {
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = $"Whisp — {msg}";
                    _trayIcon.Icon = _trayIconIdle;
                });
        });

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
                        _trayIcon.Icon = _trayIconIdle;
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
        string wallClock = DateTime.Now.ToString("HH:mm:ss.fff");
        // Temps écoulé depuis le début de l'enregistrement, affiché uniquement quand
        // le chronomètre tourne (pas pour les logs INIT avant le premier enregistrement).
        string ts = (_recordingSw != null && _recordingSw.IsRunning)
            ? $"{wallClock} +{_recordingSw.Elapsed:hh\\:mm\\:ss\\.ff}"
            : wallClock;
        _debugForm.Log($"[{ts}] [{phase}] {message}");
    }

    // ── Démarrer l'enregistrement sur un thread de fond ───────────────────────

    void StartRecording()
    {
        _isRecording   = true;
        _stopRecording = false;
        _transcribedChunks.Clear();
        _pipeline = new BlockingCollection<float[]>();

        // Ouvrir la fenêtre de debug dès le premier appui pour que les logs
        // de Record() soient visibles. ShowWithoutActivation = true garantit que
        // Show() ne vole pas le focus — on ne call plus Activate() ici pour la
        // même raison : l'app cible doit garder le focus pendant l'enregistrement.
        _recordingSw = System.Diagnostics.Stopwatch.StartNew();
        _debugForm.Clear();
        _debugForm.Show();
        _debugForm.TopMost = true;
        DbgLog("RECORD", "Démarrage de l'enregistrement");

        _trayIcon.Text = "Whisp — Enregistrement...  (appuyer à nouveau pour arrêter)";
        _trayIcon.Icon = _trayIconRecording;

        // Thread d'enregistrement : pousse les chunks dans _pipeline au fil de la capture.
        // Quand l'enregistrement s'arrête, il complète le pipeline et met le tray à jour.
        var recordThread = new Thread(() =>
        {
            Record();
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _isRecording = false;
                    _trayIcon.Text = "Whisp — Transcription en cours...";
                });
        });
        recordThread.IsBackground = true;

        // Thread de transcription : démarre en même temps, consomme _pipeline en continu.
        // Bloque sur GetConsumingEnumerable() tant que le pipeline n'est pas complété.
        var transcribeThread = new Thread(Transcribe);
        transcribeThread.IsBackground = true;

        recordThread.Start();
        transcribeThread.Start();
    }

    // ── Transcription whisper + copie clipboard ───────────────────────────────
    //
    // Consomme le pipeline en continu dès le démarrage de l'enregistrement.
    // Bloque sur GetConsumingEnumerable() tant que Record() n'a pas appelé
    // _pipeline.CompleteAdding(). Chaque chunk transcrit est ajouté à _transcribedChunks.
    // La finalisation (clipboard, paste) se fait une seule fois, après le dernier chunk.

    void Transcribe()
    {
        // Le contexte Whisper est chargé une seule fois au démarrage (champ _ctx).
        // Si l'utilisateur transcrit avant la fin du chargement, on vide le pipeline
        // (pour ne pas bloquer Record()) et on refuse poliment.
        IntPtr ctx = _ctx;
        if (ctx == IntPtr.Zero)
        {
            foreach (var _ in _pipeline.GetConsumingEnumerable()) { }
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — Modèle non prêt";
                    _trayIcon.Icon = _trayIconIdle;
                    MessageBox.Show("Le modèle est encore en cours de chargement.\nRéessaie dans quelques secondes.",
                        "Whisp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            return;
        }

        // Paramètres de transcription — créés une fois, réutilisés pour chaque chunk.
        IntPtr fullParamsPtr = whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        whisper_free_params(fullParamsPtr);

        // StringToHGlobalAnsi : alloue une chaîne C en mémoire non managée.
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

        // Boucle de consommation : bloque jusqu'à ce que Record() appelle CompleteAdding().
        // Chaque chunk float[] est transcrit immédiatement à sa réception.
        int chunkIndex = 0;
        foreach (float[] chunk in _pipeline.GetConsumingEnumerable())
        {
            chunkIndex++;
            float chunkSec = (float)chunk.Length / 16_000f;
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} reçu — {chunk.Length} samples ({chunkSec:F1}s) → whisper_full...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int result = whisper_full(ctx, wparams, chunk, chunk.Length);
            sw.Stop();
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} — terminé en {sw.ElapsedMilliseconds} ms, code retour={result}");

            if (result != 0)
            {
                _debugForm.LogError($"[ERREUR] Chunk {chunkIndex} : whisper_full code {result}, chunk ignoré");
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

            _transcribedChunks.Add(chunkText);
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} → accepté ({chunkText.Length} chars). Buffer : {_transcribedChunks.Count} chunk(s)");

            // Mettre à jour initial_prompt pour le chunk suivant.
            // Whisper traite ce prompt comme de l'audio précédemment transcrit :
            // en lui passant la fin du chunk accepté, il continue sans coupure
            // au milieu d'une phrase à la frontière des 30s.
            // On libère l'ancien pointeur avant d'en allouer un nouveau.
            // 150 chars ≈ 30 tokens — largement sous la limite interne (~224 tokens).
            Marshal.FreeHGlobal(promptPtr);
            string tail = chunkText.Length > 150 ? chunkText[^150..] : chunkText;
            promptPtr = Marshal.StringToHGlobalAnsi(tail);
            wparams.initial_prompt = promptPtr;
        }

        Marshal.FreeHGlobal(langPtr);
        Marshal.FreeHGlobal(promptPtr);
        // ctx = _ctx : partagé entre les transcriptions, libéré dans Dispose().

        string fullText = string.Join(" ", _transcribedChunks).Trim();

        if (string.IsNullOrWhiteSpace(fullText))
        {
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — En attente";
                    _trayIcon.Icon = _trayIconIdle;
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
            string? rewritten = _llm.Rewrite(fullText);
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
            _trayIcon.Icon = _trayIconIdle;
        });

        _recordingSw?.Stop();
    }

    static Icon LoadTrayIcon(bool active)
    {
        string fileName = active
            ? "recording--indicator--true--32px.ico"
            : "recording--indicator--false--32px.ico";

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icons", fileName),
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return new Icon(path);
        }

        return (Icon)SystemIcons.Application.Clone();
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
            _trayIconIdle.Dispose();
            _trayIconRecording.Dispose();
            _debugForm.Dispose();
            _pipeline?.Dispose();
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

    // ── Enregistrement audio ─────────────────────────────────────────────────
    //
    // Capture le micro en continu. Dès que 30s d'audio sont accumulées,
    // convertit en float[] et pousse dans _pipeline pour que Transcribe()
    // puisse traiter le chunk immédiatement, sans attendre la fin de l'enregistrement.
    // Quand _stopRecording passe à true, les derniers octets restants forment
    // un chunk final (durée < 30s), puis le pipeline est complété.

    void Record()
    {
        // Constantes waveIn
        const uint WAVE_MAPPER    = 0xFFFFFFFF; // device audio par défaut du système
        const uint CALLBACK_EVENT = 0x00050000; // signaler un event quand un buffer est prêt
        const uint WHDR_DONE      = 0x00000001; // flag : le driver a fini d'écrire ce buffer
        const int  N_BUFFERS      = 4;          // nombre de buffers en rotation
        const int  BYTES_PER_BUF  = 16000 * 2 * 500 / 1000; // 500ms × 16kHz × 2 octets/sample
        const int  CHUNK_BYTES    = 30 * 16000 * 2;           // 30s × 16kHz × 2 octets/sample

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
            _pipeline.CompleteAdding(); // débloquer Transcribe() même en cas d'erreur
            return;
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

                    // Dès que 30s d'audio sont accumulées, extraire et envoyer au thread de transcription.
                    // La boucle while couvre le cas (rare) où deux chunks complets se forment
                    // entre deux passages de la boucle d'enregistrement.
                    while (allBytes.Count >= CHUNK_BYTES)
                    {
                        byte[] chunkBytes = allBytes.GetRange(0, CHUNK_BYTES).ToArray();
                        allBytes.RemoveRange(0, CHUNK_BYTES);
                        DbgLog("RECORD", $"Chunk 30s extrait — {CHUNK_BYTES / 32000.0:F1}s → pipeline");
                        _pipeline.Add(PcmToFloat(chunkBytes));
                    }

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

        // Dernier chunk : audio restant après le dernier chunk de 30s complet.
        // Durée variable (entre 0 et 30s). Toujours envoyé s'il contient des données.
        if (allBytes.Count > 0)
        {
            DbgLog("RECORD", $"Dernier chunk — {allBytes.Count / 32000.0:F1}s → pipeline");
            _pipeline.Add(PcmToFloat(allBytes.ToArray()));
        }

        // Signaler à Transcribe() que plus aucun chunk n'arrivera.
        // GetConsumingEnumerable() retournera après avoir vidé le pipeline.
        _pipeline.CompleteAdding();
        DbgLog("RECORD", "Pipeline complété — fin d'enregistrement");
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
