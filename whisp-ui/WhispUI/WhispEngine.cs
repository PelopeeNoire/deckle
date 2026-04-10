using System.Runtime.InteropServices;

namespace WhispUI;

// ─── Moteur de transcription ──────────────────────────────────────────────────
//
// Port du moteur de WhispInteropTest (WhispForm) vers une classe autonome.
// Indépendant de tout framework UI — communique via des événements.
// Les événements peuvent être déclenchés depuis un thread de fond :
// les abonnés sont responsables du marshaling vers le thread UI.

internal sealed class WhispEngine : IDisposable
{
    // ── Événements ────────────────────────────────────────────────────────────

    // Déclenché depuis le thread de chargement ou depuis StartRecording/Transcribe.
    // L'abonné doit marshaler vers le thread UI via DispatcherQueue.TryEnqueue.
    public event Action<string>?  StatusChanged;

    // Logs textuels (thread de fond → LogWindow).
    public event Action<string>?  LogVerboseLine;
    public event Action<string>?  LogLine;
    public event Action<string>?  LogStepLine;
    public event Action<string>?  LogWarningLine;
    public event Action<string>?  LogErrorLine;

    // Levé en toute fin de Transcribe(), quel que soit le chemin de sortie
    // (modèle non prêt, texte vide, sortie normale). Utilisé par le HUD
    // pour se masquer. Thread de fond → abonné responsable du marshaling.
    public event Action?          TranscriptionFinished;

    // Rendez-vous synchrone juste avant PasteFromClipboard. L'appelant
    // (App.xaml.cs) y branche HudWindow.HideSync() pour garantir qu'aucune
    // mutation d'activation côté WhispUI ne survienne pendant que SendInput
    // est en vol vers la cible.
    public Action? OnReadyToPaste { get; set; }

    // ── Configuration ─────────────────────────────────────────────────────────

    const string MODEL_FILE     = "ggml-large-v3.bin";
    const string DEFAULT_MODEL  = @"D:\projects\ai\transcription\shared\" + MODEL_FILE;

    // ── État interne ──────────────────────────────────────────────────────────

    private readonly string     _modelPath;
    private readonly LlmService _llm;

    // volatile : interdit au compilateur de mettre ces valeurs en cache CPU.
    // Sans volatile, un thread de fond pourrait lire une valeur obsolète.
    private volatile IntPtr _ctx           = IntPtr.Zero;
    private volatile bool   _isRecording   = false;
    private volatile bool   _stopRecording = false;
    private volatile bool   _shouldPaste   = false;
    private volatile bool   _useLlm        = false;
    private volatile IntPtr _pasteTarget   = IntPtr.Zero;

    // Segments produits par Whisper pendant whisper_full() via le callback natif.
    // Accumulés au fil de l'eau depuis le thread d'inférence whisper.cpp — protégés
    // par lock car le callback tourne sur un thread différent du nôtre. Sert à la
    // fois de récupération progressive (logs) et de source pour le texte final.
    private readonly List<TranscribedSegment> _segments = new();
    private readonly object _segmentsLock = new();

    // Délégué stocké en champ d'instance pour empêcher le GC de le ramasser
    // pendant que whisper.cpp détient son pointeur natif (même piège que SubclassProc).
    private WhisperNewSegmentCallback? _newSegmentCallback;

    // Borne basse des ids de tokens timestamp pour le modèle courant. Cachée au début
    // de chaque Transcribe et lue par OnNewSegment pour filtrer les tokens non-texte.
    private int _tokenBeg;

    // t1 du segment précédent (centisecondes), pour calculer le gap inter-segments
    // dans OnNewSegment. Reset à -1 au début de chaque Transcribe — la 1re itération
    // affiche alors gap=+0,0s par convention. Lu/écrit uniquement depuis le thread
    // d'inférence whisper.cpp (callback séquentiel), pas besoin de lock.
    private long _lastSegmentT1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WhisperNewSegmentCallback(IntPtr ctx, IntPtr state, int n_new, IntPtr user_data);

    private readonly record struct TranscribedSegment(string Text, long T0, long T1, float NoSpeechProb);

    // Chronomètre démarré au début de chaque enregistrement (utilisé pour les logs).
    private System.Diagnostics.Stopwatch? _recordingSw;

    private bool _disposed;

    // ── Propriétés observables ─────────────────────────────────────────────────

    public bool IsReady     => _ctx != IntPtr.Zero;
    public bool IsRecording => _isRecording;

