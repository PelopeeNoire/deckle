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
    public event Action<string>?  LogLine;
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

        _llm = new LlmService(onError: msg => LogErrorLine?.Invoke($"[LLM] {msg}"));

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
            LogLine?.Invoke($"[INIT] Chargement du modèle ({Path.GetFileName(_modelPath)})...");

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
                LogLine?.Invoke($"[INIT] Modèle chargé en {sw.ElapsedMilliseconds} ms — prêt");
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

    private void DbgLog(string phase, string msg)
    {
        string wallClock = DateTime.Now.ToString("HH:mm:ss.fff");
        string ts = (_recordingSw != null && _recordingSw.IsRunning)
            ? $"{wallClock} +{_recordingSw.Elapsed:hh\\:mm\\:ss\\.ff}"
            : wallClock;
        LogLine?.Invoke($"[{ts}] [{phase}] {msg}");
    }

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
        DbgLog("RECORD", "Boucle waveIn démarrée");

        while (!_stopRecording)
        {
            NativeMethods.WaitForSingleObject(hEvent, 100);

            for (int i = 0; i < N_BUFFERS; i++)
            {
                WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    var data = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                    allBytes.AddRange(data);
                    DbgLog("RECORD", $"Buffer[{i}] récolté — {hdr.dwBytesRecorded} octets (total : {allBytes.Count})");

                    while (allBytes.Count >= CHUNK_BYTES)
                    {
                        byte[] chunkBytes = allBytes.GetRange(0, CHUNK_BYTES).ToArray();
                        allBytes.RemoveRange(0, CHUNK_BYTES);
                        DbgLog("RECORD", $"Chunk 30s extrait — {CHUNK_BYTES / 32000.0:F1}s → pipeline");
                        _pipeline.Add(PcmToFloat(chunkBytes));
                    }

                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }
        }
        DbgLog("RECORD", $"Boucle terminée — {allBytes.Count} octets accumulés ({allBytes.Count / 32000.0:F1}s)");

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
            DbgLog("RECORD", $"Dernier chunk — {allBytes.Count / 32000.0:F1}s → pipeline");
            _pipeline.Add(PcmToFloat(allBytes.ToArray()));
        }

        _pipeline.CompleteAdding();
        DbgLog("RECORD", "Pipeline complété — fin d'enregistrement");
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

        static bool IsHallucinatedOutput(string text) =>
            string.IsNullOrWhiteSpace(text) ||
            text.Contains("Sous-titrage", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radio-Canada",  StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Sous-titres",   StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SRC",           StringComparison.Ordinal);

        int chunkIndex = 0;
        foreach (float[] chunk in _pipeline.GetConsumingEnumerable())
        {
            chunkIndex++;
            float chunkSec = (float)chunk.Length / 16_000f;
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} reçu — {chunk.Length} samples ({chunkSec:F1}s) → whisper_full...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int result = NativeMethods.whisper_full(ctx, wparams, chunk, chunk.Length);
            sw.Stop();
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} — terminé en {sw.ElapsedMilliseconds} ms, code={result}");

            if (result != 0)
            {
                LogErrorLine?.Invoke($"[ERREUR] Chunk {chunkIndex} : whisper_full code {result}, chunk ignoré");
                continue;
            }

            int nSeg = NativeMethods.whisper_full_n_segments(ctx);
            var parts = new List<string>();
            for (int i = 0; i < nSeg; i++)
                parts.Add(Marshal.PtrToStringUTF8(NativeMethods.whisper_full_get_segment_text(ctx, i)) ?? "");
            string chunkText = string.Join(" ", parts).Trim();

            LogLine?.Invoke($"Brut    : {chunkText}");

            if (IsHallucinatedOutput(chunkText))
            {
                LogLine?.Invoke("→ filtré (hallucination détectée)");
                continue;
            }

            _transcribedChunks.Add(chunkText);
            DbgLog("TRANSCRIBE", $"Chunk {chunkIndex} → accepté ({chunkText.Length} chars). Buffer : {_transcribedChunks.Count} chunk(s)");

            // Mettre à jour initial_prompt avec la fin du chunk accepté pour la continuité
            Marshal.FreeHGlobal(promptPtr);
            string tail = chunkText.Length > 150 ? chunkText[^150..] : chunkText;
            promptPtr = Marshal.StringToHGlobalAnsi(tail);
            wparams.initial_prompt = promptPtr;
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
        CopyToClipboard(fullText);

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

        StatusChanged?.Invoke("En attente");
        _recordingSw?.Stop();
        TranscriptionFinished?.Invoke();
    }

    // ── Presse-papier ─────────────────────────────────────────────────────────

    private static void CopyToClipboard(string text)
    {
        const uint GMEM_MOVEABLE  = 0x0002;
        const uint CF_UNICODETEXT = 13;

        int byteCount = (text.Length + 1) * 2;

        IntPtr hMem = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hMem == IntPtr.Zero) return;

        IntPtr ptr = NativeMethods.GlobalLock(hMem);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0);
        NativeMethods.GlobalUnlock(hMem);

        if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return;
        NativeMethods.EmptyClipboard();
        NativeMethods.SetClipboardData(CF_UNICODETEXT, hMem);
        NativeMethods.CloseClipboard();
    }

    private void PasteFromClipboard()
    {
        const uint   INPUT_KEYBOARD  = 1;
        const uint   KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL      = 0x11;
        const ushort VK_V            = 0x56;

        if (_pasteTarget != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_pasteTarget);
            Thread.Sleep(50);
        }

        int cbSize = Marshal.SizeOf<INPUT>();

        var inputs = new INPUT[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_V,       ki_dwFlags = KEYEVENTF_KEYUP },
            new INPUT { type = INPUT_KEYBOARD, ki_wVk = VK_CONTROL, ki_dwFlags = KEYEVENTF_KEYUP },
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, cbSize);
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
