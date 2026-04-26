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

    // Microphone level, linear RMS [0, 1], throttled ~20 Hz (one emission per
    // 50 ms sub-window of the captured audio). Fired from the recording thread
    // — subscribers marshal to UI. Consumer-less for now (HUD contour animation
    // will hook in later).
    public event Action<float>? AudioLevel;

    // Per-segment notification from whisper.cpp's new_segment_callback — fired
    // after the segment has been appended to _segments. T0/T1 are centiseconds
    // since the start of the current Transcribe call. Confidence is the linear
    // average p over text tokens. Fired from the inference thread — subscribers
    // marshal to UI.
    public event Action<SegmentArgs>? NewSegment;

    public readonly record struct SegmentArgs(string Text, long T0, long T1, float Confidence);

    // All StatusChanged / TranscriptionFinished emissions route through these
    // two helpers so the startup warmup can silence them in a single place
    // instead of peppering the pipeline with if-checks. _isWarmup is set only
    // around the Transcribe() call inside Warmup() — model load and other
    // lifecycle events stay visible on the tray.
    private volatile bool _isWarmup = false;

    private void RaiseStatus(string status)
    {
        if (_isWarmup) return;
        StatusChanged?.Invoke(status);
    }

    private void RaiseFinished(TranscriptionOutcome outcome)
    {
        if (_isWarmup) return;
        TranscriptionFinished?.Invoke(outcome);
    }

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

    // whisper.cpp abort_callback signature: bool fn(void* user_data). Called
    // periodically by the decoder; returning true requests a clean stop —
    // whisper_full returns 0 with the segments emitted so far. We use it as
    // the kill switch for the repetition-loop detector.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool WhisperAbortCallback(IntPtr user_data);

    private WhisperAbortCallback? _abortCallback;
    private volatile bool _abortRequested;
    private readonly RepetitionDetector _repetitionDetector = new();

    private readonly record struct TranscribedSegment(string Text, long T0, long T1, float NoSpeechProb);

    // Stopwatch started at the beginning of whisper_full — read by OnNewSegment
    // to log elapsed time since inference start (cumulative, not per-segment).
    private System.Diagnostics.Stopwatch? _transcribeSw;

    // Decoding strategy label cached at the start of Transcribe, used in the
    // final recap log (e.g. "beam5" or "greedy").
    private string _strategyLabel = "";

    // Stopwatch started at the beginning of each recording (used for logs).
    private System.Diagnostics.Stopwatch? _recordingSw;

    // Per-recording RMS history — one linear-RMS sample per 50 ms sub-window
    // (so ~20 Hz). Cleared at every Recording start, fed by EmitAudioLevels
    // in flow order. Drives:
    //   - the Tail-600 ms diagnostic at Stop (last 12 samples — sidesteps the
    //     bytes-buffer ordering ambiguity of the old re-computation path),
    //   - the per-recording mic telemetry summary (min / percentiles / max
    //     in dBFS) Louis uses to calibrate MinDbfs / MaxDbfs / EmaAlpha
    //     against his actual hardware response.
    // Pre-reserved for ~10 minutes at 20 Hz; the List grows past that without
    // a resize allocation explosion thanks to standard doubling.
    private readonly List<float> _rmsLog = new(capacity: 20 * 60 * 10);

    // Auto-calibration ring buffer — one MicrophoneTelemetryPayload per
    // recording. Only filled when LevelWindow.AutoCalibrationEnabled is on.
    // Once the buffer has AutoCalibrationSamples entries, the engine pushes
    // a fresh MinDbfs/MaxDbfs back into Settings + HudChrono so the HUD
    // tracks the user's hardware drift without manual re-tuning. See
    // TryAutoCalibrate below for the heuristic.
    private readonly Queue<Logging.MicrophoneTelemetryPayload> _autoCalibBuffer = new();

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

    // VAD summary fields parsed from the whisper.cpp VAD log lines while
    // _vadCapturing. All sentinels = -1 mean "not yet parsed" — included in
    // the consolidated Verbose summary line when present, omitted gracefully
    // if the line shape changes upstream. Raw whisper_vad* lines are
    // suppressed from the log surface; the single consolidated Verbose line
    // (technical totals, LogSource.Whisper) plus a minimalist Narrative
    // (UX-facing) replace them.
    private float _vadSpeechSec     = -1f;
    private int   _vadSegments      = -1;
    private float _vadReductionPct  = -1f;
    private float _vadInferenceMs   = -1f;
    private int   _vadMappingPoints = -1;
    private static readonly Regex _vadSpeechRegex = new(
        @"total duration of speech segments:\s*([\d.]+)\s*s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _vadSegmentsRegex = new(
        @"detected\s+(\d+)\s+speech segments",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _vadReductionRegex = new(
        @"\(([\d.]+)%\s*reduction\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _vadInferenceRegex = new(
        @"vad time\s*=\s*([\d.]+)\s*ms",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _vadMappingRegex = new(
        @"mapping table with\s+(\d+)\s+points",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Effective compute backend, parsed from the first whisper.cpp log lines
    // emitted during model init (ggml_vulkan: / ggml_cuda_init: / ggml_metal_init:).
    // Defaults to "CPU" when no GPU prefix is seen before LoadModel finishes —
    // which matches the runtime behaviour of ggml when no GPU backend is
    // initialised. Read by LoadModel to include the backend in the Success log
    // (so the user sees "backend=Vulkan" in the tray tooltip source without
    // having to enable Verbose to see the raw init line).
    private volatile string _detectedBackend = "CPU";

    // Warmup result flags — silent at warmup, consumed at the first hotkey press
    // so problems detected upfront surface before the recording pipeline starts.
    // All three default to true so a missing Warmup() run never reports a false
    // negative. Written from the warmup thread, read from the hotkey thread:
    // int-backed volatile fields (0 = false, 1 = true) because bool isn't a
    // valid volatile type in C#.
    private volatile int _micWarmupOk    = 1;
    private volatile int _modelWarmupOk  = 1;
    private volatile int _ollamaWarmupOk = 1;
    private volatile int _warmupFlagsConsumed = 0;

    public bool MicrophoneWarmupOk => _micWarmupOk    == 1;
    public bool ModelWarmupOk      => _modelWarmupOk  == 1;
    public bool OllamaWarmupOk     => _ollamaWarmupOk == 1;

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

                // Backend detection — ggml prints a stable prefix on init for
                // each GPU backend. First hit wins and sticks (volatile field
                // read by LoadModel). No match = CPU by default. Matching the
                // prefix rather than a full sentence keeps this robust across
                // whisper.cpp version bumps.
                if (_detectedBackend == "CPU")
                {
                    if (msg.StartsWith("ggml_vulkan:", StringComparison.Ordinal))
                        _detectedBackend = "Vulkan";
                    else if (msg.StartsWith("ggml_cuda_init:", StringComparison.Ordinal) ||
                             msg.StartsWith("ggml_cuda:", StringComparison.Ordinal))
                        _detectedBackend = "CUDA";
                    else if (msg.StartsWith("ggml_metal_init:", StringComparison.Ordinal) ||
                             msg.StartsWith("ggml_metal:", StringComparison.Ordinal))
                        _detectedBackend = "Metal";
                }

                // Whisper.cpp's VAD module emits per-segment chatter (one line per
                // detected segment plus several summary lines) — on long recordings
                // that's hundreds of lines arriving in a single batch after VAD has
                // already finished, with no temporal value. We parse the summaries
                // in-flight, then emit one consolidated technical Verbose line plus
                // a minimalist UX-facing Narrative, and suppress every raw
                // whisper_vad* line from the log surface (see the early return below).
                bool isVadLine = msg.StartsWith("whisper_vad", StringComparison.Ordinal);

                // VAD sentinel: only while whisper_full is running (_vadCapturing).
                // Start the stopwatch on the first "whisper_vad" line (matches both
                // "whisper_vad:" high-level messages and "whisper_vad_*" sub-module
                // lines), stop it on the explicit end marker "Reduced audio from
                // X to Y samples" — emitted last by the VAD module before whisper
                // moves on to transcription proper.
                if (_vadCapturing && isVadLine)
                {
                    if (_vadSw is null)
                    {
                        _vadSw = System.Diagnostics.Stopwatch.StartNew();
                        _log.Narrative(LogSource.Transcribe, "Looking for speech in the recording — a small detector is scanning the audio for spoken segments.");
                    }

                    // Parse-once: ignore later matches in the same window.
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

                    if (_vadSegments < 0)
                    {
                        var m = _vadSegmentsRegex.Match(msg);
                        if (m.Success && int.TryParse(
                                m.Groups[1].Value,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int segs))
                        {
                            _vadSegments = segs;
                        }
                    }

                    if (_vadReductionPct < 0)
                    {
                        var m = _vadReductionRegex.Match(msg);
                        if (m.Success && float.TryParse(
                                m.Groups[1].Value,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out float pct))
                        {
                            _vadReductionPct = pct;
                        }
                    }

                    if (_vadInferenceMs < 0)
                    {
                        var m = _vadInferenceRegex.Match(msg);
                        if (m.Success && float.TryParse(
                                m.Groups[1].Value,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out float ms))
                        {
                            _vadInferenceMs = ms;
                        }
                    }

                    if (_vadMappingPoints < 0)
                    {
                        var m = _vadMappingRegex.Match(msg);
                        if (m.Success && int.TryParse(
                                m.Groups[1].Value,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int pts))
                        {
                            _vadMappingPoints = pts;
                        }
                    }

                    // Explicit end marker — stable across whisper.cpp versions
                    // since the function emitting it ("whisper_vad_segments_*")
                    // is the last step before returning to whisper_full when
                    // speech was actually found. The no-speech path is closed
                    // by the post-whisper_full fallback in Transcribe().
                    if (!_vadEnded && msg.IndexOf("Reduced audio from", StringComparison.Ordinal) >= 0)
                    {
                        _vadSw?.Stop();
                        _vadEnded = true;
                        EmitVadSummary(_vadSw?.Elapsed.TotalSeconds ?? 0);
                    }
                }

                // Suppress raw whisper_vad* lines — they're consumed by the
                // instrumentation above and replaced by the consolidated Verbose
                // line + minimalist Narrative emitted at the "Reduced audio from"
                // marker. Other whisper.cpp lines (Vulkan init, backend, etc.)
                // continue through the level switch below.
                if (isVadLine) return;

                // Downgrade the "no GPU found" line emitted by the second
                // whisper_backend_init_gpu call (triggered during VAD context
                // creation). whisper.cpp historically hardcoded use_gpu=false
                // in whisper_vad_init_context — the main Whisper backend runs
                // fine on Vulkan, this second init only reports "no GPU found"
                // because nothing asked for GPU. Bénin but alarming at Warn
                // level. Kept as Verbose so it stays discoverable in diagnostic
                // mode. Targeted match: both prefix and body required, to avoid
                // masking a real GPU failure phrased differently.
                if (msg.StartsWith("whisper_backend_init_gpu", StringComparison.Ordinal) &&
                    msg.IndexOf("no GPU found", StringComparison.Ordinal) >= 0)
                {
                    _log.Verbose(LogSource.Whisper, msg);
                    return;
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
            _log.Warning(LogSource.Engine, $"whisper_log_set unavailable: {ex.Message}");
        }
    }

    // Emits the consolidated technical Verbose line plus the UX-facing
    // Narrative for the VAD cycle. Two call sites:
    //   - the hook, on the "Reduced audio from" end marker (speech path).
    //   - Transcribe(), as a post-whisper_full fallback when VAD ran but no
    //     end marker was seen (no-speech path: whisper.cpp short-circuits
    //     before "Reduced audio" when 0 segments, leaving _vadEnded=false).
    // Caller is responsible for setting _vadEnded=true to keep this idempotent.
    private void EmitVadSummary(double vadSec)
    {
        var parts = new List<string>();
        if (_vadSegments      >= 0) parts.Add($"{_vadSegments} segments");
        if (_vadSpeechSec     >= 0) parts.Add($"speech {_vadSpeechSec:F1} s");
        if (_vadReductionPct  >= 0) parts.Add($"reduction {_vadReductionPct:F1}%");
        if (_vadInferenceMs   >= 0) parts.Add($"inference {_vadInferenceMs:F0} ms");
        if (_vadMappingPoints >= 0) parts.Add($"mapping {_vadMappingPoints} pts");
        parts.Add($"wall {vadSec:F1} s");
        _log.Verbose(LogSource.Whisper, "vad: " + string.Join(" | ", parts));

        // UX-facing Narrative — minimalist, no technical figures beyond
        // speech duration. Distinguishes "speech found" from "nothing found".
        if (_vadSegments == 0)
        {
            _log.Narrative(LogSource.Transcribe, "No speech detected in the recording.");
        }
        else if (_vadSpeechSec >= 0)
        {
            _log.Narrative(LogSource.Transcribe, $"Speech detected — {_vadSpeechSec:F1} s of speech. Passing to Whisper for transcription.");
        }
        else
        {
            _log.Narrative(LogSource.Transcribe, "Speech detected. Passing to Whisper for transcription.");
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
        RaiseStatus("Loading model…");

        if (!File.Exists(_modelPath))
        {
            _log.Warning(
                LogSource.Model,
                $"load aborted | reason=file_not_found | path={_modelPath}",
                new UserFeedback(
                    "Whisper model not found",
                    "See in settings.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus("Ready");
            return false;
        }

        double fileMb = new FileInfo(_modelPath).Length / 1024.0 / 1024.0;
        string basename = Path.GetFileName(_modelPath);
        _log.Info(LogSource.Model, "Loading model");
        _log.Narrative(LogSource.Model, $"Loading the Whisper model into GPU memory — a {fileMb:F0} MB speech recognizer is being prepared so transcription can run locally.");
        _log.Verbose(LogSource.Model, $"load start | file={basename} | file_mb={fileMb:F1} | use_gpu=1");

        // Reset the backend before init so a re-load after an idle unload
        // picks up the current backend rather than the one detected at the
        // first startup. The log hook fires synchronously during init and
        // overwrites this field as soon as it sees a ggml_vulkan:/cuda/metal
        // line; if none appear, CPU is the correct fallback.
        _detectedBackend = "CPU";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr ctxParamsPtr = NativeMethods.whisper_context_default_params_by_ref();
        WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
        NativeMethods.whisper_free_context_params(ctxParamsPtr);
        ctxParams.use_gpu = 1;

        _ctx = NativeMethods.whisper_init_from_file_with_params(_modelPath, ctxParams);
        sw.Stop();
        _log.Verbose(LogSource.Engine, $"whisper_init_from_file returned ctx={_ctx}");

        if (_ctx == IntPtr.Zero)
        {
            _log.Error(
                LogSource.Init,
                $"load failed | path={_modelPath}",
                new UserFeedback(
                    "Failed to load model",
                    "Low GPU memory or corrupt file.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus("Ready");
            return false;
        }

        _log.Success(LogSource.Model, $"Model loaded ({_detectedBackend})");
        _log.Verbose(LogSource.Model, $"load complete | load_ms={sw.ElapsedMilliseconds} | backend={_detectedBackend}");
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
            _log.Verbose(LogSource.Model, "on-demand load | reason=first_use_or_after_idle_unload");
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
            _log.Success(LogSource.Model, "Model unloaded");
            _log.Verbose(LogSource.Model, $"model unloaded | idle_s={MODEL_IDLE_TIMEOUT_MS / 1000} | state=vram-freed");
            RaiseStatus("Ready");
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

    // ── Warmup ──────────────────────────────────────────────────────────────
    //
    // Runs a silent "first inference" at startup so the real first hotkey
    // press doesn't pay the cold cost (context alloc + GPU warm + kernel
    // compile + weight paging). We push 1.6 s of zero samples through the
    // full Transcribe() path — long enough for Silero VAD to execute at
    // least one window and for whisper_full to enter. With an all-zero
    // buffer VAD returns 0 speech segments and whisper_full short-circuits
    // before any decode, so the cost stays in the low-hundreds-of-ms range.
    //
    // StatusChanged / TranscriptionFinished are gated during Transcribe()
    // via _isWarmup so the HUD never appears and the tray doesn't flash.
    // LoadModel stays audible ("Loading model" → "Ready") because that's
    // done before _isWarmup flips — useful signal on the tray that the
    // engine is warming without any intrusive UI.
    //
    // Fire-and-forget on a background thread — the call site in
    // App.OnLaunched must not block UI-thread startup. Named Warmup (not
    // WarmupAsync) because the method returns void: the *Async suffix in
    // C# is reserved for methods returning Task / ValueTask.
    public void Warmup()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _log.Info(LogSource.Init, "Warmup start");

                // 1) Mic probe — same code path StartRecording uses, just the
                //    probe result is stored instead of blocking the recording.
                _micWarmupOk = TryProbeMicrophone(out _) ? 1 : 0;

                // 2) Model load. On failure we stop here — nothing else can
                //    be tested without the model — and flag model+ollama as
                //    failing so the first hotkey surfaces the right message.
                if (!EnsureModelLoaded())
                {
                    _modelWarmupOk  = 0;
                    _ollamaWarmupOk = 0;
                    _log.Warning(LogSource.Init,
                        $"warmup aborted | reason=model_load_failed | total_ms={sw.ElapsedMilliseconds} | mic_ok={MicrophoneWarmupOk} | model_ok=false | ollama_ok=skipped");
                    return;
                }
                _modelWarmupOk = 1;

                // Silent Transcribe through the full pipeline (VAD + whisper_full)
                // to pay the first-inference cost before the user presses any
                // hotkey. 1.6 s at 16 kHz = 25 600 samples. Any buffer ≥ one VAD
                // window works; 1.6 s keeps it well above the min-speech /
                // min-silence thresholds exposed in Settings so the short-circuit
                // triggers reliably whatever the user tuned.
                float[] silentBuffer = new float[25_600];
                _isWarmup = true;
                try
                {
                    Transcribe(silentBuffer);
                }
                finally
                {
                    _isWarmup = false;
                }

                // 3) Ollama health-check. Skipped (and left as OK) when the LLM
                //    feature is disabled — no rewriter needed, no warning to
                //    surface. 3 s timeout inside IsAvailableAsync keeps the
                //    warmup bounded even if the endpoint is a dead address.
                var llmSettings = Settings.SettingsService.Instance.Current.Llm;
                if (llmSettings.Enabled)
                {
                    try
                    {
                        var ollama = new Llm.OllamaService(
                            () => Settings.SettingsService.Instance.Current.Llm.OllamaEndpoint);
                        bool reachable = ollama.IsAvailableAsync().GetAwaiter().GetResult();
                        _ollamaWarmupOk = reachable ? 1 : 0;
                    }
                    catch
                    {
                        _ollamaWarmupOk = 0;
                    }
                }

                sw.Stop();
                _log.Success(LogSource.Init, "Warmup complete");
                _log.Verbose(LogSource.Init,
                    $"warmup complete | total_ms={sw.ElapsedMilliseconds} | mic_ok={MicrophoneWarmupOk} | model_ok={ModelWarmupOk} | ollama_ok={OllamaWarmupOk}");
            }
            catch (Exception ex)
            {
                _isWarmup = false;
                _log.Error(LogSource.Init, $"Warmup failed | error={ex.GetType().Name}: {ex.Message}");
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    // ── Start recording ─────────────────────────────────────────────────────

    // manualProfileName: when non-null, rewrite the transcription with that
    // profile at the end (manual primary/secondary rewrite hotkeys). When
    // null, fall back to AutoRewriteRules based on recording duration.
    public void StartRecording(string? manualProfileName, bool shouldPaste)
    {
        if (_isRecording) return;

        // Consume the warmup flags on the first StartRecording call — surface
        // any problems detected silently at startup before the recording begins.
        // Interlocked.Exchange makes the consumption race-free even if two
        // hotkeys land back-to-back (second caller reads 1 and skips the block).
        // Subsequent hotkeys rely on the live probes below and on the LLM
        // rewrite error paths — the warmup flags are an early-warning, not a
        // permanent gate.
        if (System.Threading.Interlocked.Exchange(ref _warmupFlagsConsumed, 1) == 0)
        {
            if (!ModelWarmupOk)
            {
                _log.Error(
                    LogSource.Init,
                    "warmup flag | model_ok=false",
                    new UserFeedback(
                        "Model not ready",
                        "Load failed. See in settings.",
                        UserFeedbackSeverity.Error,
                        UserFeedbackRole.Replacement));
                return;
            }
            if (!OllamaWarmupOk)
            {
                _log.Warning(
                    LogSource.Init,
                    "warmup flag | ollama_ok=false",
                    new UserFeedback(
                        "Rewriter unavailable",
                        "Ollama is not reachable. No rewrite this session.",
                        UserFeedbackSeverity.Warning,
                        UserFeedbackRole.Overlay));
                // Proceed with recording — rewrite is optional, transcription
                // and clipboard copy still deliver the raw text.
            }
            // Mic: the live probe below emits its own UserFeedback, so a
            // false mic_ok flag only needs a diagnostic line here. If the user
            // plugged the mic in between warmup and hotkey, the live probe
            // passes and we proceed normally.
            if (!MicrophoneWarmupOk)
            {
                _log.Warning(LogSource.Init, "warmup flag | mic_ok=false (live probe below)");
            }
        }

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
                new UserFeedback(title, body,
                    UserFeedbackSeverity.Error, UserFeedbackRole.Replacement));
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
                    RaiseFinished(TranscriptionOutcome.None);
                    return;
                }

                _recordingSw = System.Diagnostics.Stopwatch.StartNew();
                RaiseStatus("Recording…");
                _log.Narrative(LogSource.Record, "Recording from the microphone. Capture continues until you press the hotkey again.");

                float[] audio = Record();
                _isRecording = false;
                RaiseStatus("Transcribing…");
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
                        "Pipeline crashed",
                        "Try again. Check logs if it persists.",
                        UserFeedbackSeverity.Error,
                        UserFeedbackRole.Replacement));
                RaiseStatus("Ready");
                RaiseFinished(TranscriptionOutcome.None);
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
        2 => ("No microphone detected", "Plug in a mic. See in settings."),
        6 => ("No microphone detected", "Plug in a mic. See in settings."),
        4 => ("Microphone in use",      "Another app is using it. Try again."),
        _ => ("Microphone unavailable", $"Audio device open failed (MMSYSERR {err}).")
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
        // 50 ms buffers (1600 bytes) so AudioLevel events fire at a steady
        // ~20 Hz spread across time, not in bursts of 10 every 500 ms.
        // 500 ms bursts were the original size — trivial driver workload
        // back then but catastrophic for a real-time HUD animation because
        // the outline couldn't react inside a spoken word. 4 circular
        // buffers give 200 ms of headroom if the drain loop stalls, still
        // enough on any modern scheduler. 20 waveIn callbacks/s is a
        // no-op for modern drivers (WASAPI defaults to 10 ms periods).
        const int  BYTES_PER_BUF  = 16000 * 2 * 50 / 1000; // 50ms × 16kHz × 2 bytes/sample

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
        // Reset the per-recording RMS series — the previous session's tail
        // must not leak into this run's telemetry summary.
        _rmsLog.Clear();
        _log.Info(LogSource.Record, "Recording start");
        _log.Verbose(LogSource.Record, "capture start | sample_rate=16 kHz | channels=mono");

        // Snapshot the cap at recording start so a mid-recording Settings
        // change doesn't shorten or extend a session already in progress.
        int maxDurationSec = Settings.SettingsService.Instance.Current.Recording.MaxRecordingDurationSeconds;
        bool capHit = false;
        int buffersReceived = 0;

        // Live low-audio tracker — "did the user speak at a healthy volume
        // at least once in the first 5 s?" phrasing. The warning fires once
        // per recording if the answer is no.
        //
        // Why not just count sub-threshold duration: a short peak (finger
        // snap, breath hit) spikes above -50 dBFS for 50-100 ms, which would
        // reset a naive consecutive counter and hide a genuinely broken mic.
        // Instead we track the positive case — a stretch of ≥200 ms where
        // dBFS stays ≥-45 is strong evidence of real speech (one full
        // syllable on a typical USB mic), and we lock the warning off for
        // the rest of the recording. Peaks are too short to clear 200 ms
        // consecutively, so they can't fake a pass.
        //
        // Threshold chosen by observation: modern condenser/USB mics at
        // typing distance produce normal conversation around -35 to -45
        // dBFS. The old -35 dBFS threshold rejected Louis's mic even during
        // active speech. -45 dBFS leaves headroom for quieter setups while
        // still catching the broken-mic / unplugged / miles-away scenarios
        // (those sit below -55 dBFS).
        const double NormalVoiceDbfsThreshold = -45.0;
        const int    NormalVoiceSustainedMs   = 200;
        const int    WarnAfterSilenceMs       = 5000;
        int  healthyVoiceConsecutiveMs = 0;
        int  recordingMs               = 0;
        bool userVoiceConfirmed        = false;
        bool lowAudioWarned            = false;
        bool captureLagWarned          = false;

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
                        _log.Warning(LogSource.Record, $"empty buffer | index={i}");
                    }
                    else
                    {
                        var data = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                        allBytes.AddRange(data);
                        EmitAudioLevels(data);
                        buffersReceived++;

                        // Per-buffer low-audio tracker. 16 kHz mono PCM16 = 32
                        // bytes per ms. Two state machines side by side:
                        //   • healthyVoiceConsecutiveMs: consecutive duration
                        //     above NormalVoiceDbfsThreshold. Hitting
                        //     NormalVoiceSustainedMs flips userVoiceConfirmed,
                        //     which permanently disarms the warning for this
                        //     recording.
                        //   • recordingMs: total captured duration. Once we
                        //     pass WarnAfterSilenceMs without
                        //     userVoiceConfirmed being set, emit the overlay
                        //     warning (one-shot).
                        int bufferMs = data.Length / 32;
                        recordingMs += bufferMs;

                        if (!userVoiceConfirmed)
                        {
                            double bufferDbfs = ComputeBufferDbfs(data);
                            if (bufferDbfs >= NormalVoiceDbfsThreshold)
                            {
                                healthyVoiceConsecutiveMs += bufferMs;
                                if (healthyVoiceConsecutiveMs >= NormalVoiceSustainedMs)
                                {
                                    userVoiceConfirmed = true;
                                }
                            }
                            else
                            {
                                healthyVoiceConsecutiveMs = 0;
                            }
                        }

                        if (!lowAudioWarned && !userVoiceConfirmed && recordingMs >= WarnAfterSilenceMs)
                        {
                            lowAudioWarned = true;
                            _log.Warning(
                                LogSource.Record,
                                $"low audio detected | recording_ms={recordingMs} | no healthy voice ≥{NormalVoiceSustainedMs} ms above {NormalVoiceDbfsThreshold} dBFS",
                                new UserFeedback(
                                    "Low audio detected",
                                    "Your mic is picking up very little sound.",
                                    UserFeedbackSeverity.Warning,
                                    UserFeedbackRole.Overlay));
                        }
                    }

                    hdr.dwFlags &= ~(uint)0x00000001;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }

            // Capture lag — fire once per recording when the ring buffer is
            // really under pressure. With 4 buffers × 50 ms and a 100 ms
            // wait, finding 1-2 buffers WHDR_DONE per iteration is normal;
            // 3+ means the consumer fell at least 150 ms behind the producer.
            if (!captureLagWarned && bufferDoneCount >= 3)
            {
                captureLagWarned = true;
                _log.Warning(LogSource.Record, $"capture lag | buffers_ready={bufferDoneCount}");
            }

            // Duration cap — forces a stop as if the user had pressed the
            // hotkey. Audio captured so far still flows through the full
            // pipeline. Only triggers once per session.
            double curSec = allBytes.Count / 32000.0;
            if (!capHit && maxDurationSec > 0 && curSec >= maxDurationSec)
            {
                capHit = true;
                int minutes = maxDurationSec / 60;
                _log.Warning(LogSource.Record,
                    $"duration cap reached | audio_sec={curSec:F1} | cap_sec={maxDurationSec}");
                _log.Narrative(LogSource.Record,
                    $"Recording hit the {minutes} min cap — stopping automatically. The audio captured so far will be transcribed.");
                _stopRecording = true;
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
                // Push the drained tail through the same sub-window mill so
                // _rmsLog covers the full session (the in-loop EmitAudioLevels
                // path stops as soon as _stopRecording flips, leaving the last
                // 1-3 buffers undrained without this explicit pass).
                EmitAudioLevels(data);
            }
            NativeMethods.waveInUnprepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
            Marshal.FreeHGlobal(bufPtrs[i]);
            Marshal.FreeHGlobal(hdrPtrs[i]);
        }

        NativeMethods.waveInClose(hWaveIn);
        NativeMethods.CloseHandle(hEvent);

        double totalSec = allBytes.Count / 32000.0;

        // Full-buffer aggregate: mean RMS + peak amplitude over the entire
        // recording. Single pass over allBytes, cost is negligible vs the
        // upcoming whisper_full call (~1 ms for a minute of audio at 16 kHz).
        // dbfs_avg = 20*log10(rms_avg), floored at -120 dBFS when the buffer
        // is pure zero to avoid −∞ in the log.
        double aggSumSq = 0;
        double aggPeak  = 0;
        int nAggSamples = allBytes.Count / 2;
        for (int i = 0; i < nAggSamples; i++)
        {
            short s = (short)(allBytes[i * 2] | (allBytes[i * 2 + 1] << 8));
            double v = s / 32768.0;
            aggSumSq += v * v;
            double av = v < 0 ? -v : v;
            if (av > aggPeak) aggPeak = av;
        }
        double rmsAvg  = nAggSamples > 0 ? Math.Sqrt(aggSumSq / nAggSamples) : 0;
        double dbfsAvg = rmsAvg > 0 ? 20.0 * Math.Log10(rmsAvg) : -120.0;

        _log.Info(LogSource.Record, $"Recording complete ({totalSec:F1} s)");
        _log.Verbose(LogSource.Record,
            $"capture complete | audio_sec={totalSec:F1} | buffers={buffersReceived} | bytes={allBytes.Count} | rms_avg={rmsAvg:F4} | rms_peak={aggPeak:F4} | dbfs_avg={dbfsAvg:F1}");
        _log.Narrative(LogSource.Record, $"Captured {totalSec:F1} s of audio. Moving on to analysis and transcription.");

        // Mic telemetry — distribution + tail summary derived from _rmsLog.
        // Replaces the previous Tail-on-allBytes computation, which was
        // returning RMS=0 (= -96.7 dBFS, the "uninitialised buffer" floor)
        // even on sessions Whisper transcribed perfectly. Root cause:
        // the post-Stop drain loop concatenates buffers in WHDR index order
        // 0..N-1, which does not always match temporal order at Stop, so
        // the last 600 ms read from the byte tail could land on an
        // out-of-order or partially-zeroed buffer. _rmsLog is fed in flow
        // order by EmitAudioLevels (during Recording AND in the drain
        // pass above), so its tail genuinely reflects the final ~600 ms
        // of audio.
        LogRecordingTelemetry();

        return PcmToFloat(allBytes.ToArray());
    }

    // Logs the per-recording mic telemetry: full-session percentile sweep
    // (min / p10 / p25 / p50 / p75 / p90 / max in dBFS) plus the legacy
    // Tail-600 ms diagnostic (active vs silent at Stop). Both lines are
    // Info level so they appear in the Activity selector without forcing
    // Verbose. Called once per Recording cycle from the Stop path.
    //
    // Linear RMS → dBFS via 20·log10(rms); guarded against rms ≤ 0
    // (returns -120 dBFS, the conventional "digital silence" floor — the
    // -96.7 dBFS we used to see corresponded to a single-LSB residual
    // from a zero-initialised buffer, indistinguishable from true silence
    // and historically misleading).
    private void LogRecordingTelemetry()
    {
        if (_rmsLog.Count == 0)
        {
            _log.Warning(LogSource.Record, "Mic telemetry: no RMS samples captured (recording too short or audio thread starved)");
            return;
        }

        static double ToDbfs(float linear) =>
            linear > 0f ? 20.0 * Math.Log10(linear) : -120.0;

        int n = _rmsLog.Count;

        // ── Tail-600 ms diagnostic (always on) ─────────────────────────────
        //
        // Root-mean-square of the last 12 sub-windows. Sums the sub-window
        // squared RMS values and re-roots, which is the mathematically
        // correct way to combine RMS samples (NOT a plain mean of RMS).
        // -50 dBFS keeps the active/silent threshold from the previous
        // diagnostic so existing log readers stay calibrated.
        int tailCount = Math.Min(12, n);
        double tailSumSq = 0;
        for (int i = _rmsLog.Count - tailCount; i < _rmsLog.Count; i++)
        {
            double v = _rmsLog[i];
            tailSumSq += v * v;
        }
        double tailRms = Math.Sqrt(tailSumSq / tailCount);
        double tailDbfs = ToDbfs((float)tailRms);
        int tailMs = tailCount * 50;
        string tailState = tailDbfs > -50 ? "active" : "silent";
        _log.Info(LogSource.Record,
            $"Tail {tailMs}ms RMS={tailRms:F4} ({tailDbfs:F1} dBFS) — signal {tailState} at Stop");

        // ── Distribution payload (always computed) ─────────────────────────
        //
        // Builds the per-Recording percentile + mean payload regardless of
        // the log/disk toggles, because auto-calibration consumes it
        // independently. Computing this is cheap — sort of ~few thousand
        // floats — so we don't gate it.
        var sorted = _rmsLog.ToArray();
        Array.Sort(sorted);

        // Percentile picker: nearest-rank, clamped to [0, n-1]. Good enough
        // for human-readable telemetry; we're not feeding a stats engine.
        float Pick(double frac) => sorted[Math.Clamp((int)(n * frac), 0, n - 1)];

        float min = sorted[0];
        float max = sorted[n - 1];
        float p10 = Pick(0.10), p25 = Pick(0.25), p50 = Pick(0.50);
        float p75 = Pick(0.75), p90 = Pick(0.90);

        // Mean of linear RMS — the number to compare against MaxDbfs window
        // when calibrating the HUD response. NOT the mean of dBFS values
        // (logs of small numbers skew that mean).
        double meanLinear = 0;
        for (int i = 0; i < n; i++) meanLinear += sorted[i];
        meanLinear /= n;
        double meanDbfs = ToDbfs((float)meanLinear);

        double durSec = n * 0.05; // 50 ms per sub-window

        var payload = new Logging.MicrophoneTelemetryPayload(
            DurationSeconds: durSec,
            Samples:         n,
            MinDbfs:         ToDbfs(min),
            P10Dbfs:         ToDbfs(p10),
            P25Dbfs:         ToDbfs(p25),
            P50Dbfs:         ToDbfs(p50),
            P75Dbfs:         ToDbfs(p75),
            P90Dbfs:         ToDbfs(p90),
            MaxDbfs:         ToDbfs(max),
            MeanRms:         meanLinear,
            MeanDbfs:        meanDbfs,
            TailRms:         tailRms,
            TailDbfs:        tailDbfs,
            TailState:       tailState);

        // ── Optional: emit the Microphone telemetry event ──────────────────
        //
        // Single event, two consumers fanned out by the bus:
        //   - LogWindow renders the human-readable Text via the Microphone
        //     template (kind=Microphone routes through the template
        //     selector to the Info layout).
        //   - JsonlFileSink writes the structured payload to
        //     <telemetry>/microphone.jsonl.
        // Both gated by Settings ▸ Telemetry ▸ Log microphone — the sink
        // re-checks the toggle at write time (defence in depth) and
        // returning early here keeps the LogWindow quiet too.
        bool micTelemetryEnabled = Settings.SettingsService.Instance
            .Current.Telemetry.MicrophoneTelemetry;
        if (micTelemetryEnabled)
            Logging.TelemetryService.Instance.Microphone(payload);

        // ── Optional: auto-calibrate the dBFS window ───────────────────────
        TryAutoCalibrate(payload);
    }

    // Auto-calibration heuristic — runs after every Recording when
    // LevelWindow.AutoCalibrationEnabled is true, independent of the
    // Log microphone toggle (the payload is always computed in
    // LogRecordingTelemetry above).
    //
    // Strategy:
    //   - Keep the last N MicrophoneTelemetryPayloads in a ring buffer
    //     (N = LevelWindow.AutoCalibrationSamples, default 5).
    //   - Once the buffer is full, recompute MinDbfs / MaxDbfs from
    //     median-across-sessions percentiles, with margins:
    //       MinDbfs = median(p25) - 5 dB  — p25 (not p10) so a noise gate
    //                                       cutting to digital silence
    //                                       (-97 dBFS) doesn't drag the
    //                                       floor into "anything below
    //                                       the gate threshold". Then
    //                                       -5 dB of headroom under the
    //                                       useful-signal minimum.
    //       MaxDbfs = median(p90) + 5 dB  — voice ceiling with breathing
    //                                       room above routine peaks.
    //   - Floor clamp at -75 dBFS to guarantee we never sit on the gate
    //     even if p25 itself is in the noise floor.
    //   - Refuse to write if the resulting window collapses to < 10 dB
    //     (pathological case — e.g. all-silence sessions).
    //   - Push to settings + HudChrono statics + log a Success line.
    //
    // The buffer is in-memory only: a fresh app launch starts collecting
    // again, which is fine — calibration only fires after N consecutive
    // recordings within one process anyway, and the persisted Min/Max
    // already reflects the last successful auto-calibration.
    //
    // The user's manual slider edits override auto-calibration until the
    // next time it fires — there's no "manual flag" gating; whoever wrote
    // last wins, which is the natural behaviour from the user's POV.
    private void TryAutoCalibrate(Logging.MicrophoneTelemetryPayload payload)
    {
        var lw = Settings.SettingsService.Instance.Current.Recording.LevelWindow;
        if (!lw.AutoCalibrationEnabled) return;

        int needed = Math.Max(1, lw.AutoCalibrationSamples);

        _autoCalibBuffer.Enqueue(payload);
        while (_autoCalibBuffer.Count > needed) _autoCalibBuffer.Dequeue();
        if (_autoCalibBuffer.Count < needed) return;

        // Median across the buffer — avoids one rogue session pulling the
        // window in either direction.
        var p25s = _autoCalibBuffer.Select(p => p.P25Dbfs).OrderBy(v => v).ToArray();
        var p90s = _autoCalibBuffer.Select(p => p.P90Dbfs).OrderBy(v => v).ToArray();
        double medianP25 = p25s[p25s.Length / 2];
        double medianP90 = p90s[p90s.Length / 2];

        // -5 dB / +5 dB margins keep the HUD from sitting flush against
        // the user's measured percentiles — peaks above the median p90
        // still saturate cleanly, and the floor doesn't trigger on the
        // very edge of the silence band.
        double newMin = Math.Round(medianP25 - 5.0);
        double newMax = Math.Round(medianP90 + 5.0);

        // Floor guard — even with p25, a session dominated by gate-induced
        // silence can drag the median into the digital floor zone. Clamp
        // at -75 dBFS so we never calibrate the HUD to react to gated
        // silence. The user can still go lower manually via the slider
        // if they want to capture a quieter mic.
        if (newMin < -75) newMin = -75;

        // Sanity: dBFS window must span at least 10 dB to give the HUD a
        // visible dynamic range. A pathological all-silence buffer would
        // produce a near-flat window — skip and wait for richer sessions.
        if (newMax - newMin < 10) return;

        // Clamp to the slider domains so the persisted values stay editable.
        newMin = Math.Clamp(newMin, -90, -10);
        newMax = Math.Clamp(newMax, -60, -10);
        if (newMax <= newMin) return;

        // Check whether anything changed — avoid log spam on stable mics.
        bool changed = Math.Abs(lw.MinDbfs - newMin) >= 0.5f
                    || Math.Abs(lw.MaxDbfs - newMax) >= 0.5f;
        if (!changed) return;

        lw.MinDbfs = (float)newMin;
        lw.MaxDbfs = (float)newMax;
        Settings.SettingsService.Instance.Save();

        // Push live into HudChrono so the next sub-window already uses the
        // new calibration. App.ApplyLevelWindow is the single point of
        // truth for the static-field write.
        App.ApplyLevelWindow(lw);

        _log.Success(LogSource.Record,
            $"Auto-calibrated level window: Min={newMin:F0} Max={newMax:F0} dBFS "
          + $"(median over {needed} sessions, p25-5dB / p90+5dB margins)");
    }

    // waveIn delivers 50ms PCM16 buffers (BYTES_PER_BUF = 1600 bytes); the
    // sub-window walker below loops at most once per call but keeps the
    // pattern in case the buffer size changes. AudioLevel fires at ~20 Hz —
    // fine enough for a smooth contour animation without swamping
    // subscribers. RMS is linear [0, 1], clamped so a rare overshoot from
    // quantization never escapes the range.
    //
    // Collect side-effect: every sub-window RMS is appended to _rmsLog. The
    // accumulation runs unconditionally (independent of AudioLevel
    // subscription) so the Stop-time mic-telemetry summary reflects the
    // entire session even when the HUD isn't listening.
    private void EmitAudioLevels(byte[] pcm16)
    {
        var handler = AudioLevel;

        const int SubWindowMs     = 50;
        const int BytesPerSubWin  = 16000 * 2 * SubWindowMs / 1000; // 1600 bytes
        const int SamplesPerSubWin = BytesPerSubWin / 2;            // 800 samples

        int offset = 0;
        while (offset + BytesPerSubWin <= pcm16.Length)
        {
            double sumSq = 0;
            for (int i = 0; i < SamplesPerSubWin; i++)
            {
                short s = (short)(pcm16[offset + i * 2] | (pcm16[offset + i * 2 + 1] << 8));
                double v = s / 32768.0;
                sumSq += v * v;
            }
            double rms = Math.Sqrt(sumSq / SamplesPerSubWin);
            if (rms > 1.0) rms = 1.0;
            float rmsF = (float)rms;
            _rmsLog.Add(rmsF);
            handler?.Invoke(rmsF);
            offset += BytesPerSubWin;
        }
    }

    // Full-buffer dBFS — single pass over a PCM16 mono buffer, returns the
    // 20*log10(rms) value floored at -120 dBFS for a pure-zero buffer (so the
    // log domain never sees 0). Used by the live low-audio tracker in
    // Record(); intentionally coarse (whole-buffer average rather than per
    // sub-window) because the tracker is already running at buffer cadence
    // and we don't need finer granularity for a "did it stay quiet for 5 s?"
    // check.
    private static double ComputeBufferDbfs(byte[] pcm16)
    {
        int nSamples = pcm16.Length / 2;
        if (nSamples == 0) return -120.0;

        double sumSq = 0;
        for (int i = 0; i < nSamples; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            double v = s / 32768.0;
            sumSq += v * v;
        }
        double rms = Math.Sqrt(sumSq / nSamples);
        return rms > 0 ? 20.0 * Math.Log10(rms) : -120.0;
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

                // Repetition-loop guard. If the last N segments are identical
                // (case/whitespace-normalised), ask whisper to stop — logprob /
                // entropy thresholds don't catch hallucination loops where the
                // decoder is confident in the wrong token (p̂ ≈ 0.99). One more
                // segment may still surface after this call because whisper
                // only probes abort_callback between decoder steps — that's
                // fine, we still escape a 237 s × N-segments runaway.
                if (!_abortRequested &&
                    _repetitionDetector.ObserveAndShouldAbort(segText, out int streak))
                {
                    _abortRequested = true;
                    string preview = segText.Trim();
                    if (preview.Length > 60) preview = preview[..60] + "…";
                    _log.Warning(LogSource.Transcribe,
                        $"repetition loop detected — {streak} identical segments ('{preview}'); requesting whisper to abort");
                    _log.Narrative(LogSource.Transcribe,
                        "Whisper got stuck repeating the same segment — stopping transcription early. The text captured so far is preserved.");
                }

                NewSegment?.Invoke(new SegmentArgs(segText, t0, t1, avgP));

                // dur = segment duration, gap = silence (or overlap) with the previous one.
                // In a typical hallucination loop, dur≈3.0s contiguous (gap=+0.0s) repeats
                // metronomically — visually recognizable pattern without mental math.
                // A large gap signals a Whisper seek or an input silence (risky).
                double dur = (t1 - t0) / 100.0;
                double gap = _lastSegmentT1 < 0 ? 0.0 : (t0 - _lastSegmentT1) / 100.0;
                _lastSegmentT1 = t1;

                // Per-segment signal is Verbose only — one line, never two.
                // Callback fires several times per transcription, so this detail
                // does NOT belong in Activity (which is for step-level events:
                // transcribe start / transcribe complete / recap). All segment
                // data — text, timings, confidence — stays in the same Verbose
                // line so a grep on `seg #N` returns exactly one row.
                //   nsp: probability that the segment is silence/noise (0 = confident speech, 1 = confident silence).
                //   p̄ / min: average and minimum confidence over text tokens.
                //   t0/t1 are in centiseconds (1 unit = 10 ms) on the whisper.cpp side.
                //   elapsed: wall-clock time since whisper_full started (cumulative).
                double elapsedSec = _transcribeSw?.Elapsed.TotalSeconds ?? 0;
                string trimmed = segText.Trim();
                _log.Verbose(LogSource.Callback,
                    $"seg #{i + 1} | t0={t0 / 100.0:F1}s | t1={t1 / 100.0:F1}s | dur={dur:F1}s | gap={(gap >= 0 ? "+" : "")}{gap:F1}s | nsp={nsp:P0} | p̄={avgP:F2} | min={minP:F2} | tok={textTok}/{nTok} | elapsed={elapsedSec:F1}s | text=\"{trimmed}\"");
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
            RaiseStatus("Model not ready");
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        if (audio.Length == 0)
        {
            _log.Warning(LogSource.Transcribe, "empty audio buffer, nothing to transcribe");
            RaiseStatus("Ready");
            RaiseFinished(TranscriptionOutcome.None);
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

        // Abort-on-repetition plumbing. Reset the detector and the flag per
        // call so a previous transcription's state doesn't leak. Same GC
        // caveat as the segment callback — keep the delegate rooted.
        _repetitionDetector.Reset();
        _abortRequested = false;
        _abortCallback = _ => _abortRequested;
        wparams.abort_callback = Marshal.GetFunctionPointerForDelegate(_abortCallback);
        wparams.abort_callback_user_data = IntPtr.Zero;

        float audioSec = (float)audio.Length / 16_000f;
        _strategyLabel = wparams.strategy == 1 ? $"beam{wparams.beam_search_beam_size}" : "greedy";
        _log.Info(LogSource.Transcribe, "Transcribing");
        _log.Verbose(LogSource.Transcribe, $"start | audio_sec={audioSec:F1} | samples={audio.Length} | strategy={_strategyLabel}");
        // Workflow narration: VAD_END handles the "now transcribing" intro in
        // its own line (it sits between VAD completion and transcription start),
        // so a separate TRANSCRIBE_START narrative would be redundant — and
        // would even fire out of order, before VAD_START which runs inside
        // whisper_full's hook.
        string strategyLabelVerbose = wparams.strategy == 1 ? $"beam(size={wparams.beam_search_beam_size})" : "greedy";
        _log.Verbose(LogSource.Transcribe, $"params | strategy={strategyLabelVerbose} | temp={wparams.temperature:F2}+{wparams.temperature_inc:F2} | logprob_thold={wparams.logprob_thold:F2} | entropy_thold={wparams.entropy_thold:F2} | no_speech_thold={wparams.no_speech_thold:F2} | suppress_nst={wparams.suppress_nst} | carry_prompt={wparams.carry_initial_prompt} | n_threads={wparams.n_threads}");

        // Log the initial prompt sent to Whisper — conditions decoding style.
        string prompt = settings.Transcription.InitialPrompt;
        bool carry = settings.Transcription.CarryInitialPrompt;
        if (!string.IsNullOrEmpty(prompt))
        {
            string truncated = prompt.Length > 60 ? prompt[..60] + "…" : prompt;
            _log.Verbose(LogSource.Transcribe, $"prompt | len={prompt.Length} | carry={carry} | text=\"{truncated}\"");
        }

        _vadSw = null;
        _vadEnded = false;
        _vadSpeechSec     = -1f;
        _vadSegments      = -1;
        _vadReductionPct  = -1f;
        _vadInferenceMs   = -1f;
        _vadMappingPoints = -1;
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

        // No-speech path: whisper.cpp short-circuits before emitting the
        // "Reduced audio from" marker when VAD finds 0 segments, so the hook
        // never closes the cycle. Force the close here so the consolidated
        // Verbose line and the "No speech detected" Narrative still surface.
        // _vadSegments stays -1 because "detected N speech segments" is also
        // skipped in the short-circuit — coerce it to 0 so EmitVadSummary
        // takes the no-speech Narrative branch.
        if (_vadSw != null && !_vadEnded)
        {
            _vadEnded = true;
            if (_vadSegments < 0) _vadSegments = 0;
            EmitVadSummary(_vadSw.Elapsed.TotalSeconds);
        }

        nativeAllocs.Free();

        // A non-zero return code paired with an abort request means whisper
        // bailed out on our signal — not a decoder error. Segments emitted
        // before the abort are still usable, so we fall through to the normal
        // text-assembly path below. A non-zero return without an abort request
        // is a real failure — surface it as before.
        if (result != 0 && !_abortRequested)
        {
            _log.Error(
                LogSource.Transcribe,
                $"whisper_full failed | result={result}",
                new UserFeedback(
                    "Transcription failed",
                    "Whisper could not decode the audio.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus("Transcription failed");
            RaiseFinished(TranscriptionOutcome.None);
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

        _log.Success(LogSource.Transcribe, $"Transcription complete ({nSeg} seg)");
        _log.Verbose(LogSource.Transcribe,
            $"complete | whisper_ms={transcribeMsTotal} | n_seg={nSeg} | chars={fullText.Length}");

        // Suppress the post-transcription Narrative when nothing was transcribed
        // — the "No speech detected in the recording." Narrative emitted by the
        // VAD cycle is already the last word for the user, and saying "Whisper
        // transcribed the speech into 0 segments" would be both noisy and silly.
        if (nSeg > 0)
        {
            _log.Narrative(LogSource.Transcribe, $"Whisper transcribed the speech into {nSeg} segments in {transcribeMsTotal / 1000.0:F1} s.");
        }

        if (string.IsNullOrWhiteSpace(fullText))
        {
            RaiseStatus("Ready");
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        // Low-audio warning is emitted live from Record() once 5 s of
        // sustained sub-threshold signal has accumulated — see the tracker in
        // the capture loop. Alerting during recording is the whole point of
        // that message: we want the user to stop talking into a broken mic
        // within seconds, not discover it 20 min later.

        // Always copy raw text first — safety net even if LLM fails. If the
        // copy fails (all three CopyToClipboard error paths already emit a
        // Critical UserFeedback), short-circuit: paste would send Ctrl+V into
        // an empty clipboard, which in most apps pastes whatever was there
        // before the transcription — confusing at best. Better to stop here.
        var swClip = System.Diagnostics.Stopwatch.StartNew();
        bool rawCopyOk = CopyToClipboard(fullText);
        swClip.Stop();
        if (!rawCopyOk)
        {
            RaiseStatus("Ready");
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        long llmMs = 0;
        var llmSettings = Settings.SettingsService.Instance.Current.Llm;
        double recDurationSec = (_recordingSw?.Elapsed.TotalSeconds) ?? 0;
        int rawWordCount = Logging.TextMetrics.CountWords(fullText);

        // Rewrite profile resolution:
        // - manual rewrite hotkey → the profile name passed to StartRecording
        // - plain transcribe hotkey → first matching AutoRewriteRule (duration-based)
        Settings.RewriteProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(_manualProfileName) && llmSettings.Enabled)
        {
            profile = llmSettings.Profiles.Find(p =>
                string.Equals(p.Name, _manualProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                _log.Warning(LogSource.Llm,
                    $"manual profile '{_manualProfileName}' not found in Profiles — transcript pasted without rewriting. Pick an existing profile on the Rewriting page.");
            }
        }
        else if (llmSettings.Enabled)
        {
            // Pivot between the two auto-rule lists. "Words" is the default —
            // word count is a truer proxy for LLM context load than wall-clock
            // duration. "Duration" keeps the legacy behaviour.
            Settings.RewriteProfile? ResolveRuleProfile(string? id, string? name)
            {
                var byId = !string.IsNullOrEmpty(id)
                    ? llmSettings.Profiles.Find(p => p.Id == id)
                    : null;
                return byId ?? llmSettings.Profiles.Find(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            bool byWords = !string.Equals(llmSettings.RuleMetric, "Duration", StringComparison.OrdinalIgnoreCase);
            if (byWords && llmSettings.AutoRewriteRulesByWords.Count > 0)
            {
                foreach (var rule in llmSettings.AutoRewriteRulesByWords
                    .OrderByDescending(r => r.MinWordCount))
                {
                    if (rawWordCount >= rule.MinWordCount)
                    {
                        profile = ResolveRuleProfile(rule.ProfileId, rule.ProfileName);
                        break;
                    }
                }
            }
            else if (!byWords && llmSettings.AutoRewriteRules.Count > 0)
            {
                foreach (var rule in llmSettings.AutoRewriteRules
                    .OrderByDescending(r => r.MinDurationSeconds))
                {
                    if (recDurationSec >= rule.MinDurationSeconds)
                    {
                        profile = ResolveRuleProfile(rule.ProfileId, rule.ProfileName);
                        break;
                    }
                }
            }
        }

        // Preserve the raw text before any rewrite replaces fullText — the
        // corpus logger (fired at the very end) captures only the raw side.
        string rawText = fullText;

        if (profile is not null)
        {
            RaiseStatus($"Rewriting ({profile.Name})…");
            // Narrative only after the call settles — on success we know it
            // landed, on failure we say so explicitly. The previous pre-call
            // "is now rewriting" line lied on failure by implying completion
            // (no counter-narrative was ever emitted). HUD state + polling
            // heartbeat already cover live feedback during the wait.
            var swLlm = System.Diagnostics.Stopwatch.StartNew();
            string? rewritten = _llm.Rewrite(fullText, llmSettings.OllamaEndpoint, profile);
            swLlm.Stop();
            llmMs = swLlm.ElapsedMilliseconds;
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                fullText = rewritten;
                // If the post-rewrite copy fails, the raw transcript from the
                // first copy is still on the clipboard — degrade silently to
                // the raw text instead of making a loud noise about a failure
                // that doesn't hurt the user.
                CopyToClipboard(fullText);
                _log.Narrative(LogSource.Llm, $"Rewrite complete in {swLlm.Elapsed.TotalSeconds:F1} s with the {profile.Name} profile — the polished text is ready to paste.");
            }
            else
            {
                _log.Narrative(LogSource.Llm, $"Rewrite failed after {swLlm.Elapsed.TotalSeconds:F1} s — raw transcript kept. Check the log for the Ollama error.");
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

        // Split recap into two Info lines (timings / outputs) that land under
        // Activity, plus the existing Narrative for the user-facing closing line.
        // The monolithic 200-char Verbose is gone — each line reads cleanly
        // in LogWindow and stays grep-friendly through the standard `k=v` format.
        // Outcome : Pasted on a verified paste delivery, ClipboardOnly when
        // the text made it to the clipboard but paste was disabled or refused
        // (target lost, WhispUI itself, SendInput partial) — the HUD uses
        // this to flash "Copied" or the Ctrl+V reminder before hiding.
        var outcome = (_shouldPaste && pasteVerified) ? TranscriptionOutcome.Pasted
                                                      : TranscriptionOutcome.ClipboardOnly;
        int finalWordCount = Logging.TextMetrics.CountWords(fullText);

        _log.Success(LogSource.Done, $"Done ({outcome})");
        _log.Verbose(LogSource.Done,
            $"timings | audio_sec={recDurationSec:F1} | vad_ms={vadMs} | whisper_ms={whisperMs} | llm_ms={llmMs} | clipboard_ms={swClip.ElapsedMilliseconds} | paste_ms={pasteMs}");
        _log.Verbose(LogSource.Done,
            $"outputs | n_seg={nSeg} | chars={fullText.Length} | words={finalWordCount} | strategy={_strategyLabel} | profile={profile?.Name ?? "(none)"} | outcome={outcome}");
        _log.Narrative(LogSource.Done, $"Done — {recDurationSec:F1} s of dictation processed. Ready for the next.");

        RaiseStatus("Ready");
        _recordingSw?.Stop();

        Logging.TelemetryService.Instance.Latency(new Logging.LatencyPayload(
            AudioSec:    audioSec,
            VadMs:       vadMs,
            WhisperMs:   whisperMs,
            LlmMs:       llmMs,
            ClipboardMs: swClip.ElapsedMilliseconds,
            PasteMs:     pasteMs,
            Strategy:    _strategyLabel,
            NSegments:   nSeg,
            TextChars:   fullText.Length,
            TextWords:   finalWordCount,
            Profile:     profile?.Name ?? "",
            Pasted:      pasteVerified,
            Outcome:     outcome.ToString()));

        // Corpus logging — captures the raw Whisper output only. We don't
        // persist the rewrite: prompts evolve, so paired (raw, rewrite)
        // samples go stale the moment the prompt is edited. Raw text stays
        // useful for benchmarking Whisper itself (initial prompt, VAD, model
        // swap). One file per rewrite profile so samples stay sliceable by
        // the workflow they came from, even without the rewrite payload.
        var telemetrySettings = Settings.SettingsService.Instance.Current.Telemetry;
        if (telemetrySettings.CorpusEnabled && profile is not null)
        {
            var whisperSettings = Settings.SettingsService.Instance.Current.Transcription;
            int rawChars = rawText.Length;
            var timestamp = DateTimeOffset.Now;

            string slug = $"{Logging.CorpusPaths.Slugify(profile.Name)}-{profile.Id}";

            // Audio capture is a second, nested opt-in gated by the same
            // profile slug — so a replay pairs JSONL rows with their WAV
            // 1:1. Same timestamp as the text entry keeps the pairing
            // unambiguous even if the user triggers a new recording
            // while the file write is still settling.
            string? audioFile = telemetrySettings.RecordAudioCorpus
                ? Logging.WavCorpusWriter.Write(slug, audio, timestamp)
                : null;

            var payload = new Logging.CorpusPayload(
                Profile:         profile.Name,
                ProfileId:       profile.Id,
                Slug:            slug,
                DurationSeconds: recDurationSec,
                Whisper:         new Logging.WhisperSection(
                                     whisperSettings.Model,
                                     whisperSettings.Language,
                                     whisperMs,
                                     InitialPrompt: string.IsNullOrEmpty(prompt) ? null : prompt),
                Raw:             new Logging.RawSection(rawText, rawWordCount, rawChars),
                Metrics:         new Logging.CorpusMetricsSection(
                                     WordsPerSecond: recDurationSec > 0 ? rawWordCount / recDurationSec : 0),
                AudioFile:       audioFile);

            Logging.TelemetryService.Instance.Corpus(payload);
        }

        RaiseFinished(outcome);
    }

    // ── Presse-papier ─────────────────────────────────────────────────────────

    // Returns true on a successful copy + verified read-back. False on any of
    // the three fatal branches (GlobalAlloc, OpenClipboard, SetClipboardData) —
    // each surfaces a Critical UserFeedback. Verify-length mismatch only emits
    // a Warning since the bytes reached the clipboard; the length check is a
    // safety net against clipboard-format mangling by a third-party watcher.
    private bool CopyToClipboard(string text)
    {
        const uint GMEM_MOVEABLE  = 0x0002;
        const uint CF_UNICODETEXT = 13;

        int byteCount = (text.Length + 1) * 2;

        IntPtr hMem = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        _log.Verbose(LogSource.Clipboard, $"GlobalAlloc | bytes={byteCount} | hMem={hMem}");
        if (hMem == IntPtr.Zero)
        {
            _log.Error(
                LogSource.Clipboard,
                $"GlobalAlloc failed | bytes={byteCount}",
                new UserFeedback(
                    "Clipboard copy failed",
                    "Memory allocation failed. Try again.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            return false;
        }

        IntPtr ptr = NativeMethods.GlobalLock(hMem);
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0);
        NativeMethods.GlobalUnlock(hMem);

        bool opened = NativeMethods.OpenClipboard(IntPtr.Zero);
        _log.Verbose(LogSource.Clipboard, $"OpenClipboard | ok={opened}");
        if (!opened)
        {
            _log.Error(
                LogSource.Clipboard,
                "OpenClipboard failed",
                new UserFeedback(
                    "Clipboard unavailable",
                    "Another app is holding it. Try again.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            return false;
        }

        NativeMethods.EmptyClipboard();
        IntPtr setHandle = NativeMethods.SetClipboardData(CF_UNICODETEXT, hMem);
        NativeMethods.CloseClipboard();
        if (setHandle == IntPtr.Zero)
        {
            _log.Error(
                LogSource.Clipboard,
                "SetClipboardData failed | handle=0",
                new UserFeedback(
                    "Clipboard copy failed",
                    "Windows refused the write. Try again.",
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            return false;
        }

        // Immediate read-back to verify the clipboard was set correctly.
        // Mismatch is a Warning — the copy reached the OS, a third-party
        // clipboard watcher may have re-encoded or trimmed the payload
        // between SetClipboardData and our read.
        if (NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            IntPtr h = NativeMethods.GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero)
            {
                _log.Warning(
                    LogSource.Clipboard,
                    "verify failed | reason=no_unicode_data",
                    new UserFeedback(
                        "Clipboard may be incomplete",
                        "Text is copied but unverified.",
                        UserFeedbackSeverity.Warning,
                        UserFeedbackRole.Overlay));
            }
            else
            {
                IntPtr p = NativeMethods.GlobalLock(h);
                string? back = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                NativeMethods.GlobalUnlock(h);
                if (back is null || back.Length != text.Length)
                {
                    _log.Warning(
                        LogSource.Clipboard,
                        $"verify failed | expected_chars={text.Length} | actual_chars={back?.Length ?? -1}",
                        new UserFeedback(
                            "Clipboard may be incomplete",
                            "Length mismatch on read-back.",
                            UserFeedbackSeverity.Warning,
                            UserFeedbackRole.Overlay));
                }
            }
            NativeMethods.CloseClipboard();
        }

        _log.Info(LogSource.Clipboard, "Copied to clipboard");
        _log.Narrative(LogSource.Clipboard, $"The transcription is now on the clipboard — {text.Length} characters ready to paste anywhere.");
        _log.Verbose(LogSource.Clipboard, $"copy complete | chars={text.Length} | bytes={byteCount}");
        return true;
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

        _log.Info(LogSource.Paste, "Pasted");
        _log.Verbose(LogSource.Paste, $"Ctrl+V sent to {Win32Util.DescribeHwnd(fg)}");
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