    // ── Constructeur ──────────────────────────────────────────────────────────

    public WhispEngine()
    {
        _modelPath = Environment.GetEnvironmentVariable("WHISP_MODEL_PATH") ?? DEFAULT_MODEL;

        _llm = new LlmService(
            onWarn: msg  => LogWarningLine?.Invoke($"[LLM] {msg}"),
            onInfo: msg  => LogLine?.Invoke(msg));

        LoadModelAsync();
    }

    // ── Chargement du modèle (thread de fond) ─────────────────────────────────

    private void LoadModelAsync()
    {
        StatusChanged?.Invoke("Chargement du modèle...");

        var t = new Thread(() =>
        {
            DebugLog.Write("ENGINE", "load thread started, path=" + _modelPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DbgLog("MODEL", $"path: {_modelPath}");
            if (File.Exists(_modelPath))
            {
                double mb = new FileInfo(_modelPath).Length / 1024.0 / 1024.0;
                DbgLog("MODEL", $"fichier: {mb:F1} MB");
            }
            else
            {
                DbgWarn($"MODEL: fichier introuvable sur disque ({_modelPath})");
            }
            DbgLog("MODEL", "init whisper_init_from_file_with_params (use_gpu=1)");

            IntPtr ctxParamsPtr = NativeMethods.whisper_context_default_params_by_ref();
            WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
            NativeMethods.whisper_free_context_params(ctxParamsPtr);
            ctxParams.use_gpu = 1;

            _ctx = NativeMethods.whisper_init_from_file_with_params(_modelPath, ctxParams);
            DebugLog.Write("ENGINE", "whisper_init_from_file returned ctx=" + _ctx);
            sw.Stop();

            if (_ctx == IntPtr.Zero)
            {
                LogErrorLine?.Invoke($"[INIT] Impossible de charger le modèle : {_modelPath}");
                StatusChanged?.Invoke("Erreur : modèle non chargé");
            }
            else
            {
                DbgStep($"Modèle chargé en {sw.ElapsedMilliseconds} ms");
                StatusChanged?.Invoke("En attente");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ── Démarrer l'enregistrement ─────────────────────────────────────────────

    public void StartRecording(bool useLlm, bool shouldPaste, IntPtr pasteTarget)
    {
        if (_isRecording) return;

        _isRecording   = true;
        _stopRecording = false;
        _shouldPaste   = shouldPaste;
        _useLlm        = useLlm;
        _pasteTarget   = pasteTarget;
        lock (_segmentsLock) _segments.Clear();

        _recordingSw = System.Diagnostics.Stopwatch.StartNew();
        StatusChanged?.Invoke("Enregistrement...");

        // Trace de la cible capturée au Start — symétrique du Step vert final.
        // Permet de voir, dès le départ, quelle fenêtre / quel contrôle recevra
        // le paste, et de diagnostiquer les cas où la cible Start est mauvaise.
        if (_pasteTarget != IntPtr.Zero)
        {
            DbgVerbose("HOTKEY", $"cible capturée au Start: {Win32Util.DescribeHwnd(_pasteTarget)}");
            string? focusClass = Win32Util.GetFocusedClass(_pasteTarget);
            DbgVerbose("HOTKEY", focusClass is null
                ? "contrôle focusé au Start: <aucun focus clavier détecté>"
                : $"contrôle focusé au Start: {focusClass}");
        }
        else
        {
            DbgVerbose("HOTKEY", "aucune cible capturée au Start (paste désactivé ou foreground = WhispUI)");
        }

        // Un seul thread de fond : Record puis Transcribe en séquence.
        // Whisper a besoin de la totalité de l'audio pour gérer son fenêtrage
        // interne — pas de parallélisme possible, et plus simple à débuguer.
        var worker = new Thread(() =>
        {
            float[] audio = Record();
            _isRecording = false;
            StatusChanged?.Invoke("Transcription en cours...");
            Transcribe(audio);
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // ── Arrêter l'enregistrement (deuxième appui hotkey) ──────────────────────

    public void StopRecording()
    {
        // Re-capture la cible au Stop pour gérer le cas "j'ai changé de champ
        // texte pendant l'enregistrement" : on veut coller dans le champ où
        // l'utilisateur se trouve AU MOMENT du Stop, pas celui de Start. Le
        // hotkey étant global, GetForegroundWindow() à cet instant renvoie
        // l'app où il est. Filet : si le foreground appartient à WhispUI lui-
        // même (HUD ou LogWindow activé par un clic), on garde la cible Start
        // — sinon on aurait un faux positif "collé dans nos propres logs".
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            if (pid != ownPid)
            {
                if (fg != _pasteTarget)
                    DbgVerbose("HOTKEY", $"cible mise à jour au Stop: {Win32Util.DescribeHwnd(fg)}");
                _pasteTarget = fg;
            }
            else
            {
                DbgVerbose("HOTKEY", $"foreground au Stop = WhispUI ({Win32Util.DescribeHwnd(fg)}), cible Start conservée");
            }
        }
        _stopRecording = true;
    }

    // ── Log horodaté ──────────────────────────────────────────────────────────

    private string FormatLine(string phase, string msg)
    {
        string wallClock = DateTime.Now.ToString("HH:mm:ss.fff");
        string ts = (_recordingSw != null && _recordingSw.IsRunning)
            ? $"{wallClock} +{_recordingSw.Elapsed:hh\\:mm\\:ss\\.ff}"
            : wallClock;
        return $"[{ts}] [{phase}] {msg}";
    }

    private void DbgLog(string phase, string msg)     => LogLine?.Invoke(FormatLine(phase, msg));
    private void DbgVerbose(string phase, string msg) => LogVerboseLine?.Invoke(FormatLine(phase, msg));
    private void DbgStep(string msg) => LogStepLine?.Invoke(msg);
    private void DbgWarn(string msg) => LogWarningLine?.Invoke(msg);

    // ── Enregistrement audio ──────────────────────────────────────────────────
    //
    // Capture le micro en continu dans un unique buffer redimensionnable.
    // Quand _stopRecording passe à true, retourne tout l'audio accumulé en float[]
    // (PCM16 → float [-1, 1]) pour être passé en un seul appel à whisper_full().
    // Whisper gère son propre fenêtrage interne (30s + seek dynamique) et la
    // propagation de contexte inter-fenêtres via tokens — pas de chunking ici.

    private float[] Record()
    {
        const uint WAVE_MAPPER    = 0xFFFFFFFF;
        const uint CALLBACK_EVENT = 0x00050000;
        const uint WHDR_DONE      = 0x00000001;
        const int  N_BUFFERS      = 4;
        const int  BYTES_PER_BUF  = 16000 * 2 * 500 / 1000; // 500ms × 16kHz × 2 octets/sample

        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,     // PCM non compressé
            nChannels       = 1,     // mono
            nSamplesPerSec  = 16000,
            nAvgBytesPerSec = 32000,
            nBlockAlign     = 2,
            wBitsPerSample  = 16,
            cbSize          = 0,
        };

        IntPtr hEvent = NativeMethods.CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, null);

        // Périphérique sélectionné dans les Settings. -1 = WAVE_MAPPER (défaut système).
        int configuredDevice = Settings.SettingsService.Instance.Current.Recording.AudioInputDeviceId;
        uint deviceId = configuredDevice < 0 ? WAVE_MAPPER : (uint)configuredDevice;

        uint err = NativeMethods.waveInOpen(out IntPtr hWaveIn, deviceId, ref wfx, hEvent, IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            LogErrorLine?.Invoke($"[RECORD] waveInOpen erreur {err}");
            NativeMethods.CloseHandle(hEvent);
            return Array.Empty<float>();
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
            NativeMethods.waveInPrepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
        }

        NativeMethods.waveInStart(hWaveIn);
        // Buffer unique, croît tout au long de l'enregistrement.
        // 1 sample = 2 octets PCM16. À 16 kHz, 1 minute = 1.92M octets.
        var allBytes = new List<byte>(capacity: 16000 * 2 * 60); // pré-réserve ~1 min
        DbgLog("RECORD", "Enregistrement démarré (16kHz mono PCM16)");

        double nextHeartbeatSec = 5.0;

        while (!_stopRecording)
        {
            NativeMethods.WaitForSingleObject(hEvent, 100);

            int bufferDoneCount = 0;
            for (int i = 0; i < N_BUFFERS; i++)
            {
                WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    bufferDoneCount++;
                    if (hdr.dwBytesRecorded == 0)
                    {
                        DbgWarn($"RECORD: buffer[{i}] vide reçu");
                    }
                    else
                    {
                        var data = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                        allBytes.AddRange(data);
                    }

                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }

            if (bufferDoneCount > 1)
                DbgWarn($"RECORD: retard, {bufferDoneCount} buffers prêts simultanément");

            // Heartbeat ~5s, Verbose → visible Full uniquement
            double curSec = allBytes.Count / 32000.0;
            if (curSec >= nextHeartbeatSec)
            {
                DbgVerbose("RECORD", $"+{curSec:F1}s capturés");
                nextHeartbeatSec += 5.0;
            }
        }

        NativeMethods.waveInStop(hWaveIn);
        Thread.Sleep(100);

        for (int i = 0; i < N_BUFFERS; i++)
        {
            WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
            if ((hdr.dwFlags & WHDR_DONE) != 0 && hdr.dwBytesRecorded > 0)
            {
                var data = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                allBytes.AddRange(data);
            }
            NativeMethods.waveInUnprepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            Marshal.FreeHGlobal(bufPtrs[i]);
            Marshal.FreeHGlobal(hdrPtrs[i]);
        }

        NativeMethods.waveInClose(hWaveIn);
        NativeMethods.CloseHandle(hEvent);

        double totalSec = allBytes.Count / 32000.0;
        DbgLog("RECORD", $"Capture terminée — {totalSec:F1}s d'audio ({allBytes.Count} octets)");

        return PcmToFloat(allBytes.ToArray());
    }

    // ── Transcription Whisper ─────────────────────────────────────────────────
    //
    // Appel monobloc : tout l'audio passe en une fois à whisper_full(), qui gère
    // son propre fenêtrage interne (30s + seek dynamique) et la propagation de
    // contexte inter-fenêtres via tokens. Pas de chunking côté C#.
    //
    // Récupération progressive via new_segment_callback : whisper.cpp invoque le
    // callback à chaque nouveau segment validé pendant le décodage, sur SON thread
    // d'inférence — d'où le lock sur _segments. Le texte final est assemblé à
    // partir de ces segments à la fin de l'appel.

    // Détecte une répétition simple : sous-séquence de ≥4 mots qui revient ≥3 fois.
    // Conservée en log-only (warning) pour signal de diagnostic — ne filtre rien.
    private static bool LooksRepeated(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 12) return false;
        for (int n = 4; n <= 8; n++)
        {
            var counts = new Dictionary<string, int>();
            for (int i = 0; i + n <= words.Length; i++)
            {
                var key = string.Join(' ', words, i, n);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (counts[key] >= 3) return true;
            }
        }
        return false;
    }

    private void OnNewSegment(IntPtr ctx, IntPtr state, int n_new, IntPtr user_data)
    {
        // n_new = nb de segments produits depuis le dernier appel ; ils se trouvent
        // à la fin de la liste totale exposée par whisper_full_n_segments.
        try
        {
            int total = NativeMethods.whisper_full_n_segments(ctx);
            int from  = total - n_new;
            // Borne basse des ids de tokens timestamp — au-delà, ce sont des <|t.tt|>,
            // pas des tokens texte. Cachée par appel Transcribe pour éviter des P/Invoke
            // répétés inutiles (dépend du modèle, pas du segment).
            int tokenBeg = _tokenBeg;
            for (int i = from; i < total; i++)
            {
                string segText = Marshal.PtrToStringUTF8(NativeMethods.whisper_full_get_segment_text(ctx, i)) ?? "";
                long  t0  = NativeMethods.whisper_full_get_segment_t0(ctx, i);
                long  t1  = NativeMethods.whisper_full_get_segment_t1(ctx, i);
                float nsp = NativeMethods.whisper_full_get_segment_no_speech_prob(ctx, i);

                // Confiance par segment, agrégée sur les seuls tokens texte.
                // p = proba linéaire du token tel qu'échantillonné par Whisper.
                // avg = signal "phrase globalement sûre ?", min = "maillon faible / mot bricolé ?".
                int nTok = NativeMethods.whisper_full_n_tokens(ctx, i);
                float sumP = 0f, minP = 1f;
                int textTok = 0;
                for (int k = 0; k < nTok; k++)
                {
                    int id = NativeMethods.whisper_full_get_token_id(ctx, i, k);
                    if (id >= tokenBeg) continue; // skip tokens timestamp
                    float p = NativeMethods.whisper_full_get_token_p(ctx, i, k);
                    sumP += p;
                    if (p < minP) minP = p;
                    textTok++;
                }
                float avgP = textTok > 0 ? sumP / textTok : 0f;
                if (textTok == 0) minP = 0f; // segment sans token texte → min "indéfini"

                lock (_segmentsLock)
                    _segments.Add(new TranscribedSegment(segText, t0, t1, nsp));

                // dur = durée du segment, gap = silence (ou recouvrement) avec le précédent.
                // Sur une boucle d'hallucination type, on observe dur≈3,0s contiguë (gap=+0,0s)
                // de façon métronomique — pattern visuellement repérable sans calcul mental.
                // Un gros gap signale un saut de Whisper ou un blanc en entrée (à risque).
                double dur = (t1 - t0) / 100.0;
                double gap = _lastSegmentT1 < 0 ? 0.0 : (t0 - _lastSegmentT1) / 100.0;
                _lastSegmentT1 = t1;

                // no_speech : proba que le segment soit du silence/bruit (0 = parole sûre, 1 = silence sûr).
                // p̄ / min : confiance moyenne et minimale sur les tokens texte du segment.
                // t0/t1 sont en centisecondes (1 unité = 10 ms) côté whisper.cpp.
                DbgVerbose("TRANSCRIBE", $"seg #{i + 1} [{t0 / 100.0:F1}s→{t1 / 100.0:F1}s dur={dur:F1}s gap={(gap >= 0 ? "+" : "")}{gap:F1}s, nsp={nsp:P0}, p̄={avgP:F2} min={minP:F2}, {textTok}/{nTok} tok] {segText.Trim()}");
            }
        }
        catch (Exception ex)
        {
            // Ne JAMAIS laisser une exception traverser la frontière managed→native.
            LogErrorLine?.Invoke($"[CALLBACK] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Transcribe(float[] audio)
    {
        IntPtr ctx = _ctx;
        if (ctx == IntPtr.Zero)
        {
            StatusChanged?.Invoke("Modèle non prêt");
            TranscriptionFinished?.Invoke();
            return;
        }

        if (audio.Length == 0)
        {
            DbgWarn("TRANSCRIBE: buffer audio vide, rien à transcrire");
            StatusChanged?.Invoke("En attente");
            TranscriptionFinished?.Invoke();
            return;
        }

        IntPtr fullParamsPtr = NativeMethods.whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        NativeMethods.whisper_free_params(fullParamsPtr);

        wparams.print_progress = 0;

        // Snapshot des settings utilisateur au début de la transcription.
        // Les champs hot-reload (seuils, VAD, suppress, contexte, décodage)
        // sont appliqués ici à chaque appel — aucune relance de contexte.
        // Les réglages lourds (modèle, use_gpu) sont gérés au LoadModelAsync.
        var settings = Settings.SettingsService.Instance.Current;
        var nativeAllocs = Settings.WhisperParamsMapper.Apply(ref wparams, settings);

        // Cache la borne des tokens timestamp une fois pour tout l'appel — c'est une
        // propriété du modèle, pas du segment, pas la peine d'appeler à chaque token.
        _tokenBeg = NativeMethods.whisper_token_beg(ctx);
        _lastSegmentT1 = -1;

        // Branchement du callback natif. Délégué stocké en champ d'instance pour
        // empêcher le GC de le ramasser pendant que whisper.cpp détient le pointeur.
        _newSegmentCallback = OnNewSegment;
        wparams.new_segment_callback = Marshal.GetFunctionPointerForDelegate(_newSegmentCallback);
        wparams.new_segment_callback_user_data = IntPtr.Zero;

        float audioSec = (float)audio.Length / 16_000f;
        DbgLog("TRANSCRIBE", $"Audio reçu ({audioSec:F1}s, {audio.Length} samples) → whisper_full");
        DbgVerbose("TRANSCRIBE", $"params: temp={wparams.temperature:F2} +{wparams.temperature_inc:F2} | logprob_thold={wparams.logprob_thold:F2} | entropy_thold={wparams.entropy_thold:F2} | no_speech_thold={wparams.no_speech_thold:F2} | suppress_nst={wparams.suppress_nst} | n_threads={wparams.n_threads}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int result = NativeMethods.whisper_full(ctx, wparams, audio, audio.Length);
        sw.Stop();
        long transcribeMsTotal = sw.ElapsedMilliseconds;

        nativeAllocs.Free();

        if (result != 0)
        {
            LogErrorLine?.Invoke($"[ERREUR] whisper_full code {result}");
            StatusChanged?.Invoke("Erreur transcription");
            TranscriptionFinished?.Invoke();
            return;
        }

        // Assemble le texte final à partir des segments accumulés par le callback.
        // On pourrait aussi re-itérer whisper_full_n_segments(ctx) ici, mais passer
        // par _segments garantit qu'un segment loggé est exactement un segment du
        // texte final — pas de divergence possible entre les deux sources.
        string fullText;
        int nSeg;
        lock (_segmentsLock)
        {
            nSeg = _segments.Count;
            fullText = string.Join(" ", _segments.Select(s => s.Text)).Trim();
        }

        DbgLog("TRANSCRIBE", $"whisper_full OK ({transcribeMsTotal} ms, {nSeg} segments, {fullText.Length} chars)");

        if (LooksRepeated(fullText))
            DbgWarn("TRANSCRIBE: répétition détectée dans le texte (signal heuristique, aucun filtrage)");

        if (string.IsNullOrWhiteSpace(fullText))
        {
            StatusChanged?.Invoke("En attente");
            TranscriptionFinished?.Invoke();
            return;
        }

        // Copie systématique du texte brut — filet de sécurité même si le LLM échoue
        var swClip = System.Diagnostics.Stopwatch.StartNew();
        CopyToClipboard(fullText);
        swClip.Stop();

        long llmMs = 0;
        if (_useLlm)
        {
            var swLlm = System.Diagnostics.Stopwatch.StartNew();
            string? rewritten = _llm.Rewrite(fullText);
            swLlm.Stop();
            llmMs = swLlm.ElapsedMilliseconds;
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                fullText = rewritten;
                CopyToClipboard(fullText);
            }
        }

        long pasteMs = 0;
        bool pasteVerified = false;
        if (_shouldPaste)
        {
            // Rendez-vous synchrone : l'handler (App) cache le HUD et ne rend
            // la main qu'une fois SW_HIDE effectif sur le thread UI. Après ce
            // point, plus rien dans WhispUI ne touche à l'activation jusqu'à
            // la fin de Transcribe — la livraison du Ctrl+V est protégée.
            OnReadyToPaste?.Invoke();
            DbgVerbose("PASTE", "HUD masqué (HideSync) — prêt à coller");
            var swPaste = System.Diagnostics.Stopwatch.StartNew();
            pasteVerified = PasteFromClipboard();
            swPaste.Stop();
            pasteMs = swPaste.ElapsedMilliseconds;
        }

        double totalSec = (_recordingSw?.Elapsed.TotalSeconds) ?? 0;
        string recap = $"total {totalSec:F1}s (trans {transcribeMsTotal}/llm {llmMs}/clip {swClip.ElapsedMilliseconds}/paste {pasteMs} ms)";
        if (_shouldPaste && pasteVerified)
            DbgStep($"Bout en bout OK — {fullText.Length} chars collés dans {Win32Util.DescribeHwnd(_pasteTarget)} ({recap})");
        else
            DbgVerbose("DONE", recap);

        StatusChanged?.Invoke("En attente");
        _recordingSw?.Stop();
        TranscriptionFinished?.Invoke();
    }

    // ── Presse-papier ─────────────────────────────────────────────────────────

    private void CopyToClipboard(string text)
    {
        const uint GMEM_MOVEABLE  = 0x0002;
        const uint CF_UNICODETEXT = 13;

        int byteCount = (text.Length + 1) * 2;

        IntPtr hMem = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        DbgVerbose("CLIPBOARD", $"GlobalAlloc {byteCount}o → hMem={hMem}");
        if (hMem == IntPtr.Zero) { DbgWarn("Clipboard: GlobalAlloc échoué"); return; }

        IntPtr ptr = NativeMethods.GlobalLock(hMem);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0);
        NativeMethods.GlobalUnlock(hMem);

        bool opened = NativeMethods.OpenClipboard(IntPtr.Zero);
        DbgVerbose("CLIPBOARD", $"OpenClipboard → {opened}");
        if (!opened) { DbgWarn("Clipboard: OpenClipboard échoué"); return; }

        NativeMethods.EmptyClipboard();
        IntPtr setHandle = NativeMethods.SetClipboardData(CF_UNICODETEXT, hMem);
        if (setHandle == IntPtr.Zero) DbgWarn("Clipboard: SetClipboardData échoué (handle 0)");
        NativeMethods.CloseClipboard();

        // Re-lecture immédiate pour vérifier que c'est bien dans le presse-papier
        if (NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            IntPtr h = NativeMethods.GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero)
            {
                DbgWarn("Clipboard: vérif post-copie → aucune donnée Unicode");
            }
            else
            {
                IntPtr p = NativeMethods.GlobalLock(h);
                string? back = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                NativeMethods.GlobalUnlock(h);
                if (back is null || back.Length != text.Length)
                    DbgWarn($"Clipboard: vérif post-copie → longueur {back?.Length ?? -1} != {text.Length}");
            }
            NativeMethods.CloseClipboard();
        }

        DbgLog("CLIPBOARD", $"Texte copié ({text.Length} chars)");
    }

    // Retourne true uniquement si toutes les conditions de vérification sont
    // réunies (cible valide, hors WhispUI, foreground restauré, focus clavier
    // sur un contrôle plausiblement texte, SendInput intégral). Sinon false :
    // l'appelant émet alors un Warn avec mode opératoire pour collage manuel.
    private bool PasteFromClipboard()
    {
        const uint   INPUT_KEYBOARD  = 1;
        const uint   KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL      = 0x11;
        const ushort VK_V            = 0x56;

        DbgVerbose("PASTE", $"cible attendue: {Win32Util.DescribeHwnd(_pasteTarget)}");
        IntPtr fgBefore = NativeMethods.GetForegroundWindow();
        DbgVerbose("PASTE", $"foreground avant: {Win32Util.DescribeHwnd(fgBefore)}");

        if (_pasteTarget == IntPtr.Zero)
        {
            DbgWarn("PASTE refusé: aucune cible enregistrée. Le presse-papier contient le texte — colle manuellement avec Ctrl+V.");
            return false;
        }

        // Refus si la cible est une fenêtre de WhispUI lui-même (LogWindow, HUD…).
        // Évite le faux positif où on a "collé" dans nos propres logs.
        NativeMethods.GetWindowThreadProcessId(_pasteTarget, out uint targetPid);
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        if (targetPid == ownPid)
        {
            DbgWarn($"PASTE refusé: la cible appartient à WhispUI ({Win32Util.DescribeHwnd(_pasteTarget)}). Le presse-papier contient le texte — colle manuellement avec Ctrl+V dans la bonne fenêtre.");
            return false;
        }

        bool sfgOk = NativeMethods.SetForegroundWindow(_pasteTarget);
        DbgVerbose("PASTE", $"SetForegroundWindow → {sfgOk}");
        Thread.Sleep(50);

        IntPtr fgAfter = NativeMethods.GetForegroundWindow();
        if (fgAfter != _pasteTarget)
        {
            DbgWarn($"PASTE refusé: focus pas restauré (attendu {Win32Util.DescribeHwnd(_pasteTarget)}, actuel {Win32Util.DescribeHwnd(fgAfter)}). Le presse-papier contient le texte — colle manuellement avec Ctrl+V.");
            return false;
        }

        string? focusClass = Win32Util.GetFocusedClass(_pasteTarget);
        if (focusClass is null)
        {
            DbgWarn("PASTE refusé: la cible n'a pas de focus clavier sur un contrôle texte. Le presse-papier contient le texte — clique dans un champ texte puis Ctrl+V.");
            return false;
        }
        DbgVerbose("PASTE", $"contrôle focusé: {focusClass}");

        int cbSize = Marshal.SizeOf<INPUT>();

        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V,       ki_dwFlags = KEYEVENTF_KEYUP },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL, ki_dwFlags = KEYEVENTF_KEYUP },
        };

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, cbSize);
        if (sent != inputs.Length)
        {
            DbgWarn($"PASTE partiel: SendInput a injecté {sent}/{inputs.Length} events. Le presse-papier contient le texte — colle manuellement avec Ctrl+V.");
            return false;
        }

        DbgLog("PASTE", $"Ctrl+V envoyé à {Win32Util.DescribeHwnd(_pasteTarget)} (focus={focusClass})");
        // ↑ Info bleu : c'est l'événement "j'ai vraiment tiré le coup", important.
        return true;
    }

    // ── Conversion PCM → float ────────────────────────────────────────────────

    private static float[] PcmToFloat(byte[] pcm)
    {
        int n = pcm.Length / 2;
        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            result[i] = s / 32768.0f;
        }
        return result;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ctx != IntPtr.Zero)
        {
            NativeMethods.whisper_free(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
