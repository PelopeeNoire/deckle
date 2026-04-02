// Étape 6 — 4 raccourcis avec comportements distincts
//
// Alt+`              → transcription → clipboard uniquement
// Alt+Shift+`        → transcription → clipboard + collage auto
// Alt+Ctrl+`         → transcription → réécriture LLM → clipboard (TODO)
// Alt+Ctrl+Shift+`   → transcription → réécriture LLM → clipboard + collage auto (TODO)
//
// UX toggle : premier appui = démarrer, deuxième appui = arrêter + transcrire

using System.Net.Http;
using System.Net.Http.Headers;
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

Application.EnableVisualStyles();
Application.Run(new WhispForm(modelPath));

// ─── Form caché — reçoit WM_HOTKEY, gère le tray ─────────────────────────────

class WhispForm : Form
{
    // ── Constantes Windows ────────────────────────────────────────────────────

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
    const uint MOD_SHIFT   = 0x0004; // touche Shift
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
            Text    = "Whisp — En attente",
            Visible = true
        };

        // Menu contextuel : clic droit sur l'icône
        var menu = new ContextMenuStrip();
        menu.Items.Add("Quitter", null, (_, _) => Application.Exit());
        _trayIcon.ContextMenuStrip = menu;

        // Enregistrer les 4 raccourcis globaux.
        // this.Handle force la création du handle Windows (identifiant numérique
        // que Windows utilise pour envoyer les messages WM_HOTKEY à notre fenêtre).
        // Le | combine les masques de modificateurs : MOD_ALT | MOD_SHIFT = Alt+Shift.
        RegisterHotKey(this.Handle, HOTKEY_ID_ALT,      MOD_ALT,                 VK_OEM_3);
        RegisterHotKey(this.Handle, HOTKEY_ID_ALT_CTRL, MOD_ALT | MOD_CONTROL,   VK_OEM_3);
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

    // ── Démarrer l'enregistrement sur un thread de fond ───────────────────────

    void StartRecording()
    {
        _isRecording   = true;
        _stopRecording = false;

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
        // Écrire le WAV sur disque (utile pour vérifier l'enregistrement si besoin)
        string tempWav = Path.Combine(AppContext.BaseDirectory, "temp.wav");
        WriteWav(tempWav, rawPcm);

        float[] samples = PcmToFloat(rawPcm);

        // Charger le modèle avec GPU activé
        IntPtr ctxParamsPtr = whisper_context_default_params_by_ref();
        WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
        whisper_free_context_params(ctxParamsPtr);
        ctxParams.use_gpu = 1;

        IntPtr ctx = whisper_init_from_file_with_params(_modelPath, ctxParams);
        if (ctx == IntPtr.Zero)
        {
            if (!this.IsDisposed)
                this.BeginInvoke(() =>
                {
                    _trayIcon.Text = "Whisp — Erreur : modèle non chargé";
                    _trayIcon.Icon = SystemIcons.Application;
                });
            return;
        }

        // Paramètres de transcription
        IntPtr fullParamsPtr = whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        whisper_free_params(fullParamsPtr);

        // StringToHGlobalAnsi : alloue une chaîne C (bytes ASCII) en mémoire non managée.
        // Obligatoire pour passer une chaîne à une DLL C.
        IntPtr langPtr         = Marshal.StringToHGlobalAnsi("fr");
        IntPtr promptPtr       = Marshal.StringToHGlobalAnsi("Transcription en français.");
        wparams.language       = langPtr;
        wparams.initial_prompt = promptPtr;
        // entropy_thold : défaut 2.4, descendu à 1.9.
        // Mesure le désordre de la distribution de probabilité sur les tokens.
        // Une valeur haute tolère les sorties incohérentes (répétitions, hallucinations).
        // À 1.9 Whisper rejette plus tôt les segments bruités ou répétitifs.
        wparams.entropy_thold  = 1.9f;
        // no_speech_thold : défaut 0.6, conservé — segments à faible confiance déjà filtrés.
        wparams.print_progress = 0;

        int result = whisper_full(ctx, wparams, samples, samples.Length);
        Marshal.FreeHGlobal(langPtr);
        Marshal.FreeHGlobal(promptPtr);

        // Patterns parasites injectés par Whisper sur silence ou fin d'audio.
        // Si le texte ne contient que ces tokens, il est rejeté (clipboard inchangé).
        static bool IsHallucinatedOutput(string text) =>
            string.IsNullOrWhiteSpace(text) ||
            text.Contains("Sous-titrage", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radio-Canada",  StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Sous-titres",   StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SRC",           StringComparison.Ordinal);

        if (result == 0)
        {
            int nSeg = whisper_full_n_segments(ctx);
            var parts = new List<string>();
            for (int i = 0; i < nSeg; i++)
                parts.Add(Marshal.PtrToStringUTF8(whisper_full_get_segment_text(ctx, i)) ?? "");

            string fullText = string.Join(" ", parts).Trim();

            if (IsHallucinatedOutput(fullText))
                return; // rien à coller, sortie silencieuse

            // Filet clipboard : le brut est dans le clipboard avant l'appel LLM.
            // Si le LLM plante ou retourne du vide, l'utilisateur récupère au moins le brut.
            CopyToClipboard(fullText);

            // Réécriture LLM : point d'accroche pour les raccourcis Alt+Ctrl+` et Alt+Ctrl+Shift+`
            if (_useLlm)
            {
                string? rewritten = RewriteWithLlm(fullText);
                if (!string.IsNullOrWhiteSpace(rewritten))
                {
                    fullText = rewritten;
                    CopyToClipboard(fullText);   // remplace le brut par le réécrit
                }
                // Si LLM indisponible ou vide → le clipboard garde le brut, rien à faire
            }

            // Collage auto : toujours actif pour les deux raccourcis
            if (_shouldPaste)
                PasteFromClipboard();
        }

        whisper_free(ctx);

        if (!this.IsDisposed)
            this.BeginInvoke(() =>
            {
                _trayIcon.Text = "Whisp — En attente";
                _trayIcon.Icon = SystemIcons.Application;
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
        }
        base.Dispose(disposing);
    }

    // ─── P/Invoke : user32.dll — SendInput ───────────────────────────────────
    //
    // SendInput : injecte des événements clavier/souris dans la file de messages Windows.
    // Remplace l'API keybd_event (dépréciée depuis Windows Vista).
    //
    // nInputs  : nombre d'éléments dans le tableau pInputs
    // pInputs  : tableau de structures INPUT décrivant chaque événement
    // cbSize   : taille en octets d'une seule structure INPUT (pour validation interne)
    // Retour   : nombre d'événements effectivement injectés (0 si bloqué par UIPI)

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ─── P/Invoke : user32.dll — gestion du focus ────────────────────────────

    // GetForegroundWindow : retourne le handle de la fenêtre actuellement au premier plan
    // (celle qui reçoit les frappes clavier de l'utilisateur).
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // SetForegroundWindow : demande à Windows de mettre la fenêtre hWnd au premier plan.
    // Nécessite que le process appelant ait le droit de changer le focus
    // (accordé temporairement après traitement d'un hotkey).
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    // ─── P/Invoke : user32.dll — hotkey ──────────────────────────────────────
    //
    // RegisterHotKey : demande à Windows d'envoyer WM_HOTKEY à notre fenêtre
    // quand la combinaison de touches est pressée, quelle que soit l'application active.
    //
    // hWnd       : handle de notre fenêtre — qui recevra le message WM_HOTKEY
    // id         : identifiant arbitraire pour distinguer plusieurs hotkeys
    // fsModifiers : masque de modificateurs (MOD_ALT, MOD_CTRL, MOD_SHIFT, MOD_WIN)
    // vk         : code virtuel de la touche principale

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // UnregisterHotKey : libère le raccourci (important : un raccourci non libéré
    // reste bloqué pour toutes les autres applis tant que le process tourne)
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ─── P/Invoke : user32.dll — presse-papier ────────────────────────────────

    [DllImport("user32.dll")]
    static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    // ─── P/Invoke : winmm.dll ────────────────────────────────────────────────

    [DllImport("winmm.dll")]
    static extern uint waveInOpen(
        out IntPtr phwi, uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    static extern uint waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    static extern uint waveInClose(IntPtr hwi);

    // ─── P/Invoke : kernel32.dll ─────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr hMem);

    // ─── P/Invoke : libwhisper.dll ────────────────────────────────────────────

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_context_default_params_by_ref();

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free_context_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_init_from_file_with_params(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        WhisperContextParams cparams);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_full_default_params_by_ref(int strategy);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern int whisper_full(IntPtr ctx, WhisperFullParams wparams, float[] samples, int n_samples);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern int whisper_full_n_segments(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    static extern void whisper_free(IntPtr ctx);

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

                    // Remettre le buffer dans la file du driver.
                    // On efface WHDR_DONE (0x1) mais on garde WHDR_PREPARED (0x2).
                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }
        }

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

// ─── Structs ──────────────────────────────────────────────────────────────────
// Placées après la classe (règle top-level statements : les types viennent après le code)

// INPUT aplati : représente un événement clavier pour SendInput.
//
// La struct Windows INPUT contient une union C (clavier, souris, matériel).
// Ici on l'aplatit pour éviter les problèmes de layout avec struct imbriquée.
//
// Offsets sur Windows 64 bits :
//   0  : type (uint, 4 octets) — 1 = clavier
//   4  : padding (4 octets, alignement imposé par IntPtr dans la suite)
//   8  : ki.wVk (ushort, 2 octets) — code virtuel de la touche
//   10 : ki.wScan (ushort, 2 octets) — scan code matériel (0 si on utilise wVk)
//   12 : ki.dwFlags (uint, 4 octets) — 0 = enfoncer, 0x0002 = relâcher
//   16 : ki.time (uint, 4 octets) — horodatage (0 = Windows gère)
//   20 : ki.dwExtraInfo (IntPtr, 8 octets) — données extra (0 ici)
// Taille totale : 28 octets
// INPUT aplati pour SendInput.
//
// Taille totale sur Windows 64 bits = 40 octets :
//   offset  0 : type (uint, 4 octets)
//   offset  4 : padding (4 octets — alignement 8 imposé par les IntPtr dans l'union)
//   offset  8 : début de l'union  ← KEYBDINPUT et MOUSEINPUT partagent cet espace
//   offset 40 : fin
//
// L'union est dimensionnée par MOUSEINPUT (le plus grand membre) :
//   dx(4) + dy(4) + mouseData(4) + dwFlags(4) + time(4) + padding(4) + dwExtraInfo(8) = 32 octets
//
// KEYBDINPUT.dwExtraInfo est à l'offset 16 dans KEYBDINPUT (padding interne après time),
// soit offset 24 dans INPUT (8 + 16).
//
// Le champ _pad à l'offset 32 force Marshal.SizeOf à retourner 40
// (8 octets à partir de l'offset 32 → fin à 40, multiple de 8 ✓).
[StructLayout(LayoutKind.Explicit)]
struct INPUT
{
    [FieldOffset(0)]  public uint   type;
    [FieldOffset(8)]  public ushort ki_wVk;
    [FieldOffset(10)] public ushort ki_wScan;
    [FieldOffset(12)] public uint   ki_dwFlags;
    [FieldOffset(16)] public uint   ki_time;
    [FieldOffset(24)] public IntPtr ki_dwExtraInfo;
    [FieldOffset(32)] public long   _pad;            // padding pour atteindre 40 octets (taille MOUSEINPUT)
}

[StructLayout(LayoutKind.Sequential)]
struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint   nSamplesPerSec;
    public uint   nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
struct WAVEHDR
{
    public IntPtr lpData;           // pointeur vers le buffer de données audio
    public uint   dwBufferLength;   // taille totale du buffer (octets)
    public uint   dwBytesRecorded;  // octets effectivement écrits par le driver
    public IntPtr dwUser;           // donnée utilisateur libre (non utilisé ici)
    public uint   dwFlags;          // flags : WHDR_DONE = buffer rempli par le driver
    public uint   dwLoops;          // nombre de boucles (lecture seulement)
    public IntPtr lpNext;           // usage interne driver
    public IntPtr reserved;         // usage interne driver
}

[StructLayout(LayoutKind.Sequential)]
struct WhisperContextParams
{
    public byte    use_gpu;
    public byte    flash_attn;
    public int     gpu_device;
    public byte    dtw_token_timestamps;
    public int     dtw_aheads_preset;
    public int     dtw_n_top;
    public UIntPtr dtw_aheads_n_heads;
    public IntPtr  dtw_aheads_heads;
    public UIntPtr dtw_mem_size;
}

[StructLayout(LayoutKind.Sequential)]
struct WhisperFullParams
{
    public int   strategy;
    public int   n_threads;
    public int   n_max_text_ctx;
    public int   offset_ms;
    public int   duration_ms;
    public byte  translate;
    public byte  no_context;
    public byte  no_timestamps;
    public byte  single_segment;
    public byte  print_special;
    public byte  print_progress;
    public byte  print_realtime;
    public byte  print_timestamps;
    public byte  token_timestamps;
    public float thold_pt;
    public float thold_ptsum;
    public int   max_len;
    public byte  split_on_word;
    public int   max_tokens;
    public byte  debug_mode;
    public int   audio_ctx;
    public byte  tdrz_enable;
    public IntPtr suppress_regex;
    public IntPtr initial_prompt;
    public byte   carry_initial_prompt;
    public IntPtr prompt_tokens;
    public int    prompt_n_tokens;
    public IntPtr language;
    public byte   detect_language;
    public byte  suppress_blank;
    public byte  suppress_nst;
    public float temperature;
    public float max_initial_ts;
    public float length_penalty;
    public float temperature_inc;
    public float entropy_thold;
    public float logprob_thold;
    public float no_speech_thold;
    public int   greedy_best_of;
    public int   beam_search_beam_size;
    public float beam_search_patience;
    public IntPtr new_segment_callback;
    public IntPtr new_segment_callback_user_data;
    public IntPtr progress_callback;
    public IntPtr progress_callback_user_data;
    public IntPtr encoder_begin_callback;
    public IntPtr encoder_begin_callback_user_data;
    public IntPtr abort_callback;
    public IntPtr abort_callback_user_data;
    public IntPtr logits_filter_callback;
    public IntPtr logits_filter_callback_user_data;
    public IntPtr  grammar_rules;
    public UIntPtr n_grammar_rules;
    public UIntPtr i_start_rule;
    public float   grammar_penalty;
    public byte   vad;
    public IntPtr vad_model_path;
    public float vad_threshold;
    public int   vad_min_speech_duration_ms;
    public int   vad_min_silence_duration_ms;
    public float vad_max_speech_duration_s;
    public int   vad_speech_pad_ms;
    public float vad_samples_overlap;
}
