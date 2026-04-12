using System.Runtime.InteropServices;
using WhispUI.Logging;

namespace WhispUI;

// ─── Transcription engine ─────────────────────────────────────────────────────
//
// Ported from WhispInteropTest (WhispForm) into a standalone class.
// UI-framework independent — communicates via events.
// Events may fire from background threads: subscribers are responsible
// for marshaling to the UI thread.

internal sealed class WhispEngine : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────

    // Fired from the loading thread or from StartRecording/Transcribe.
    // Subscriber must marshal to UI thread via DispatcherQueue.TryEnqueue.
    public event Action<string>?  StatusChanged;

    // Fired at the very end of Transcribe(), regardless of exit path
    // (model not ready, empty text, normal exit). Used by HUD to hide.
    // Background thread → subscriber responsible for marshaling.
    public event Action?          TranscriptionFinished;

    // Fired when StartRecording detects (via waveInOpen probe) that the
    // audio device is unavailable. Subscriber (HudWindow) shows an explicit
    // error message instead of the chrono. Hotkey thread → subscriber
    // responsible for marshaling.
    public event Action<string, string>? MicrophoneUnavailable;

    // Synchronous rendezvous just before PasteFromClipboard. The caller
    // (App.xaml.cs) hooks HudWindow.HideSync() to ensure no activation
    // mutation from WhispUI occurs while SendInput is in flight to the target.
    public Action? OnReadyToPaste { get; set; }

    // ── Configuration ─────────────────────────────────────────────────────────

    const string MODEL_FILE     = "ggml-large-v3.bin";
    const string DEFAULT_MODEL  = @"D:\projects\ai\transcription\shared\" + MODEL_FILE;

    // ── Internal state ───────────────────────────────────────────────────────

    private static readonly LogService _log = LogService.Instance;

    private readonly string     _modelPath;
    private readonly LlmService _llm;

    // volatile: prevents the compiler from caching these values in CPU registers.
    // Without volatile, a background thread could read a stale value.
    private volatile IntPtr _ctx           = IntPtr.Zero;
    private volatile bool   _isRecording   = false;
    private volatile bool   _stopRecording = false;
    private volatile bool   _shouldPaste   = false;
    private volatile bool   _useLlm        = false;
    private volatile IntPtr _pasteTarget   = IntPtr.Zero;

    // Segments produced by Whisper during whisper_full() via native callback.
    // Accumulated progressively from the whisper.cpp inference thread — protected
    // by lock since the callback runs on a different thread. Serves both as
    // progressive recovery (logs) and source for the final text.
    private readonly List<TranscribedSegment> _segments = new();
    private readonly object _segmentsLock = new();

    // Delegate stored as instance field to prevent the GC from collecting it
    // while whisper.cpp holds its native pointer (same pitfall as SubclassProc).
    private WhisperNewSegmentCallback? _newSegmentCallback;

    // whisper_log_set callback — same GC constraint. Stored for lifetime since
    // the hook is global (process-wide) and installed once at startup.
    private NativeMethods.WhisperLogCallback? _whisperLogCallback;

    // Lower bound of timestamp token IDs for the current model. Cached at the
    // start of each Transcribe and read by OnNewSegment to filter non-text tokens.
    private int _tokenBeg;

    // t1 of the previous segment (centiseconds), used to compute inter-segment
    // gap in OnNewSegment. Reset to -1 at the start of each Transcribe — first
    // iteration shows gap=+0.0s by convention. Read/written only from the
    // whisper.cpp inference thread (sequential callback), no lock needed.
    private long _lastSegmentT1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WhisperNewSegmentCallback(IntPtr ctx, IntPtr state, int n_new, IntPtr user_data);

    private readonly record struct TranscribedSegment(string Text, long T0, long T1, float NoSpeechProb);

    // Stopwatch started at the beginning of each recording (used for logs).
    private System.Diagnostics.Stopwatch? _recordingSw;

    private bool _disposed;

    // ── Observable properties ──────────────────────────────────────────────────

    public bool IsReady     => _ctx != IntPtr.Zero;
    public bool IsRecording => _isRecording;

    // ── Constructor ──────────────────────────────────────────────────────────

    public WhispEngine()
    {
        _modelPath = Environment.GetEnvironmentVariable("WHISP_MODEL_PATH") ?? DEFAULT_MODEL;

        _llm = new LlmService();

        // Hook the global whisper.cpp log callback before LoadModelAsync to
        // catch Vulkan/CUDA initialization logs and model parsing warnings.
        // Install-once, process-wide.
        InstallWhisperLogHook();

        LoadModelAsync();
    }

    // Redirects whisper.cpp internal logs (ggml_log) to LogVerbose.
    // These lines contain backend details (Vulkan device, mem, threads),
    // progress updates during whisper_full, and runtime warnings.
    // Classified as Verbose by default — too chatty for normal flow.
    private void InstallWhisperLogHook()
    {
        _whisperLogCallback = (level, textPtr, _) =>
        {
            try
            {
                string msg = Marshal.PtrToStringUTF8(textPtr)?.TrimEnd('\r', '\n', ' ') ?? "";
                if (string.IsNullOrEmpty(msg)) return;

                // ggml levels: 0=None, 1=Debug, 2=Info, 3=Warn, 4=Error, 5=Cont.
                // Warn/Error surface as normal logs to be visible without enabling
                // Verbose filter. Info/Debug/Cont stay in Verbose.
                switch (level)
                {
                    case 4: _log.Error(LogSource.Whisper, msg); break;
                    case 3: _log.Warning(LogSource.Whisper, msg); break;
                    default: _log.Verbose(LogSource.Whisper, msg); break;
                }
            }
            catch
            {
                // Never let an exception cross the native boundary.
            }
        };

        try
        {
            NativeMethods.whisper_log_set(_whisperLogCallback, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // whisper_log_set missing from a very old libwhisper: log and
            // continue — the rest of the pipeline doesn't depend on it.
            DebugLog.Write("ENGINE", $"whisper_log_set unavailable: {ex.Message}");
        }
    }

    // ── Model loading (background thread) ────────────────────────────────────

    private void LoadModelAsync()
    {
        StatusChanged?.Invoke("Chargement du modèle...");

        var t = new Thread(() =>
        {
            DebugLog.Write("ENGINE", "load thread started, path=" + _modelPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _log.Info(LogSource.Model, $"path: {_modelPath}");
            if (File.Exists(_modelPath))
            {
                double mb = new FileInfo(_modelPath).Length / 1024.0 / 1024.0;
                _log.Info(LogSource.Model, $"file: {mb:F1} MB");
            }
            else
            {
                _log.Warning(LogSource.Model, $"file not found on disk ({_modelPath})");
            }
            _log.Info(LogSource.Model, "init whisper_init_from_file_with_params (use_gpu=1)");

            IntPtr ctxParamsPtr = NativeMethods.whisper_context_default_params_by_ref();
            WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
            NativeMethods.whisper_free_context_params(ctxParamsPtr);
            ctxParams.use_gpu = 1;

            _ctx = NativeMethods.whisper_init_from_file_with_params(_modelPath, ctxParams);
            DebugLog.Write("ENGINE", "whisper_init_from_file returned ctx=" + _ctx);
            sw.Stop();

            if (_ctx == IntPtr.Zero)
            {
                _log.Error(LogSource.Init, $"Failed to load model: {_modelPath}");
                StatusChanged?.Invoke("Erreur : modèle non chargé");
            }
            else
            {
                _log.Step(LogSource.Model, $"Model loaded ({sw.ElapsedMilliseconds} ms)");
                StatusChanged?.Invoke("En attente");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ── Start recording ─────────────────────────────────────────────────────

    public void StartRecording(bool useLlm, bool shouldPaste, IntPtr pasteTarget)
    {
        if (_isRecording) return;

        // Probe the audio device BEFORE firing StatusChanged("Enregistrement...").
        // If the mic is absent/busy, short-circuit the entire pipeline:
        // no HUD chrono, no worker thread, no Transcribe(empty).
        // Instead, MicrophoneUnavailable is raised → HudWindow shows a
        // dedicated error message that stays visible for a few seconds.
        if (!TryProbeMicrophone(out uint probeErr))
        {
            var (title, body) = DescribeMicError(probeErr);
            _log.Error(LogSource.Record, $"probe MMSYSERR={probeErr} — {title}");
            MicrophoneUnavailable?.Invoke(title, body);
            return;
        }

        _isRecording   = true;
        _stopRecording = false;
        _shouldPaste   = shouldPaste;
        _useLlm        = useLlm;
        _pasteTarget   = pasteTarget;
        lock (_segmentsLock) _segments.Clear();

        _recordingSw = System.Diagnostics.Stopwatch.StartNew();
        StatusChanged?.Invoke("Enregistrement...");

        // Trace the target captured at Start — symmetric with the green Step at the end.
        // Shows from the start which window/control will receive the paste,
        // and helps diagnose cases where the Start target is wrong.
        if (_pasteTarget != IntPtr.Zero)
        {
            _log.Verbose(LogSource.Hotkey, $"target captured at Start: {Win32Util.DescribeHwnd(_pasteTarget)}");
            string? focusClass = Win32Util.GetFocusedClass(_pasteTarget);
            _log.Verbose(LogSource.Hotkey, focusClass is null
                ? "focused control at Start: <no keyboard focus detected>"
                : $"focused control at Start: {focusClass}");
        }
        else
        {
            _log.Verbose(LogSource.Hotkey, "no target captured at Start (paste disabled or foreground = WhispUI)");
        }

        // Single background thread: Record then Transcribe in sequence.
        // Whisper needs the full audio for its internal windowing —
        // no parallelism possible, and simpler to debug.
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

    // ── Stop recording (second hotkey press) ────────────────────────────────

    public void StopRecording()
    {
        // Re-capture the target at Stop to handle "I switched text fields during
        // recording": we want to paste in the field where the user IS at Stop
        // time, not the one at Start. The hotkey is global, so
        // GetForegroundWindow() at this point returns the user's app. Safety
        // net: if foreground belongs to WhispUI itself (HUD or LogWindow
        // activated by a click), keep the Start target — otherwise we'd get
        // a false positive "pasted into our own logs".
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            if (pid != ownPid)
            {
                if (fg != _pasteTarget)
                    _log.Verbose(LogSource.Hotkey, $"target updated at Stop: {Win32Util.DescribeHwnd(fg)}");
                _pasteTarget = fg;
            }
            else
            {
                _log.Verbose(LogSource.Hotkey, $"foreground at Stop = WhispUI ({Win32Util.DescribeHwnd(fg)}), keeping Start target");
            }
        }
        _stopRecording = true;
    }

    // ── Audio device probe (before StartRecording) ─────────────────────────────
    //
    // Attempts waveInOpen + waveInClose in sequence with the target format and
    // configured device. If it passes, we know the recording session can start;
    // otherwise we get the MMSYSERR code for a detailed message.
    // Measured cost ~1-2 ms on a healthy device — negligible vs Whisper latency.

    private bool TryProbeMicrophone(out uint err)
    {
        const uint WAVE_MAPPER = 0xFFFFFFFF;
        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,
            nChannels       = 1,
            nSamplesPerSec  = 16000,
            nAvgBytesPerSec = 32000,
            nBlockAlign     = 2,
            wBitsPerSample  = 16,
            cbSize          = 0,
        };

        int configuredDevice = Settings.SettingsService.Instance.Current.Recording.AudioInputDeviceId;
        uint deviceId = configuredDevice < 0 ? WAVE_MAPPER : (uint)configuredDevice;

        err = NativeMethods.waveInOpen(out IntPtr hWaveIn, deviceId, ref wfx, IntPtr.Zero, IntPtr.Zero, 0u);
        if (err != 0) return false;

        NativeMethods.waveInClose(hWaveIn);
        return true;
    }

    // MMSYSERR → (title, body) for UI. Messages formulated for the end user
    // — no Win32 jargon. Raw code is logged elsewhere for debug.
    private static (string Title, string Body) DescribeMicError(uint err) => err switch
    {
        2 => ("Aucun microphone détecté", "Branche un micro ou vérifie l'entrée audio sélectionnée dans les paramètres de transcription."),
        6 => ("Aucun microphone détecté", "Branche un micro ou vérifie l'entrée audio sélectionnée dans les paramètres de transcription."),
        4 => ("Microphone occupé", "Un autre logiciel utilise déjà le microphone. Ferme-le puis relance l'enregistrement."),
        _ => ("Microphone indisponible", $"L'ouverture du périphérique audio a échoué (code MMSYSERR {err}).")
    };

    // ── Audio recording ─────────────────────────────────────────────────────
    //
    // Captures the microphone continuously into a single resizable buffer.
    // When _stopRecording becomes true, returns all accumulated audio as float[]
    // (PCM16 → float [-1, 1]) to be passed in a single call to whisper_full().
    // Whisper handles its own internal windowing (30s + dynamic seek) and
    // inter-window context propagation via tokens — no chunking here.

    private float[] Record()
    {
        const uint WAVE_MAPPER    = 0xFFFFFFFF;
        const uint CALLBACK_EVENT = 0x00050000;
        const uint WHDR_DONE      = 0x00000001;
        const int  N_BUFFERS      = 4;
        const int  BYTES_PER_BUF  = 16000 * 2 * 500 / 1000; // 500ms × 16kHz × 2 octets/sample

        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,     // uncompressed PCM
            nChannels       = 1,     // mono
            nSamplesPerSec  = 16000,
            nAvgBytesPerSec = 32000,
            nBlockAlign     = 2,
            wBitsPerSample  = 16,
            cbSize          = 0,
        };

        IntPtr hEvent = NativeMethods.CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, null);

        // Device selected in Settings. -1 = WAVE_MAPPER (system default).
        int configuredDevice = Settings.SettingsService.Instance.Current.Recording.AudioInputDeviceId;
        uint deviceId = configuredDevice < 0 ? WAVE_MAPPER : (uint)configuredDevice;

        uint err = NativeMethods.waveInOpen(out IntPtr hWaveIn, deviceId, ref wfx, hEvent, IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            _log.Error(LogSource.Record, $"waveInOpen error {err}");
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
        // Single buffer, grows throughout the recording.
        // 1 sample = 2 bytes PCM16. At 16 kHz, 1 minute = 1.92M bytes.
        var allBytes = new List<byte>(capacity: 16000 * 2 * 60); // pre-reserve ~1 min
        _log.Info(LogSource.Record, "Recording started (16kHz mono PCM16)");

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
                        _log.Warning(LogSource.Record, $"empty buffer[{i}] received");
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
                _log.Warning(LogSource.Record, $"lag, {bufferDoneCount} buffers ready simultaneously");

            // Heartbeat ~5s, Verbose → visible in All filter only
            double curSec = allBytes.Count / 32000.0;
            if (curSec >= nextHeartbeatSec)
            {
                _log.Verbose(LogSource.Record, $"+{curSec:F1}s captured");
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
        _log.Info(LogSource.Record, $"Capture complete — {totalSec:F1}s audio ({allBytes.Count} bytes)");

        // Tail RMS: measures the energy of the final 600ms to see if we're
        // cutting during a syllable (lost punctuation) or in a clean silence.
        // Purely diagnostic — doesn't change the buffer sent to whisper.cpp.
        // 600ms = typical breathing/inter-phrase pause duration, enough to
        // check if the last word was fully spoken before Stop.
        {
            const int TailMs = 600;
            int tailBytes = Math.Min(allBytes.Count, 16000 * 2 * TailMs / 1000);
            if (tailBytes >= 400) // au moins ~12ms
            {
                int start = allBytes.Count - tailBytes;
                double sumSq = 0;
                int nSamples = tailBytes / 2;
                for (int i = 0; i < nSamples; i++)
                {
                    short s = (short)(allBytes[start + i * 2] | (allBytes[start + i * 2 + 1] << 8));
                    double v = s / 32768.0;
                    sumSq += v * v;
                }
                double rms = Math.Sqrt(sumSq / nSamples);
                double dbfs = rms > 0 ? 20.0 * Math.Log10(rms) : -120.0;
                double tailSec = tailBytes / 32000.0;
                _log.Info(LogSource.Record, $"Tail {tailSec * 1000:F0}ms RMS={rms:F4} ({dbfs:F1} dBFS) — signal {(dbfs > -50 ? "active" : "silent")} at Stop");
            }
        }

        return PcmToFloat(allBytes.ToArray());
    }

    // ── Whisper transcription ────────────────────────────────────────────────
    //
    // Monolithic call: all audio is passed at once to whisper_full(), which
    // handles its own internal windowing (30s + dynamic seek) and inter-window
    // context propagation via tokens. No chunking on the C# side.
    //
    // Progressive recovery via new_segment_callback: whisper.cpp invokes the
    // callback for each new validated segment during decoding, on ITS inference
    // thread — hence the lock on _segments. Final text is assembled from these
    // segments at the end of the call.

    // Detects simple repetition: subsequence of >= 4 words recurring >= 3 times.
    // Kept as log-only (warning) for diagnostic signal — doesn't filter anything.
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
        // n_new = number of segments produced since last call; they sit at the
        // end of the total list exposed by whisper_full_n_segments.
        try
        {
            int total = NativeMethods.whisper_full_n_segments(ctx);
            int from  = total - n_new;
            // Lower bound of timestamp token IDs — above this are <|t.tt|>,
            // not text tokens. Cached per Transcribe call to avoid unnecessary
            // repeated P/Invoke (depends on model, not segment).
            int tokenBeg = _tokenBeg;
            for (int i = from; i < total; i++)
            {
                string segText = Marshal.PtrToStringUTF8(NativeMethods.whisper_full_get_segment_text(ctx, i)) ?? "";
                long  t0  = NativeMethods.whisper_full_get_segment_t0(ctx, i);
                long  t1  = NativeMethods.whisper_full_get_segment_t1(ctx, i);
                float nsp = NativeMethods.whisper_full_get_segment_no_speech_prob(ctx, i);

                // Per-segment confidence, aggregated over text tokens only.
                // p = linear probability of the token as sampled by Whisper.
                // avg = "is the sentence globally confident?", min = "weakest link / fabricated word?".
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
                if (textTok == 0) minP = 0f; // segment without text tokens → min "undefined"

                lock (_segmentsLock)
                    _segments.Add(new TranscribedSegment(segText, t0, t1, nsp));

                // dur = segment duration, gap = silence (or overlap) with the previous one.
                // In a typical hallucination loop, dur≈3.0s contiguous (gap=+0.0s) repeats
                // metronomically — visually recognizable pattern without mental math.
                // A large gap signals a Whisper seek or an input silence (risky).
                double dur = (t1 - t0) / 100.0;
                double gap = _lastSegmentT1 < 0 ? 0.0 : (t0 - _lastSegmentT1) / 100.0;
                _lastSegmentT1 = t1;

                // no_speech: probability that the segment is silence/noise (0 = confident speech, 1 = confident silence).
                // p̄ / min: average and minimum confidence over text tokens in the segment.
                // t0/t1 are in centiseconds (1 unit = 10 ms) on the whisper.cpp side.
                _log.Verbose(LogSource.Transcribe, $"seg #{i + 1} [{t0 / 100.0:F1}s→{t1 / 100.0:F1}s dur={dur:F1}s gap={(gap >= 0 ? "+" : "")}{gap:F1}s, nsp={nsp:P0}, p̄={avgP:F2} min={minP:F2}, {textTok}/{nTok} tok] {segText.Trim()}");
            }
        }
        catch (Exception ex)
        {
            // NEVER let an exception cross the managed→native boundary.
            _log.Error(LogSource.Callback, $"{ex.GetType().Name}: {ex.Message}");
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
            _log.Warning(LogSource.Transcribe, "empty audio buffer, nothing to transcribe");
            StatusChanged?.Invoke("En attente");
            TranscriptionFinished?.Invoke();
            return;
        }

        IntPtr fullParamsPtr = NativeMethods.whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        NativeMethods.whisper_free_params(fullParamsPtr);

        wparams.print_progress = 0;

        // Snapshot user settings at the start of transcription.
        // Hot-reload fields (thresholds, VAD, suppress, context, decoding)
        // are applied here on each call — no context restart needed.
        // Heavy settings (model, use_gpu) are handled at LoadModelAsync.
        var settings = Settings.SettingsService.Instance.Current;
        var nativeAllocs = Settings.WhisperParamsMapper.Apply(ref wparams, settings);

        // Cache the timestamp token bound once for the entire call — it's a model
        // property, not per-segment, no need to call for each token.
        _tokenBeg = NativeMethods.whisper_token_beg(ctx);
        _lastSegmentT1 = -1;

        // Hook the native callback. Delegate stored as instance field to prevent
        // GC from collecting it while whisper.cpp holds the pointer.
        _newSegmentCallback = OnNewSegment;
        wparams.new_segment_callback = Marshal.GetFunctionPointerForDelegate(_newSegmentCallback);
        wparams.new_segment_callback_user_data = IntPtr.Zero;

        float audioSec = (float)audio.Length / 16_000f;
        _log.Info(LogSource.Transcribe, $"Audio reçu ({audioSec:F1}s, {audio.Length} samples) → whisper_full");
        _log.Verbose(LogSource.Transcribe, $"params: temp={wparams.temperature:F2} +{wparams.temperature_inc:F2} | logprob_thold={wparams.logprob_thold:F2} | entropy_thold={wparams.entropy_thold:F2} | no_speech_thold={wparams.no_speech_thold:F2} | suppress_nst={wparams.suppress_nst} | n_threads={wparams.n_threads}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int result = NativeMethods.whisper_full(ctx, wparams, audio, audio.Length);
        sw.Stop();
        long transcribeMsTotal = sw.ElapsedMilliseconds;

        nativeAllocs.Free();

        if (result != 0)
        {
            _log.Error(LogSource.Transcribe, $"whisper_full returned code {result}");
            StatusChanged?.Invoke("Erreur transcription");
            TranscriptionFinished?.Invoke();
            return;
        }

        // Assemble final text from segments accumulated by the callback.
        // We could also re-iterate whisper_full_n_segments(ctx) here, but going
        // through _segments guarantees that a logged segment is exactly a segment
        // of the final text — no possible divergence between the two sources.
        string fullText;
        int nSeg;
        lock (_segmentsLock)
        {
            nSeg = _segments.Count;
            fullText = string.Join(" ", _segments.Select(s => s.Text)).Trim();
        }

        _log.Info(LogSource.Transcribe, $"whisper_full OK ({transcribeMsTotal} ms, {nSeg} segments, {fullText.Length} chars)");

        if (LooksRepeated(fullText))
            _log.Warning(LogSource.Transcribe, "repetition detected in text (heuristic signal, no filtering)");

        if (string.IsNullOrWhiteSpace(fullText))
        {
            StatusChanged?.Invoke("En attente");
            TranscriptionFinished?.Invoke();
            return;
        }

        // Always copy raw text first — safety net even if LLM fails
        var swClip = System.Diagnostics.Stopwatch.StartNew();
        CopyToClipboard(fullText);
        swClip.Stop();

        long llmMs = 0;
        var llmSettings = Settings.SettingsService.Instance.Current.Llm;
        double recDurationSec = (_recordingSw?.Elapsed.TotalSeconds) ?? 0;

        // Rewrite profile resolution:
        // - Alt+Ctrl+` (manual) → ManualProfileName profile
        // - Alt+` (normal) + auto-rewrite → first matching AutoRewriteRule
        Settings.RewriteProfile? profile = null;
        if (_useLlm && llmSettings.Enabled)
        {
            profile = llmSettings.Profiles.Find(p =>
                string.Equals(p.Name, llmSettings.ManualProfileName, StringComparison.OrdinalIgnoreCase));
        }
        else if (llmSettings.Enabled && llmSettings.AutoRewriteRules.Count > 0)
        {
            // Descending scan: the longest matching rule wins.
            foreach (var rule in llmSettings.AutoRewriteRules
                .OrderByDescending(r => r.MinDurationSeconds))
            {
                if (recDurationSec >= rule.MinDurationSeconds)
                {
                    profile = llmSettings.Profiles.Find(p =>
                        string.Equals(p.Name, rule.ProfileName, StringComparison.OrdinalIgnoreCase));
                    break;
                }
            }
        }

        if (profile is not null)
        {
            StatusChanged?.Invoke($"Réécriture ({profile.Name})...");
            var swLlm = System.Diagnostics.Stopwatch.StartNew();
            string? rewritten = _llm.Rewrite(fullText, llmSettings.OllamaEndpoint, profile);
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
            // Synchronous rendezvous: the handler (App) hides the HUD and only
            // returns once SW_HIDE is effective on the UI thread. After this
            // point, nothing in WhispUI touches activation until the end of
            // Transcribe — Ctrl+V delivery is protected.
            OnReadyToPaste?.Invoke();
            _log.Verbose(LogSource.Paste, "HUD hidden (HideSync) — ready to paste");
            var swPaste = System.Diagnostics.Stopwatch.StartNew();
            pasteVerified = PasteFromClipboard();
            swPaste.Stop();
            pasteMs = swPaste.ElapsedMilliseconds;
        }

        string recap = $"total {recDurationSec:F1}s (trans {transcribeMsTotal}/llm {llmMs}/clip {swClip.ElapsedMilliseconds}/paste {pasteMs} ms)";
        if (_shouldPaste && pasteVerified)
            _log.Step(LogSource.Transcribe, $"End-to-end OK — {fullText.Length} chars pasted into {Win32Util.DescribeHwnd(_pasteTarget)} ({recap})");
        else
            _log.Verbose(LogSource.Done, recap);

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
        _log.Verbose(LogSource.Clipboard, $"GlobalAlloc {byteCount}b → hMem={hMem}");
        if (hMem == IntPtr.Zero) { _log.Warning(LogSource.Clipboard, "GlobalAlloc failed"); return; }

        IntPtr ptr = NativeMethods.GlobalLock(hMem);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0);
        NativeMethods.GlobalUnlock(hMem);

        bool opened = NativeMethods.OpenClipboard(IntPtr.Zero);
        _log.Verbose(LogSource.Clipboard, $"OpenClipboard → {opened}");
        if (!opened) { _log.Warning(LogSource.Clipboard, "OpenClipboard failed"); return; }

        NativeMethods.EmptyClipboard();
        IntPtr setHandle = NativeMethods.SetClipboardData(CF_UNICODETEXT, hMem);
        if (setHandle == IntPtr.Zero) _log.Warning(LogSource.Clipboard, "SetClipboardData failed (handle 0)");
        NativeMethods.CloseClipboard();

        // Immediate read-back to verify the clipboard was set correctly
        if (NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            IntPtr h = NativeMethods.GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero)
            {
                _log.Warning(LogSource.Clipboard, "post-copy verify → no Unicode data");
            }
            else
            {
                IntPtr p = NativeMethods.GlobalLock(h);
                string? back = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                NativeMethods.GlobalUnlock(h);
                if (back is null || back.Length != text.Length)
                    _log.Warning(LogSource.Clipboard, $"post-copy verify → length {back?.Length ?? -1} != {text.Length}");
            }
            NativeMethods.CloseClipboard();
        }

        _log.Info(LogSource.Clipboard, $"Text copied ({text.Length} chars)");
    }

    // Returns true only if all verification conditions are met (valid target,
    // not WhispUI, foreground restored, keyboard focus on a plausibly text
    // control, complete SendInput). Otherwise false: the caller then emits a
    // Warn with instructions for manual paste.
    private bool PasteFromClipboard()
    {
        const uint   INPUT_KEYBOARD  = 1;
        const uint   KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL      = 0x11;
        const ushort VK_V            = 0x56;

        _log.Verbose(LogSource.Paste, $"expected target: {Win32Util.DescribeHwnd(_pasteTarget)}");
        IntPtr fgBefore = NativeMethods.GetForegroundWindow();
        _log.Verbose(LogSource.Paste, $"foreground before: {Win32Util.DescribeHwnd(fgBefore)}");

        if (_pasteTarget == IntPtr.Zero)
        {
            _log.Warning(LogSource.Paste, "refused: no registered target. Clipboard contains the text — paste manually with Ctrl+V.");
            return false;
        }

        // Refuse if the target is a WhispUI window itself (LogWindow, HUD...).
        // Avoids the false positive where we "pasted" into our own logs.
        NativeMethods.GetWindowThreadProcessId(_pasteTarget, out uint targetPid);
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        if (targetPid == ownPid)
        {
            _log.Warning(LogSource.Paste, $"refused: target belongs to WhispUI ({Win32Util.DescribeHwnd(_pasteTarget)}). Clipboard contains the text — paste manually with Ctrl+V in the right window.");
            return false;
        }

        bool sfgOk = NativeMethods.SetForegroundWindow(_pasteTarget);
        _log.Verbose(LogSource.Paste, $"SetForegroundWindow → {sfgOk}");
        Thread.Sleep(50);

        IntPtr fgAfter = NativeMethods.GetForegroundWindow();
        if (fgAfter != _pasteTarget)
        {
            _log.Warning(LogSource.Paste, $"refused: focus not restored (expected {Win32Util.DescribeHwnd(_pasteTarget)}, actual {Win32Util.DescribeHwnd(fgAfter)}). Clipboard contains the text — paste manually with Ctrl+V.");
            return false;
        }

        string? focusClass = Win32Util.GetFocusedClass(_pasteTarget);
        if (focusClass is null)
        {
            _log.Warning(LogSource.Paste, "refused: target has no keyboard focus on a text control. Clipboard contains the text — click in a text field then Ctrl+V.");
            return false;
        }
        _log.Verbose(LogSource.Paste, $"focused control: {focusClass}");

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
            _log.Warning(LogSource.Paste, $"partial: SendInput injected {sent}/{inputs.Length} events. Clipboard contains the text — paste manually with Ctrl+V.");
            return false;
        }

        _log.Info(LogSource.Paste, $"Ctrl+V sent to {Win32Util.DescribeHwnd(_pasteTarget)} (focus={focusClass})");
        return true;
    }

    // ── PCM → float conversion ─────────────────────────────────────────────

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
