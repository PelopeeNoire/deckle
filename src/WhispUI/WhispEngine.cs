using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WhispUI.Interop;
using WhispUI.Llm;
using WhispUI.Logging;
using WhispUI.Settings;

namespace WhispUI;

// Result of a pipeline pass, consumed by the HUD post-paste handler.
//   None            — nothing to show (empty audio, empty text, error).
//   Pasted          — UIA confirmed a text field and Ctrl+V was delivered;
//                     HUD flashes "Pasted" in green.
//   ClipboardOnly   — text is on the clipboard but paste was skipped (UIA
//                     couldn't confirm, foreground was WhispUI, SendInput
//                     partial…); HUD shows the Ctrl+V reminder for a few
//                     seconds. This is the safe default when in doubt.
internal enum TranscriptionOutcome { None, Pasted, ClipboardOnly }

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
    // (model not ready, empty text, normal exit). The outcome tells the HUD
    // whether text was actually delivered, so it can show a short "Copié"
    // confirmation on success, a "Ctrl+V" reminder when the clipboard holds
    // the result but paste was refused, or hide silently when there's
    // nothing meaningful to report (errors, empty audio, empty text).
    // Background thread → subscriber responsible for marshaling.
    public event Action<TranscriptionOutcome>? TranscriptionFinished;

    // Synchronous rendezvous just before PasteFromClipboard. The caller
    // (App.xaml.cs) hooks HudWindow.HideSync() to ensure no activation
    // mutation from WhispUI occurs while SendInput is in flight to the target.
    public Action? OnReadyToPaste { get; set; }

    // ── Configuration ─────────────────────────────────────────────────────────

    const string MODEL_FILE = "ggml-large-v3.bin";

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

    // Name of the rewrite profile chosen by the hotkey that started this
    // recording (null = no manual rewrite; fall back to AutoRewriteRules
    // based on recording duration). Captured at StartRecording time and
    // consumed at the end of Transcribe().
    private string?         _manualProfileName = null;

    // Model lifecycle: lazy load on first hotkey, unload after idle timeout.
    // _pipelineActive guards against unloading while Record+Transcribe runs.
    private volatile bool   _pipelineActive = false;
    private readonly object _modelLock = new();
    private System.Threading.Timer? _idleTimer;
    private const int MODEL_IDLE_TIMEOUT_MS = 5 * 60 * 1000; // 5 minutes

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

    // Stopwatch started at the beginning of whisper_full — read by OnNewSegment
    // to log elapsed time since inference start (cumulative, not per-segment).
    private System.Diagnostics.Stopwatch? _transcribeSw;

    // Decoding strategy label cached at the start of Transcribe, used in the
    // final recap log (e.g. "beam5" or "greedy").
    private string _strategyLabel = "";

    // Stopwatch started at the beginning of each recording (used for logs).
    private System.Diagnostics.Stopwatch? _recordingSw;

    // VAD timing — whisper.cpp's Silero VAD runs inside whisper_full() natively,
    // so we can't bracket it with a C# stopwatch. Instead we watch the native
    // log hook for "whisper_vad" lines: the first one starts the stopwatch,
    // the sentinel "Reduced audio from X to Y samples" (last line emitted by
    // the VAD module before whisper_full hands speech chunks to transcription)
    // stops it. _vadCapturing gates the detection to the whisper_full call
    // window — load-time logs can't trip it.
    //
    // Earlier heuristic ("first non-VAD line stops the stopwatch") tripped on
    // "whisper_backend_init_gpu" emitted during VAD context creation, well
    // before actual detection ran (VAD wall time mis-reported as 0 s).
    private System.Diagnostics.Stopwatch? _vadSw;
    private bool _vadEnded;
    private volatile bool _vadCapturing;

    // Total duration of speech segments parsed from the whisper.cpp VAD log
    // line "whisper_vad_segments: total duration of speech segments: 12.34 s".
    // Sentinel -1 means "not yet parsed" — surfaced in the VAD_END narrative,
    // omitted gracefully when the line shape changes upstream.
    private float _vadSpeechSec = -1f;
    private static readonly Regex _vadSpeechRegex = new(
        @"total duration of speech segments:\s*([\d.]+)\s*s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool _disposed;

    // ── Observable properties ──────────────────────────────────────────────────

    public bool IsReady     => _ctx != IntPtr.Zero;
    public bool IsRecording => _isRecording;

    // ── Constructor ──────────────────────────────────────────────────────────

    public WhispEngine()
    {
        _modelPath = Environment.GetEnvironmentVariable("WHISP_MODEL_PATH")
            ?? Path.Combine(SettingsService.Instance.ResolveModelsDirectory(), MODEL_FILE);

        _llm = new LlmService();

        // Hook the global whisper.cpp log callback before any model load to
        // catch Vulkan/CUDA initialization logs and model parsing warnings.
        // Install-once, process-wide.
        InstallWhisperLogHook();

        // Model loaded on-demand at first hotkey press (see EnsureModelLoaded).
        // Unloaded after MODEL_IDLE_TIMEOUT_MS of inactivity to free VRAM.
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

                // VAD sentinel: only while whisper_full is running (_vadCapturing).
                // Start the stopwatch on the first "whisper_vad" line (matches both
                // "whisper_vad:" high-level messages and "whisper_vad_*" sub-module
                // lines), stop it on the explicit end marker "Reduced audio from
                // X to Y samples" — emitted last by the VAD module before whisper
                // moves on to transcription proper.
                if (_vadCapturing)
                {
                    bool isVadLine = msg.StartsWith("whisper_vad", StringComparison.Ordinal);
                    if (isVadLine)
                    {
                        if (_vadSw is null)
                        {
                            _vadSw = System.Diagnostics.Stopwatch.StartNew();
                            _log.Narrative(LogSource.Transcribe, "Looking for speech in the recording — a small detector is scanning the audio for spoken segments.");
                        }

                        // Enrich VAD_END with the total speech duration when
                        // whisper.cpp prints it. Parse-once: ignore later
                        // matches in the same window.
                        if (_vadSpeechSec < 0)
                        {
                            var m = _vadSpeechRegex.Match(msg);
                            if (m.Success && float.TryParse(
                                    m.Groups[1].Value,
                                    NumberStyles.Float,
                                    CultureInfo.InvariantCulture,
                                    out float sp))
                            {
                                _vadSpeechSec = sp;
                            }
                        }

                        // Explicit end marker — stable across whisper.cpp versions
                        // since the function emitting it ("whisper_vad_segments_*")
                        // is the last step before returning to whisper_full.
                        if (!_vadEnded && msg.IndexOf("Reduced audio from", StringComparison.Ordinal) >= 0)
                        {
                            _vadSw?.Stop();
                            _vadEnded = true;
                            double vadSec = _vadSw?.Elapsed.TotalSeconds ?? 0;
                            if (_vadSpeechSec >= 0)
                                _log.Narrative(LogSource.Transcribe, $"Speech detected — {_vadSpeechSec:F1} s of speech found in {vadSec:F1} s. Passing to Whisper for transcription.");
                            else
                                _log.Narrative(LogSource.Transcribe, $"Speech detection done in {vadSec:F1} s. Passing to Whisper for transcription.");
                        }
                    }
                }

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

    // ── Model lifecycle (lazy load + idle unload) ──────────────────────────────
    //
    // The model is NOT loaded at startup. It is loaded on-demand when the user
    // presses the hotkey for the first time (or after an idle unload).
    // After each transcription, an idle timer starts. When it expires without
    // a new transcription, the model is freed to release VRAM.

    /// <summary>
    /// Loads the whisper model synchronously. Caller must be on a background thread.
    /// </summary>
    private bool LoadModel()
    {
        StatusChanged?.Invoke("Loading model");

        DebugLog.Write("ENGINE", "load started, path=" + _modelPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.Info(LogSource.Model, $"path: {_modelPath}");
        if (File.Exists(_modelPath))
        {
            double mb = new FileInfo(_modelPath).Length / 1024.0 / 1024.0;
            _log.Info(LogSource.Model, $"file: {mb:F1} MB");
        }
        else
        {
            _log.Warning(
                LogSource.Model,
                $"file not found on disk ({_modelPath})",
                new UserFeedback(
                    "Whisper model not found",
                    $"File missing: {_modelPath}. Check settings.",
                    UserFeedbackSeverity.Error));
            StatusChanged?.Invoke("Ready");
            return false;
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
            _log.Error(
                LogSource.Init,
                $"Failed to load model: {_modelPath}",
                new UserFeedback(
                    "Failed to load model",
                    "Low GPU memory or corrupt file.",
                    UserFeedbackSeverity.Error));
            StatusChanged?.Invoke("Ready");
            return false;
        }

        _log.Success(LogSource.Model, $"Model loaded ({sw.ElapsedMilliseconds} ms)");
        return true;
    }

    /// <summary>
    /// Ensures the model is in VRAM, loading it if necessary. Thread-safe.
    /// </summary>
    private bool EnsureModelLoaded()
    {
        if (_ctx != IntPtr.Zero) return true;
        lock (_modelLock)
        {
            if (_ctx != IntPtr.Zero) return true; // double-check after acquiring lock
            _log.Info(LogSource.Model, "on-demand load (first use or after idle unload)");
            return LoadModel();
        }
    }

    /// <summary>
    /// Frees the whisper context to release VRAM. Called by the idle timer.
    /// Skipped if a pipeline (Record+Transcribe) is currently active.
    /// </summary>
    private void UnloadModel()
    {
        lock (_modelLock)
        {
            if (_pipelineActive)
            {
                _log.Verbose(LogSource.Model, "idle unload skipped (pipeline active)");
                return;
            }
            if (_ctx == IntPtr.Zero) return;

            NativeMethods.whisper_free(_ctx);
            _ctx = IntPtr.Zero;
            _log.Success(LogSource.Model, $"Model unloaded after {MODEL_IDLE_TIMEOUT_MS / 1000}s idle (VRAM freed)");
            StatusChanged?.Invoke("Ready");
        }
    }

    /// <summary>
    /// Resets (or starts) the idle timer. Called after each transcription completes.
    /// </summary>
    private void ResetIdleTimer()
    {
        if (_idleTimer is null)
            _idleTimer = new System.Threading.Timer(_ => UnloadModel(), null, MODEL_IDLE_TIMEOUT_MS, Timeout.Infinite);
        else
            _idleTimer.Change(MODEL_IDLE_TIMEOUT_MS, Timeout.Infinite);
        _log.Verbose(LogSource.Model, $"idle timer set ({MODEL_IDLE_TIMEOUT_MS / 1000}s)");
    }

    // ── Start recording ─────────────────────────────────────────────────────

    // manualProfileName: when non-null, rewrite the transcription with that
    // profile at the end (manual slot A/B hotkeys). When null, fall back to
    // AutoRewriteRules based on recording duration.
    public void StartRecording(string? manualProfileName, bool shouldPaste)
    {
        if (_isRecording) return;

        // Probe the audio device BEFORE firing StatusChanged("Recording").
        // If the mic is absent/busy, short-circuit the entire pipeline:
        // no HUD chrono, no worker thread, no Transcribe(empty).
        // The UserFeedback payload carries the HUD-visible message through the
        // LogService pipeline — one channel, no parallel event.
        if (!TryProbeMicrophone(out uint probeErr))
        {
            var (title, body) = DescribeMicError(probeErr);
            _log.Error(
                LogSource.Record,
                $"probe MMSYSERR={probeErr} — {title}",
                new UserFeedback(title, body, UserFeedbackSeverity.Error));
            return;
        }

        _isRecording       = true;
        _stopRecording     = false;
        _shouldPaste       = shouldPaste;
        _manualProfileName = manualProfileName;
        _pipelineActive    = true;
        lock (_segmentsLock) _segments.Clear();

        // Cancel any pending idle unload — a new pipeline is starting.
        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        // Single background thread: EnsureModel → Record → Transcribe.
        // Model load (if needed) happens here, not on the UI/hotkey thread,
        // so the app stays responsive during the GPU init (~1-3s).
        //
        // The try/catch/finally is the safety net that keeps the UI consistent
        // when an exception escapes Record() or Transcribe(). Without it, a
        // crash leaves _isRecording=true and StatusChanged never fires again,
        // freezing the tray tooltip on "Recording" indefinitely.
        var worker = new Thread(() =>
        {
            try
            {
                if (!EnsureModelLoaded())
                {
                    TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
                    return;
                }

                _recordingSw = System.Diagnostics.Stopwatch.StartNew();
                StatusChanged?.Invoke("Recording");
                _log.Narrative(LogSource.Record, "Recording from the microphone. Capture continues until you press the hotkey again.");

                float[] audio = Record();
                _isRecording = false;
                StatusChanged?.Invoke("Transcribing");
                Transcribe(audio);
                ResetIdleTimer();
            }
            catch (Exception ex)
            {
                // Recover the UI status that the normal terminal paths would
                // have emitted, so the tray tooltip leaves the recording state.
                _log.Error(
                    LogSource.Transcribe,
                    $"pipeline crashed: {ex.GetType().Name}: {ex.Message}",
                    new UserFeedback(
                        "Unexpected error",
                        "Try again. If it persists, check the logs.",
                        UserFeedbackSeverity.Error));
                StatusChanged?.Invoke("Ready");
                TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
            }
            finally
            {
                // Flags reset unconditionally — subsequent hotkeys must be able
                // to start a new pipeline even after a crash.
                _isRecording = false;
                _pipelineActive = false;
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // ── Stop recording (second hotkey press) ────────────────────────────────

    public void StopRecording()
    {
        // Paste target is whatever the user has focused at Stop time — read
        // live in PasteFromClipboard. We deliberately don't freeze anything at
        // Start: the recording + transcription + LLM pipeline takes seconds,
        // and forcing the user back to the original window would be intrusive.
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
        2 => ("No microphone detected", "Plug in a microphone or check the audio input selected in the transcription settings."),
        6 => ("No microphone detected", "Plug in a microphone or check the audio input selected in the transcription settings."),
        4 => ("Microphone in use", "Another application is already using the microphone. Close it and try again."),
        _ => ("Microphone unavailable", $"Opening the audio device failed (MMSYSERR code {err}).")
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
        _log.Narrative(LogSource.Record, $"Captured {totalSec:F1} s of audio. Moving on to analysis and transcription.");

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
                // elapsed: wall-clock time since whisper_full started (cumulative).
                double elapsedSec = _transcribeSw?.Elapsed.TotalSeconds ?? 0;
                _log.Verbose(LogSource.Transcribe, $"seg #{i + 1} [{t0 / 100.0:F1}s→{t1 / 100.0:F1}s dur={dur:F1}s gap={(gap >= 0 ? "+" : "")}{gap:F1}s, nsp={nsp:P0}, p̄={avgP:F2} min={minP:F2}, {textTok}/{nTok} tok, elapsed={elapsedSec:F1}s] {segText.Trim()}");
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
            StatusChanged?.Invoke("Model not ready");
            TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
            return;
        }

        if (audio.Length == 0)
        {
            _log.Warning(LogSource.Transcribe, "empty audio buffer, nothing to transcribe");
            StatusChanged?.Invoke("Ready");
            TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
            return;
        }

        IntPtr fullParamsPtr = NativeMethods.whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        NativeMethods.whisper_free_params(fullParamsPtr);

        wparams.print_progress = 0;

        // Snapshot user settings at the start of transcription.
        // Hot-reload fields (thresholds, VAD, suppress, context, decoding)
        // are applied here on each call — no context restart needed.
        // Heavy settings (model, use_gpu) are handled at LoadModel.
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
        // Workflow narration: VAD_END handles the "now transcribing" intro in
        // its own line (it sits between VAD completion and transcription start),
        // so a separate TRANSCRIBE_START narrative would be redundant — and
        // would even fire out of order, before VAD_START which runs inside
        // whisper_full's hook.
        _strategyLabel = wparams.strategy == 1 ? $"beam{wparams.beam_search_beam_size}" : "greedy";
        string strategyLabelVerbose = wparams.strategy == 1 ? $"beam(size={wparams.beam_search_beam_size})" : "greedy";
        _log.Verbose(LogSource.Transcribe, $"params: {strategyLabelVerbose} | temp={wparams.temperature:F2} +{wparams.temperature_inc:F2} | logprob_thold={wparams.logprob_thold:F2} | entropy_thold={wparams.entropy_thold:F2} | no_speech_thold={wparams.no_speech_thold:F2} | suppress_nst={wparams.suppress_nst} | carry_prompt={wparams.carry_initial_prompt} | n_threads={wparams.n_threads}");

        // Log the initial prompt sent to Whisper — conditions decoding style.
        string prompt = settings.Transcription.InitialPrompt;
        bool carry = settings.Transcription.CarryInitialPrompt;
        if (!string.IsNullOrEmpty(prompt))
        {
            string truncated = prompt.Length > 60 ? prompt[..60] + "…" : prompt;
            _log.Info(LogSource.Transcribe, $"prompt: \"{truncated}\" ({prompt.Length} chars, carry={carry})");
        }

        _vadSw = null;
        _vadEnded = false;
        _vadSpeechSec = -1f;
        _vadCapturing = true;

        _transcribeSw = System.Diagnostics.Stopwatch.StartNew();
        var sw = _transcribeSw;
        int result = NativeMethods.whisper_full(ctx, wparams, audio, audio.Length);
        sw.Stop();
        long transcribeMsTotal = sw.ElapsedMilliseconds;

        _vadCapturing = false;
        if (_vadSw is { IsRunning: true }) _vadSw.Stop();
        long vadMs = _vadSw?.ElapsedMilliseconds ?? 0;
        long whisperMs = Math.Max(0, transcribeMsTotal - vadMs);

        nativeAllocs.Free();

        if (result != 0)
        {
            _log.Error(LogSource.Transcribe, $"whisper_full returned code {result}");
            StatusChanged?.Invoke("Erreur transcription");
            TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
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
        _log.Narrative(LogSource.Transcribe, $"Whisper transcribed the speech into {nSeg} segments in {transcribeMsTotal / 1000.0:F1} s.");

        if (LooksRepeated(fullText))
            _log.Warning(LogSource.Transcribe, "repetition detected in text (heuristic signal, no filtering)");

        if (string.IsNullOrWhiteSpace(fullText))
        {
            StatusChanged?.Invoke("Ready");
            TranscriptionFinished?.Invoke(TranscriptionOutcome.None);
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
        // - manual slot hotkey → the profile name passed to StartRecording
        // - primary hotkey     → first matching AutoRewriteRule (duration-based)
        Settings.RewriteProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(_manualProfileName) && llmSettings.Enabled)
        {
            profile = llmSettings.Profiles.Find(p =>
                string.Equals(p.Name, _manualProfileName, StringComparison.OrdinalIgnoreCase));
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
            _log.Narrative(LogSource.Llm, $"A local language model (Ollama) is now rewriting the transcript with the {profile.Name} profile.");
            var swLlm = System.Diagnostics.Stopwatch.StartNew();
            string? rewritten = _llm.Rewrite(fullText, llmSettings.OllamaEndpoint, profile);
            swLlm.Stop();
            llmMs = swLlm.ElapsedMilliseconds;
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                fullText = rewritten;
                CopyToClipboard(fullText);
                _log.Narrative(LogSource.Llm, $"Rewrite complete in {swLlm.Elapsed.TotalSeconds:F1} s — the polished text is ready to paste.");
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

        if (_shouldPaste && pasteVerified)
        {
            string exeName = Win32Util.GetExeName(NativeMethods.GetForegroundWindow());
            _log.Narrative(LogSource.Paste, $"Final text pasted into {exeName}.");
        }

        // Technical recap stays available for tuning sessions but drops out of
        // the user-facing Activity / Steps views — visible only under "All".
        string recap = $"total {recDurationSec:F1}s ({_strategyLabel} trans {transcribeMsTotal}/llm {llmMs}/clip {swClip.ElapsedMilliseconds}/paste {pasteMs} ms)";
        _log.Verbose(LogSource.Done, recap);
        _log.Narrative(LogSource.Done, $"Done — {recDurationSec:F1} s of dictation processed. Ready for the next.");

        StatusChanged?.Invoke("Ready");
        _recordingSw?.Stop();

        // Outcome : Pasted on a verified paste delivery, ClipboardOnly when
        // the text made it to the clipboard but paste was disabled or refused
        // (target lost, WhispUI itself, SendInput partial) — the HUD uses
        // this to flash "Copié" or the Ctrl+V reminder before hiding.
        var outcome = (_shouldPaste && pasteVerified) ? TranscriptionOutcome.Pasted
                                                      : TranscriptionOutcome.ClipboardOnly;

        TelemetryLog.Append(new TelemetrySample(
            AudioSec:    audioSec,
            VadMs:       vadMs,
            WhisperMs:   whisperMs,
            LlmMs:       llmMs,
            ClipboardMs: swClip.ElapsedMilliseconds,
            PasteMs:     pasteMs,
            Strategy:    _strategyLabel,
            NSegments:   nSeg,
            TextChars:   fullText.Length,
            Profile:     profile?.Name ?? "",
            Pasted:      pasteVerified,
            Outcome:     outcome.ToString()));

        TranscriptionFinished?.Invoke(outcome);
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

    // Sends Ctrl+V to whatever window currently has the foreground at Stop
    // time — but only when UI Automation confirms the focused element is a
    // text-accepting control (Edit or Document). No Start-time capture, no
    // bring-to-front, no focus comparison: the user had all the time of the
    // recording + transcription to place their cursor where they want.
    //
    // Doctrine: clipboard is the safe default. Paste only when we are confident
    // the target expects text. When in doubt — UIA refuses to answer, unknown
    // control type, foreground is WhispUI itself — the text stays on the
    // clipboard and the HUD shows the Ctrl+V reminder.
    private bool PasteFromClipboard()
    {
        const uint   INPUT_KEYBOARD  = 1;
        const uint   KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL      = 0x11;
        const ushort VK_V            = 0x56;

        IntPtr fg = NativeMethods.GetForegroundWindow();
        _log.Verbose(LogSource.Paste, $"foreground at paste: {Win32Util.DescribeHwnd(fg)}");

        if (fg == IntPtr.Zero)
        {
            _log.Warning(LogSource.Paste, "skipped: no foreground window. Clipboard holds the text — Ctrl+V where you want it.");
            return false;
        }

        // Refuse if the foreground is a WhispUI window itself (LogWindow, HUD,
        // Settings). Avoids the false positive where we would paste into our
        // own logs while the user reads them.
        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        if (fgPid == ownPid)
        {
            _log.Warning(LogSource.Paste, "skipped: foreground is WhispUI itself. Clipboard holds the text — Ctrl+V in the right window.");
            return false;
        }

        // UI Automation probe on the currently focused element. If the probe
        // is anything other than "yes, it's an Edit or Document", we bail out
        // to the clipboard-only path. No speculative paste.
        bool editable = UIAutomation.IsFocusedElementTextEditable(out string uiaDiag);
        _log.Verbose(LogSource.Paste, $"UIA: {uiaDiag}");
        if (!editable)
        {
            _log.Warning(LogSource.Paste, "skipped: focused element is not a text field. Clipboard holds the text — Ctrl+V where you want it.");
            return false;
        }

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
            _log.Warning(LogSource.Paste, $"partial: SendInput injected {sent}/{inputs.Length} events. Clipboard holds the text — Ctrl+V manually.");
            return false;
        }

        _log.Info(LogSource.Paste, $"Ctrl+V sent to {Win32Util.DescribeHwnd(fg)}");
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

        _idleTimer?.Dispose();

        if (_ctx != IntPtr.Zero)
        {
            NativeMethods.whisper_free(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
