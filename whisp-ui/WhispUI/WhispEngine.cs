using System.Collections.Concurrent;
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

    // Pipeline producteur/consommateur entre Record() et Transcribe().
    // Recréé à chaque session (BlockingCollection ne peut être complété qu'une fois).
    private BlockingCollection<float[]> _pipeline = null!;

    // Accumule les textes transcrits chunk par chunk.
    // Survive à une exception dans Transcribe() — les chunks déjà traités sont récupérables.
    private readonly List<string> _transcribedChunks = new();

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
        _transcribedChunks.Clear();
        _pipeline = new BlockingCollection<float[]>();

        _recordingSw = System.Diagnostics.Stopwatch.StartNew();
        StatusChanged?.Invoke("Enregistrement...");

        var recordThread = new Thread(() =>
        {
            Record();
            _isRecording = false;
            StatusChanged?.Invoke("Transcription en cours...");
        });
        recordThread.IsBackground = true;

        var transcribeThread = new Thread(Transcribe);
        transcribeThread.IsBackground = true;

        recordThread.Start();
        transcribeThread.Start();
    }

    // ── Arrêter l'enregistrement (deuxième appui hotkey) ──────────────────────

    public void StopRecording()
    {
        // _pasteTarget a été capturé au Start — on ne le re-capture PAS ici.
        // Depuis l'introduction du HUD, GetForegroundWindow() au moment du Stop
        // peut retourner le HUD (activable malgré SW_SHOWNOACTIVATE) au lieu de
        // l'app d'origine. La cible du paste = l'app où l'utilisateur parlait,
        // captée une fois pour toutes au Start.
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
    // Capture le micro en continu. Dès que 30s d'audio sont accumulées,
    // convertit en float[] et pousse dans _pipeline pour traitement immédiat.
    // Quand _stopRecording passe à true, les octets restants forment un dernier chunk.

    private void Record()
    {
        const uint WAVE_MAPPER    = 0xFFFFFFFF;
        const uint CALLBACK_EVENT = 0x00050000;
        const uint WHDR_DONE      = 0x00000001;
        const int  N_BUFFERS      = 4;
        const int  BYTES_PER_BUF  = 16000 * 2 * 500 / 1000; // 500ms × 16kHz × 2 octets/sample
        const int  CHUNK_BYTES    = 30 * 16000 * 2;           // 30s × 16kHz × 2 octets/sample

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

        uint err = NativeMethods.waveInOpen(out IntPtr hWaveIn, WAVE_MAPPER, ref wfx, hEvent, IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            LogErrorLine?.Invoke($"[RECORD] waveInOpen erreur {err}");
            NativeMethods.CloseHandle(hEvent);
            _pipeline.CompleteAdding();
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
            NativeMethods.waveInPrepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
        }

        NativeMethods.waveInStart(hWaveIn);
        var allBytes = new List<byte>();
        DbgLog("RECORD", "Enregistrement démarré (16kHz mono PCM16)");

        int chunksExtracted = 0;
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

                    while (allBytes.Count >= CHUNK_BYTES)
                    {
                        byte[] chunkBytes = allBytes.GetRange(0, CHUNK_BYTES).ToArray();
                        allBytes.RemoveRange(0, CHUNK_BYTES);
                        chunksExtracted++;
                        DbgLog("RECORD", $"Chunk {chunksExtracted} extrait (30s) → pipeline");
                        _pipeline.Add(PcmToFloat(chunkBytes));
                    }

                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }

            if (bufferDoneCount > 1)
                DbgWarn($"RECORD: retard, {bufferDoneCount} buffers prêts simultanément");

            // Heartbeat ~5s, en Info → visible Full uniquement
            double curSec = allBytes.Count / 32000.0 + chunksExtracted * 30.0;
            if (curSec >= nextHeartbeatSec)
            {
                DbgVerbose("RECORD", $"+{curSec:F1}s capturés (en attente: {allBytes.Count}o)");
                nextHeartbeatSec += 5.0;
            }
        }
        DbgVerbose("RECORD", $"Boucle terminée — {allBytes.Count} octets accumulés ({allBytes.Count / 32000.0:F1}s)");

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

        if (allBytes.Count > 0)
        {
            chunksExtracted++;
            DbgLog("RECORD", $"Chunk final extrait ({allBytes.Count / 32000.0:F1}s)");
            _pipeline.Add(PcmToFloat(allBytes.ToArray()));
        }

        _pipeline.CompleteAdding();
        DbgLog("RECORD", $"Capture terminée — {chunksExtracted} chunk(s)");
    }

    // ── Transcription Whisper ─────────────────────────────────────────────────
    //
    // Consomme le pipeline en continu. Bloque sur GetConsumingEnumerable()
    // tant que Record() n'a pas appelé CompleteAdding().
    // Finalise dans le clipboard (+ LLM optionnel + paste).

    private void Transcribe()
    {
        IntPtr ctx = _ctx;
        if (ctx == IntPtr.Zero)
        {
            foreach (var _ in _pipeline.GetConsumingEnumerable()) { }
            StatusChanged?.Invoke("Modèle non prêt");
            TranscriptionFinished?.Invoke();
            return;
        }

        IntPtr fullParamsPtr = NativeMethods.whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        NativeMethods.whisper_free_params(fullParamsPtr);

        IntPtr langPtr   = Marshal.StringToHGlobalAnsi("fr");
        IntPtr promptPtr = Marshal.StringToHGlobalAnsi("Transcription en français.");
        wparams.language       = langPtr;
        wparams.initial_prompt = promptPtr;
        wparams.entropy_thold   = 1.9f;
        wparams.no_speech_thold = 0.7f;
        wparams.print_progress  = 0;

        // Patterns d'hallucination connus, retournés pour pouvoir logger lequel a matché.
        static string? MatchHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(vide)";
            string[] pats = { "Sous-titrage", "Radio-Canada", "[BLANK_AUDIO]", "Sous-titres" };
            foreach (var p in pats)
                if (text.Contains(p, StringComparison.OrdinalIgnoreCase)) return p;
            if (text.Contains("SRC", StringComparison.Ordinal)) return "SRC";
            return null;
        }

        // Détecte une répétition simple : sous-séquence de ≥4 mots qui revient ≥3 fois.
        // Heuristique grossière, juste pour avoir un signal dans les logs.
        static bool LooksRepeated(string text)
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

        int chunkIndex = 0;
        long transcribeMsTotal = 0;
        foreach (float[] chunk in _pipeline.GetConsumingEnumerable())
        {
            chunkIndex++;
            float chunkSec = (float)chunk.Length / 16_000f;
            DbgVerbose("TRANSCRIBE", $"Chunk {chunkIndex} reçu ({chunkSec:F1}s)");
            DbgVerbose("TRANSCRIBE", $"Chunk {chunkIndex} → whisper_full ({chunk.Length} samples)");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int result = NativeMethods.whisper_full(ctx, wparams, chunk, chunk.Length);
            sw.Stop();
            transcribeMsTotal += sw.ElapsedMilliseconds;
            DbgVerbose("TRANSCRIBE", $"Chunk {chunkIndex} → whisper_full code={result}, {sw.ElapsedMilliseconds} ms");

            if (result != 0)
            {
                LogErrorLine?.Invoke($"[ERREUR] Chunk {chunkIndex} : whisper_full code {result}, chunk ignoré");
                continue;
            }

            int nSeg = NativeMethods.whisper_full_n_segments(ctx);
            var parts = new List<string>();
            DbgVerbose("TRANSCRIBE", $"Chunk {chunkIndex} découpé en {nSeg} phrase(s) par Whisper :");
            for (int i = 0; i < nSeg; i++)
            {
                string segText = Marshal.PtrToStringUTF8(NativeMethods.whisper_full_get_segment_text(ctx, i)) ?? "";
                parts.Add(segText);
                long t0 = NativeMethods.whisper_full_get_segment_t0(ctx, i);
                long t1 = NativeMethods.whisper_full_get_segment_t1(ctx, i);
                float nsp = NativeMethods.whisper_full_get_segment_no_speech_prob(ctx, i);
                // no_speech : proba que le segment soit du silence/bruit (0 = parole sûre, 1 = silence sûr)
                DbgVerbose("TRANSCRIBE", $"   {i + 1}. [{t0/100.0:F1}s→{t1/100.0:F1}s, silence?={nsp:P0}] {segText.Trim()}");
            }
            string chunkText = string.Join(" ", parts).Trim();

            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} — texte recollé : {chunkText}");

            string? hallucPattern = MatchHallucination(chunkText);
            if (hallucPattern is not null)
            {
                DbgWarn($"Chunk {chunkIndex} filtré (hallucination: pattern='{hallucPattern}')");
                continue;
            }

            if (LooksRepeated(chunkText))
                DbgWarn($"Chunk {chunkIndex} suspect (répétition détectée)");

            _transcribedChunks.Add(chunkText);
            DbgVerbose("TRANSCRIBE", $"Chunk {chunkIndex} transcrit OK ({sw.ElapsedMilliseconds} ms, {nSeg} seg, {chunkText.Length} chars)");

            // Mémoire courte transmise au chunk suivant : Whisper lit ce texte avant
            // de transcrire les 30s suivantes, ce qui aide à garder le fil (orthographe,
            // contexte, langue). On lui donne les ~150 derniers caractères du chunk courant.
            Marshal.FreeHGlobal(promptPtr);
            string tail = chunkText.Length > 150 ? chunkText[^150..] : chunkText;
            promptPtr = Marshal.StringToHGlobalAnsi(tail);
            wparams.initial_prompt = promptPtr;
            DbgVerbose("TRANSCRIBE", $"Mémoire passée au chunk suivant : « {tail} »");
        }

        Marshal.FreeHGlobal(langPtr);
        Marshal.FreeHGlobal(promptPtr);

        string fullText = string.Join(" ", _transcribedChunks).Trim();

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

        _pipeline?.Dispose();

        if (_ctx != IntPtr.Zero)
        {
            NativeMethods.whisper_free(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
