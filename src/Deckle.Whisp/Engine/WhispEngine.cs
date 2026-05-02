using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Deckle.Capture;
using Deckle.Capture.Telemetry;
using Deckle.Interop;
using Deckle.Llm;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Whisp.Pinvoke;
using Deckle.Whisp.Setup;

namespace Deckle.Whisp;

// Result of a pipeline pass, consumed by the HUD post-paste handler.
//   None            — nothing to show (empty audio, empty text, error).
//   Pasted          — UIA confirmed a text field and Ctrl+V was delivered;
//                     HUD flashes "Pasted" in green.
//   ClipboardOnly   — text is on the clipboard but paste was skipped (UIA
//                     couldn't confirm, foreground was Deckle, SendInput
//                     partial…); HUD shows the Ctrl+V reminder for a few
//                     seconds. This is the safe default when in doubt.
public enum TranscriptionOutcome { None, Pasted, ClipboardOnly }

// Pipeline state — single source of truth for the recording lifecycle.
// Manipulated exclusively via Interlocked.CompareExchange on _state. Each
// transition is protected by a CAS so that rapid double-presses on the
// hotkey, or a Stop racing the cap-duration internal stop, all rebound
// cleanly instead of double-spawning a worker thread or double-calling
// whisper_full on the same context (whisper.cpp is not thread-safe across
// concurrent calls on the same context — segfault, not a managed exception).
//
// Legal transitions:
//   Idle         → Starting     (hotkey, RequestToggle entry)
//   Starting     → Recording    (mic probe ok, worker about to spawn)
//   Starting     → Idle         (probe fail / warmup gate / disposed — rollback)
//   Recording    → Stopping     (hotkey Stop OR cap duration hit)
//   Stopping     → Transcribing (worker, after Record() returns)
//   Transcribing → Idle         (worker finally, after Transcribe() exits)
//   *            → Disposed     (Dispose, terminal)
//
// See WhispEngine.RequestToggle, TryStartFromIdle, and WorkerRun for the
// actual CAS sites. The cap-duration branch is now handled inside
// MicrophoneCapture / WaveInLoop — WorkerRun observes CaptureOutcome.CapHit
// on return and runs the Recording → Stopping CAS itself.
public enum PipelineState { Idle, Starting, Recording, Stopping, Transcribing, Disposed }

// Outcome of a hotkey toggle request — returned to App.OnHotkey so the
// caller can drive HUD/log without ever reading engine state directly
// (which is what caused the original double-press race).
public enum ToggleResult
{
    Started,            // CAS Idle → Starting succeeded, worker spawned.
    Stopped,            // CAS Recording → Stopping succeeded, worker draining.
    IgnoredBusy,        // State was Starting/Stopping/Transcribing — silent no-op.
    IgnoredNoProfile,   // Rewrite hotkey with no profile bound, called from Idle.
    IgnoredDisposed,    // Engine in shutdown — silent no-op.
}

// ─── Transcription engine ─────────────────────────────────────────────────────
//
// Ported from WhispInteropTest (WhispForm) into a standalone class.
// UI-framework independent — communicates via events.
// Events may fire from background threads: subscribers are responsible
// for marshaling to the UI thread.

public sealed class WhispEngine : IDisposable
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
    // mutation from Deckle occurs while SendInput is in flight to the target.
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
    // instead of peppering the pipeline with if-checks.
    //
    // ThreadStatic — the suppression is scoped to the *invocation* of
    // Transcribe() that owns the warmup, not to the engine instance. Warmup
    // sets the flag on its own thread before calling Transcribe and clears
    // it after; the user-driven Worker thread reads its own (false) copy and
    // is unaffected. A shared instance flag would let the Worker observe
    // Warmup's `true` for the slice between Warmup releasing
    // _transcribeLock and reaching its `finally` reset, silencing the user's
    // narratives mid-call. Whisper.cpp invokes its segment / abort / log
    // callbacks synchronously on the thread that called whisper_full, so
    // ThreadStatic is the right scope for them too.
    [ThreadStatic] private static bool t_isWarmup;

    private void RaiseStatus(string status)
    {
        if (t_isWarmup) return;
        StatusChanged?.Invoke(status);
    }

    private void RaiseFinished(TranscriptionOutcome outcome)
    {
        if (t_isWarmup) return;
        TranscriptionFinished?.Invoke(outcome);
    }

    // Gate user-facing narratives the same way as RaiseStatus/RaiseFinished so
    // the boot-time warmup doesn't pollute LogWindow with phrases describing a
    // real transcription. The two warmup-specific narratives (priming start
    // and pipeline-ready end) are emitted directly through _log.Narrative so
    // they bypass this gate. `source` is a LogSource constant (string under
    // the hood — LogSource is a static class of named string constants, not
    // an enum).
    private void RaiseNarrative(string source, string msg)
    {
        if (t_isWarmup) return;
        _log.Narrative(source, msg);
    }

    // ── Internal state ───────────────────────────────────────────────────────

    private static readonly LogService _log = LogService.Instance;

    private readonly string     _modelPath;
    private readonly LlmService _llm;

    // volatile: prevents the compiler from caching these values in CPU registers.
    // Without volatile, a background thread could read a stale value.
    private volatile IntPtr _ctx           = IntPtr.Zero;
    private volatile bool   _shouldPaste   = false;

    // Pipeline state — single source of truth, manipulated only via
    // Interlocked.CompareExchange on _state. Backed by int because Interlocked
    // doesn't operate on enums directly — cast to/from PipelineState at every
    // read/write site. See the PipelineState enum above for the legal
    // transitions and the rationale.
    //
    // Invariant: every public entry point (RequestToggle, Dispose, UnloadModel)
    // reads _state exactly once via Volatile.Read, then either CAS-transitions
    // it or rebounds with a no-op. The worker thread owns the
    // Stopping → Transcribing → Idle transitions; no other thread may write
    // those.
    private int _state = (int)PipelineState.Idle;

    // Cancellation channel for the active capture session. Recreated at the
    // top of each WorkerRun (Idle → Recording transition), cancelled by the
    // Stop path (RequestToggle after CAS Recording → Stopping) and by Dispose.
    // Disposed inside WorkerRun's finally once the capture call has returned.
    //
    // The cap-duration branch is now owned by MicrophoneCapture / WaveInLoop
    // — the orchestrator observes it via CaptureResult.Outcome on return.
    // No second channel needed.
    private CancellationTokenSource? _recordCts;

    // Signaled when the engine returns to Idle (worker exits + state reset).
    // Initialised "set" because no recording is in flight at construction time.
    // Reset on Idle → Starting, set on the worker's terminal Idle transition
    // (the same finally that emits "Ready"). Used by Dispose to await the
    // running pipeline within a bounded timeout — never read from the hotkey
    // path, which relies on the CAS itself for re-entry refusal.
    private readonly ManualResetEventSlim _idleEvent = new(initialState: true);

    // Reference to the live worker thread (Record + Transcribe). Held only
    // for Dispose to call Join with a timeout — no other consumer reads this.
    // null when Idle.
    private Thread? _worker;

    // Cancellation channel for the boot warmup. Created by Warmup() when its
    // background thread enters, nulled+disposed in finally before the thread
    // exits. RequestToggle and Dispose call TrySignalWarmupCancel() to
    // unblock a hotkey or quit pressed while warmup is in flight — see the
    // Warmup doc-block for the rationale. Plain field, no `volatile`:
    // writers see each other through the lifecycle (Warmup creates →
    // toggle/Dispose cancels → Warmup nulls), and the helper snapshots the
    // field locally to avoid TOCTOU between the null-check and Cancel().
    private CancellationTokenSource? _warmupCts;

    // Best-effort cancel of an in-flight warmup. No-op if no warmup is
    // running. Catches ObjectDisposedException because the Warmup thread's
    // finally may have disposed the CTS in the small window between our
    // local snapshot and the Cancel() call.
    private void TrySignalWarmupCancel()
    {
        var cts = _warmupCts;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // Serialises whisper_full calls on the same _ctx. The state machine
    // already prevents concurrent transcriptions through the user-driven
    // pipeline (only one Idle → Recording → Transcribing chain at a time),
    // but Warmup() also calls Transcribe() at startup on a separate thread,
    // and a hotkey arriving during the warmup window would race the warmup
    // whisper_full on the same context. whisper.cpp is not thread-safe across
    // concurrent calls on a single context — the result is a native segfault
    // that no managed handler can rescue. This lock makes the invariant local
    // to the call site.
    private readonly object _transcribeLock = new();

    // Name of the rewrite profile chosen by the hotkey that started this
    // recording (null = no manual rewrite; fall back to AutoRewriteRules
    // based on recording duration). Captured at StartRecording time and
    // consumed at the end of Transcribe().
    private string?         _manualProfileName = null;

    // Model lifecycle: lazy load on first hotkey, unload after idle timeout.
    // The "pipeline running, don't unload" guard now reads _state directly
    // (anything other than Idle / Disposed means a pipeline is in flight),
    // removing the previous _pipelineActive bool that duplicated the state
    // machine and could drift out of sync with it.
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
    private WhisperPInvoke.WhisperLogCallback? _whisperLogCallback;

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

    // Latency instrumentation — extra stage timers feeding LatencyPayload.
    // Reset at the entry of StartRecording so each run reports its own values.
    //   _modelLoadMs        — captured by LoadModel; 0 when warm.
    //   _hotkeySw           — entry of StartRecording → just after waveInStart.
    //                         On a cold run includes _modelLoadMs (load runs on
    //                         the worker thread before the mic opens), plus
    //                         the mic probe and worker thread spin-up.
    //   _recordDrainDuration — CT cancels → end of CaptureResult drain phase.
    //                         Subset of _stopToPipelineSw — shows how much of
    //                         the stop-to-pipeline cost is spent in the mic
    //                         drain alone. Captured from CaptureResult on
    //                         Record() return; the Capture module owns the
    //                         underlying stopwatch.
    //   _stopToPipelineSw   — entry of RequestToggle (Stop branch) → first whisper_vad log.
    //                         Captures Record() drain + Transcribe() entry.
    //   _whisperInitSw      — just before whisper_full() → first whisper_vad log.
    //                         Pre-VAD overhead inside whisper_full (context
    //                         init, mel allocation, GPU upload).
    // The latter two stop in the same hook branch that starts _vadSw.
    private long _modelLoadMs;
    private System.Diagnostics.Stopwatch? _hotkeySw;
    // Drain duration of the post-Stop waveIn drain phase. Captured from
    // CaptureResult.DrainDuration on Record() return — the legacy
    // _recordDrainSw stopwatch lived inside the Record() body, this field
    // now persists the value across the WorkerRun for the LatencyPayload
    // builder to pick up.
    private System.TimeSpan _recordDrainDuration;
    private System.Diagnostics.Stopwatch? _stopToPipelineSw;
    private System.Diagnostics.Stopwatch? _whisperInitSw;

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

    public bool IsReady => _ctx != IntPtr.Zero;

    // True whenever the pipeline is in any non-Idle, non-Disposed state.
    // Read by callers that want a coarse "is something going on?" signal —
    // the hotkey path does NOT consume this (it goes through RequestToggle
    // which CAS's the transition atomically; reading and then deciding is
    // exactly the racy pattern this passe was designed to remove).
    public bool IsBusy
    {
        get
        {
            var s = (PipelineState)Volatile.Read(ref _state);
            return s != PipelineState.Idle && s != PipelineState.Disposed;
        }
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    private readonly IWhispEngineHost _host;

    // Microphone capture service — extracted from the legacy 331-line Record()
    // body. The engine owns the orchestration (state machine, narratives,
    // auto-calibration enveloppe) and consumes Capture as a black-box PCM
    // producer. Future modules (Ask-Ollama) will share the same instance
    // type without dragging Whisp's transcription dependencies along.
    private readonly MicrophoneCapture _capture;

    // Adapter that exposes IWhispEngineHost as IRecordingHost — keeps
    // IWhispEngineHost free of an IRecordingHost inheritance (Ask-Ollama
    // would not have wanted that coupling).
    private readonly RecordingHostAdapter _recordingHost;

    public WhispEngine(IWhispEngineHost host)
    {
        _host = host;
        _modelPath = ResolveModelPath();

        _llm = new LlmService();

        _capture = new MicrophoneCapture(_log);
        _recordingHost = new RecordingHostAdapter(_host);
        // Forward the per-sub-window RMS to whoever subscribes to the engine
        // (HUD chrono today). Capture stays unaware of UI consumers.
        _capture.AudioLevel += rms => AudioLevel?.Invoke(rms);
        // Close the hotkey-to-capture latency stopwatch the moment waveInStart
        // confirms the mic is live. The legacy code did this directly inside
        // Record(); the event keeps the orchestrator in charge of its own
        // stopwatches.
        _capture.CaptureStarted += () => _hotkeySw?.Stop();
        // Surface the localized low-audio overlay when the live tracker
        // detects no sustained healthy voice in the first 5 s. Capture only
        // emits a technical log; the localized UserFeedback is built here.
        _capture.LowAudioDetected += OnCaptureLowAudioDetected;

        // Hook the global whisper.cpp log callback before any model load to
        // catch Vulkan/CUDA initialization logs and model parsing warnings.
        // Install-once, process-wide.
        InstallWhisperLogHook();

        // Model loaded on-demand at first hotkey press (see EnsureModelLoaded).
        // Unloaded after MODEL_IDLE_TIMEOUT_MS of inactivity to free VRAM.
    }

    // Localized UserFeedback for the low-audio condition. Capture emits a
    // bare `LowAudioDetected` event so it stays free of any Loc.Get
    // dependency; the engine owns the localization (Engine_LowAudio_Title /
    // Body) and the UserFeedback role (Overlay — capture continues).
    private void OnCaptureLowAudioDetected()
    {
        _log.Warning(
            LogSource.Capture,
            "low audio overlay surfaced",
            new UserFeedback(
                Loc.Get("Engine_LowAudio_Title"),
                Loc.Get("Engine_LowAudio_Body"),
                UserFeedbackSeverity.Warning,
                UserFeedbackRole.Overlay));
    }

    // Adapter that maps IWhispEngineHost → IRecordingHost. Lives inside
    // WhispEngine so the engine project owns the coupling rather than
    // requiring IWhispEngineHost to inherit from IRecordingHost (which would
    // bleed Capture's contract into every host that wants to drive Whisp).
    private sealed class RecordingHostAdapter : IRecordingHost
    {
        private readonly IWhispEngineHost _h;
        public RecordingHostAdapter(IWhispEngineHost h) { _h = h; }
        public int  AudioInputDeviceId         => _h.Capture.AudioInputDeviceId;
        public int  MaxRecordingDurationSeconds => _h.Capture.MaxRecordingDurationSeconds;
        public bool MicrophoneTelemetryEnabled  => _h.Telemetry.MicrophoneTelemetry;
    }

    // Resolves the model path. Order of precedence:
    //   1. DECKLE_MODEL_PATH environment variable, only if it points to an
    //      absolute path that exists on disk. Anything else is rejected
    //      with a warning so the user notices the misconfiguration instead
    //      of silently falling back. Validation is cheap and avoids handing
    //      a relative or non-existent path to the native whisper layer.
    //   2. The path resolved from SettingsService (configured models dir +
    //      the model filename selected by the user, falling back to the
    //      Setup catalog default when the setting is unset).
    private string ResolveModelPath()
    {
        var transcription = _host.Whisp.Transcription;
        string modelFile = string.IsNullOrWhiteSpace(transcription.Model)
            ? SpeechModels.DefaultModelFileName
            : transcription.Model;

        string fallback = Path.Combine(
            _host.ResolveModelsDirectory(), modelFile);

        string? envPath = Environment.GetEnvironmentVariable("DECKLE_MODEL_PATH");
        if (string.IsNullOrWhiteSpace(envPath))
            return fallback;

        if (!Path.IsPathRooted(envPath) || !File.Exists(envPath))
        {
            _log.Warning(LogSource.Engine,
                $"DECKLE_MODEL_PATH ignored (not an existing absolute path): \"{envPath}\". " +
                $"Falling back to \"{fallback}\".");
            return fallback;
        }

        return envPath;
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
                        // Close two upstream timers on the same first VAD line:
                        //   _stopToPipelineSw bracketed StopRecording → here.
                        //   _whisperInitSw    bracketed whisper_full() → here.
                        // Both share the same anchor on purpose — the VAD line
                        // is the first observable signal that whisper.cpp has
                        // moved past its setup phase into actual work.
                        _stopToPipelineSw?.Stop();
                        _whisperInitSw?.Stop();
                        RaiseNarrative(LogSource.Transcribe, "Looking for speech in the recording — a small detector is scanning the audio for spoken segments.");
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
            WhisperPInvoke.whisper_log_set(_whisperLogCallback, IntPtr.Zero);
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
            RaiseNarrative(LogSource.Transcribe, "No speech detected in the recording.");
        }
        else if (_vadSpeechSec >= 0)
        {
            RaiseNarrative(LogSource.Transcribe, $"Speech detected — {_vadSpeechSec:F1} s of speech. Passing to Whisper for transcription.");
        }
        else
        {
            RaiseNarrative(LogSource.Transcribe, "Speech detected. Passing to Whisper for transcription.");
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
        RaiseStatus(Loc.Get("Status_LoadingModel"));

        if (!File.Exists(_modelPath))
        {
            _log.Warning(
                LogSource.Model,
                $"load aborted | reason=file_not_found | path={_modelPath}",
                new UserFeedback(
                    Loc.Get("Engine_WhisperModelNotFound_Title"),
                    Loc.Get("Engine_WhisperModelNotFound_Body"),
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus(Loc.Get("Status_Ready"));
            return false;
        }

        double fileMb = new FileInfo(_modelPath).Length / 1024.0 / 1024.0;
        string basename = Path.GetFileName(_modelPath);
        _log.Info(LogSource.Model, "Loading model");
        RaiseNarrative(LogSource.Model, $"Loading the Whisper model into GPU memory — a {fileMb:F0} MB speech recognizer is being prepared so transcription can run locally.");
        _log.Verbose(LogSource.Model, $"load start | file={basename} | file_mb={fileMb:F1} | use_gpu=1");

        // Reset the backend before init so a re-load after an idle unload
        // picks up the current backend rather than the one detected at the
        // first startup. The log hook fires synchronously during init and
        // overwrites this field as soon as it sees a ggml_vulkan:/cuda/metal
        // line; if none appear, CPU is the correct fallback.
        _detectedBackend = "CPU";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr ctxParamsPtr = WhisperPInvoke.whisper_context_default_params_by_ref();
        WhisperContextParams ctxParams = Marshal.PtrToStructure<WhisperContextParams>(ctxParamsPtr);
        WhisperPInvoke.whisper_free_context_params(ctxParamsPtr);
        ctxParams.use_gpu = 1;

        _ctx = WhisperPInvoke.whisper_init_from_file_with_params(_modelPath, ctxParams);
        sw.Stop();
        _log.Verbose(LogSource.Engine, $"whisper_init_from_file returned ctx={_ctx}");

        if (_ctx == IntPtr.Zero)
        {
            _log.Error(
                LogSource.Init,
                $"load failed | path={_modelPath}",
                new UserFeedback(
                    Loc.Get("Engine_ModelLoadFailed_Title"),
                    Loc.Get("Engine_ModelLoadFailed_Body"),
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus(Loc.Get("Status_Ready"));
            return false;
        }

        // Capture for the next LatencyPayload — read once by Transcribe(), reset
        // by the next StartRecording. A non-zero value here means the run paid
        // the cold-load cost; warm runs report 0.
        _modelLoadMs = sw.ElapsedMilliseconds;

        _log.Success(LogSource.Model, $"Model loaded ({_detectedBackend})");
        _log.Verbose(LogSource.Model, $"load complete | load_ms={sw.ElapsedMilliseconds} | backend={_detectedBackend}");

        // Mirror the symmetric "Ready" emitted on the failure paths above so
        // the tray tooltip transitions Loading model… → Ready as soon as the
        // model is in VRAM. Without this, the success path returns silently
        // and the tooltip stays stuck on "Loading model…" through warmup
        // (Transcribe's own RaiseStatus(Loc.Get("Status_Ready")) is absorbed by t_isWarmup)
        // and only flips on the first user hotkey.
        RaiseStatus(Loc.Get("Status_Ready"));
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
    /// Skipped if a pipeline (Record+Transcribe) is currently active —
    /// reads _state directly (the previous _pipelineActive bool was a
    /// duplicate of the state machine and is gone). Also skipped if the
    /// engine is being disposed; Dispose itself frees the context.
    /// </summary>
    private void UnloadModel()
    {
        lock (_modelLock)
        {
            var state = (PipelineState)Volatile.Read(ref _state);
            if (state != PipelineState.Idle)
            {
                _log.Verbose(LogSource.Model, $"idle unload skipped | state={state}");
                return;
            }
            if (_ctx == IntPtr.Zero) return;

            WhisperPInvoke.whisper_free(_ctx);
            _ctx = IntPtr.Zero;
            _log.Success(LogSource.Model, "Model unloaded");
            _log.Verbose(LogSource.Model, $"model unloaded | idle_s={MODEL_IDLE_TIMEOUT_MS / 1000} | state=vram-freed");
            // Re-check state right before RaiseStatus — a hotkey could have
            // landed during whisper_free (rare, since unload only runs after
            // the idle timer fires from Idle). If state has moved, defer to
            // the worker's finally to emit "Ready" at the right moment.
            if ((PipelineState)Volatile.Read(ref _state) == PipelineState.Idle)
            {
                RaiseStatus(Loc.Get("Status_Ready"));
            }
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

    // ── Warmup clip loader ──────────────────────────────────────────────────
    //
    // Reads Assets/Sounds/speech.wav (deployed next to the exe via the
    // Content directive in Deckle.csproj) and converts the PCM mono 16-bit
    // 16 kHz body into the float[-1, 1] sample buffer Whisper expects.
    // Strict format check — the file is shipped pre-converted, anything
    // unexpected returns null so Warmup falls back to a silent buffer
    // instead of crashing the boot path.
    //
    // Header layout reference (canonical 44-byte PCM WAV):
    //   00..03  "RIFF"
    //   04..07  RIFF size (file - 8)
    //   08..11  "WAVE"
    //   12..15  "fmt "
    //   16..19  fmt chunk size (16 for plain PCM)
    //   20..21  audio format (1 = PCM)
    //   22..23  num channels
    //   24..27  sample rate
    //   28..31  byte rate
    //   32..33  block align
    //   34..35  bits per sample
    //   36..39  "data"
    //   40..43  data chunk size
    //   44..    int16 little-endian samples
    private float[]? TryLoadWarmupClip()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "speech.wav");
        try
        {
            if (!File.Exists(path))
            {
                _log.Verbose(LogSource.Init, $"warmup clip missing | path={path}");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44
                || bytes[0]  != 'R' || bytes[1]  != 'I' || bytes[2]  != 'F' || bytes[3]  != 'F'
                || bytes[8]  != 'W' || bytes[9]  != 'A' || bytes[10] != 'V' || bytes[11] != 'E'
                || bytes[12] != 'f' || bytes[13] != 'm' || bytes[14] != 't' || bytes[15] != ' '
                || bytes[36] != 'd' || bytes[37] != 'a' || bytes[38] != 't' || bytes[39] != 'a')
            {
                _log.Warning(LogSource.Init, $"warmup clip header invalid | path={path}");
                return null;
            }

            int audioFormat   = BitConverter.ToInt16(bytes, 20);
            int numChannels   = BitConverter.ToInt16(bytes, 22);
            int sampleRate    = BitConverter.ToInt32(bytes, 24);
            int bitsPerSample = BitConverter.ToInt16(bytes, 34);
            int dataSize      = BitConverter.ToInt32(bytes, 40);

            if (audioFormat != 1 || numChannels != 1 || sampleRate != 16000 || bitsPerSample != 16)
            {
                _log.Warning(LogSource.Init,
                    $"warmup clip format unexpected | format={audioFormat} ch={numChannels} sr={sampleRate} bits={bitsPerSample} (expected PCM mono 16-bit 16 kHz)");
                return null;
            }

            int sampleCount = dataSize / 2;
            float[] samples = new float[sampleCount];
            int offset = 44;
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(bytes, offset);
                samples[i] = s / 32768f;
                offset += 2;
            }
            return samples;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Init, $"warmup clip load failed | error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── Warmup ──────────────────────────────────────────────────────────────
    //
    // Runs a real "first inference" at startup so the user's first hotkey
    // press doesn't pay the cold cost (context alloc + GPU warm + Vulkan
    // pipeline compile + weight paging). We push a short embedded reference
    // clip (Assets/Sounds/speech.wav, PCM mono 16 kHz ~2 s) through the full
    // Transcribe() path so VAD finds speech, whisper_full actually decodes,
    // and the GPU pipelines are compiled once and for all. Roughly 200–800 ms
    // on RX 7900 XT with Vulkan ggml — paid here instead of on the user's
    // first dictation.
    //
    // StatusChanged / TranscriptionFinished / Narrative are gated during
    // Transcribe() via t_isWarmup (RaiseStatus / RaiseFinished /
    // RaiseNarrative) so the HUD never appears, the tray doesn't flash, and
    // LogWindow doesn't surface "Looking for speech…" / "Speech detected —
    // 2.4 s…" / "Whisper transcribed…" phrases that would confuse the user
    // at boot. Two warmup-specific narratives are emitted directly — one at
    // the start ("Priming the recognizer…") and one at the end ("Pipeline
    // ready"). LoadModel's narrative stays audible because it runs before
    // t_isWarmup flips.
    //
    // Cancellable. RequestToggle and Dispose call Cancel() on _warmupCts to
    // unblock the user — a hotkey pressed during warmup must not wait for
    // the warmup's whisper_full to finish before the recording can start.
    // The abort_callback observes the token mid-decoder, so cancellation
    // surfaces in ~50 ms rather than the worst-case ~800 ms.
    //
    // Fire-and-forget on a background thread — the call site in
    // App.OnLaunched must not block UI-thread startup. Named Warmup (not
    // WarmupAsync) because the method returns void: the *Async suffix in
    // C# is reserved for methods returning Task / ValueTask.
    public void Warmup()
    {
        if (_disposed) return;
        var thread = new Thread(() =>
        {
            // Re-check : Dispose peut survenir entre le Thread.Start et
            // le démarrage effectif du thread (rare mais possible si
            // l'utilisateur quitte très tôt après le boot).
            if (_disposed) return;

            // CTS lifetime is bounded by this thread. We assign to the field
            // so RequestToggle / Dispose can signal cancellation, and clear
            // it in finally so post-warmup callers see "no warmup in flight".
            var cts = new CancellationTokenSource();
            _warmupCts = cts;
            var ct = cts.Token;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _log.Info(LogSource.Init, "Warmup start");

                // 1) Mic probe — same code path StartRecording uses, just the
                //    probe result is stored instead of blocking the recording.
                _micWarmupOk = _capture.Probe(_host.Capture.AudioInputDeviceId).Ok ? 1 : 0;

                if (ct.IsCancellationRequested)
                {
                    _log.Info(LogSource.Init,
                        $"Warmup cancelled before model load | total_ms={sw.ElapsedMilliseconds}");
                    return;
                }

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

                if (ct.IsCancellationRequested)
                {
                    _log.Info(LogSource.Init,
                        $"Warmup cancelled before transcribe | total_ms={sw.ElapsedMilliseconds}");
                    return;
                }

                // Real-audio Transcribe through the full pipeline (VAD +
                // whisper_full + Vulkan kernel compile) to pay the first-
                // inference cost before any user hotkey. The clip is shipped
                // alongside the exe under Assets/Sounds/speech.wav (PCM mono
                // 16-bit 16 kHz). On load failure we fall back to a 1.6 s
                // silent buffer — the user-visible narratives are gated
                // either way, so the fallback is invisible beyond the warmup
                // log line. Length-mismatch scenarios (corrupted file, wrong
                // format) are rare but should not block startup.
                float[] warmupBuffer = TryLoadWarmupClip()
                    ?? new float[25_600];

                _log.Narrative(LogSource.Init,
                    "Priming the recognizer with a short reference clip — the audio pipeline is being warmed up so your first dictation feels instant.");

                t_isWarmup = true;
                try
                {
                    Transcribe(warmupBuffer, ct);
                }
                finally
                {
                    t_isWarmup = false;
                }

                if (ct.IsCancellationRequested)
                {
                    _log.Info(LogSource.Init,
                        $"Warmup cancelled during transcribe | total_ms={sw.ElapsedMilliseconds}");
                    return;
                }

                _log.Narrative(LogSource.Init, "Pipeline ready.");

                // 3) Ollama health-check. Skipped (and left as OK) when the LLM
                //    feature is disabled — no rewriter needed, no warning to
                //    surface. 3 s timeout par tentative dans IsAvailableAsync
                //    × 3 essais espacés de 500 ms — couvre la race classique
                //    au boot PC où Deckle démarre avant qu'Ollama ait fini
                //    d'écouter sur 11434. Pire cas borné à ~10 s.
                var llmSettings = _host.Llm;
                if (llmSettings.Enabled)
                {
                    try
                    {
                        var ollama = new Llm.OllamaService(
                            () => _host.Llm.OllamaEndpoint);
                        bool reachable = ollama.IsAvailableAsync(maxAttempts: 3).GetAwaiter().GetResult();
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
                _log.Error(LogSource.Init, $"Warmup failed | error={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _warmupCts = null;
                cts.Dispose();
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    // ── Hotkey toggle entry point ───────────────────────────────────────────
    //
    // All hotkey-driven Start/Stop traffic flows through here. The earlier
    // pattern read engine state from App and branched into StartRecording or
    // StopRecording — that read-then-branch was the original double-press
    // race (App.OnHotkey reading IsRecording, then calling Start, while a
    // second press arrived between the read and the call). The current
    // contract: the engine atomically CAS-transitions the state machine and
    // returns the result; the caller only switches on the outcome to drive
    // HUD and logs, never to decide what to do.
    //
    // requireProfile: passed by App for rewrite hotkeys. When a press lands
    // in Idle but the rewrite slot has no profile bound, refuse with
    // IgnoredNoProfile so the press doesn't start an empty rewrite session.
    // Pressed during Recording, requireProfile is silent — the press is a
    // valid Stop irrespective of profile binding.
    public ToggleResult RequestToggle(string? manualProfileName, bool shouldPaste, bool requireProfile)
    {
        // _disposed is set before Dispose CAS's the state to Disposed, so it
        // catches the moment between "Quit clicked" and "Dispose finished".
        // Either guard reaching this branch means the engine is shutting
        // down — silent no-op.
        if (_disposed) return ToggleResult.IgnoredDisposed;

        // Unblock a warmup in flight. The user's hotkey wins over the
        // best-effort priming work — abort_callback observes the token and
        // whisper_full bails within ~50 ms, releasing _transcribeLock so the
        // worker (spawned a few lines below) can enter Transcribe without
        // waiting out the warmup's residual decode time.
        TrySignalWarmupCancel();

        var current = (PipelineState)Volatile.Read(ref _state);
        if (current == PipelineState.Disposed) return ToggleResult.IgnoredDisposed;

        if (current == PipelineState.Recording)
        {
            // Try to claim the Recording → Stopping transition. If we lose
            // the CAS, another thread already moved the state out of
            // Recording (cap duration hitting at the same instant, a race
            // with another Stop press). Treat as busy; no second action.
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)PipelineState.Stopping,
                    (int)PipelineState.Recording)
                != (int)PipelineState.Recording)
            {
                return ToggleResult.IgnoredBusy;
            }

            // Stop-to-pipeline latency starts here — closed by the
            // whisper.cpp log hook on the first whisper_vad line. The CAS
            // above guarantees this runs at most once per recording cycle,
            // so we don't need the previous "if (_isRecording)" guard.
            _stopToPipelineSw = System.Diagnostics.Stopwatch.StartNew();

            // Signal the capture polling loop to drain and return. The CTS
            // is created at the top of WorkerRun and disposed at its
            // finally; Cancel() is safe even if Record() has already
            // returned (the token is then unobserved).
            try { _recordCts?.Cancel(); }
            catch (ObjectDisposedException) { /* worker raced our cancel */ }
            return ToggleResult.Stopped;
        }

        if (current != PipelineState.Idle)
        {
            // Starting / Stopping / Transcribing — the previous pipeline is
            // still in flight. Silent no-op, only a Verbose telemetry line
            // for diagnosis when the user reports "I pressed but nothing
            // happened". Decision: ignore (Settings Win11 voice-typing
            // semantics). See plan
            // C:\Users\Louis\.claude\plans\j-ai-un-petit-probl-me-quiet-pebble.md
            _log.Verbose(LogSource.Hotkey, $"toggle ignored | state={current}");
            return ToggleResult.IgnoredBusy;
        }

        if (requireProfile && string.IsNullOrWhiteSpace(manualProfileName))
        {
            // Rewrite hotkey from Idle without a profile bound — same
            // semantics as before the refactor: warn and refuse. The press
            // does NOT take the Idle → Starting CAS, so a subsequent
            // transcribe-hotkey press will still start cleanly.
            return ToggleResult.IgnoredNoProfile;
        }

        return TryStartFromIdle(manualProfileName, shouldPaste);
    }

    // Idle → Starting → Recording. Called only when RequestToggle has
    // verified the engine is in Idle and (for rewrite hotkeys) the profile
    // is bound. CAS Idle → Starting up front so a second hotkey press
    // arriving inside the warmup gate or the mic probe rebounds immediately.
    // The entire Starting window is mutually exclusive with any other Start
    // attempt, even one that fires while MicrophoneCapture.Probe() is
    // blocked on the Win32 audio device (~1-2 ms typical, but can spike on
    // contended hardware).
    //
    // CRITICAL: every early-return path below MUST roll the state back to
    // Idle and signal _idleEvent, otherwise the engine permanently locks
    // out future hotkeys. The try/finally with `committed` ensures this.
    private ToggleResult TryStartFromIdle(string? manualProfileName, bool shouldPaste)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)PipelineState.Starting,
                (int)PipelineState.Idle)
            != (int)PipelineState.Idle)
        {
            // Lost the CAS — another thread (Dispose, parallel hotkey)
            // moved the state out of Idle in the small window since
            // RequestToggle's snapshot read.
            return ToggleResult.IgnoredBusy;
        }

        // From here we own the Idle → Starting → (Recording or Idle) edge.
        // _idleEvent is reset until either RollbackToIdle below, or the
        // worker's terminal Idle transition.
        _idleEvent.Reset();

        bool committed = false;
        try
        {
            // Reset per-run latency stage timers. _modelLoadMs is overwritten
            // by LoadModel() if it runs (cold path); _hotkeySw is stopped
            // after waveInStart via the MicrophoneCapture.CaptureStarted
            // event; _recordDrainDuration is set from CaptureResult on
            // Record() return; _stopToPipelineSw and _whisperInitSw are
            // stopped from the whisper.cpp log hook on the first whisper_vad
            // line.
            _modelLoadMs = 0;
            _hotkeySw = System.Diagnostics.Stopwatch.StartNew();
            _recordDrainDuration = System.TimeSpan.Zero;
            _stopToPipelineSw = null;
            _whisperInitSw = null;

            // Consume the warmup flags on the first start — surface any
            // problems detected silently at startup before the pipeline runs.
            // Interlocked.Exchange makes the consumption race-free; the CAS
            // above already prevents two concurrent Starts, so this is now
            // belt-and-braces. Kept for documentation value at the call site.
            if (System.Threading.Interlocked.Exchange(ref _warmupFlagsConsumed, 1) == 0)
            {
                if (!ModelWarmupOk)
                {
                    _log.Error(
                        LogSource.Init,
                        "warmup flag | model_ok=false",
                        new UserFeedback(
                            Loc.Get("Engine_ModelNotReady_Title"),
                            Loc.Get("Engine_ModelNotReady_Body"),
                            UserFeedbackSeverity.Error,
                            UserFeedbackRole.Replacement));
                    return ToggleResult.IgnoredBusy;
                }
                if (!OllamaWarmupOk)
                {
                    // Live re-probe avant d'émettre le warning : Ollama peut
                    // être devenu reachable entre warmup et premier hotkey
                    // (cas typique : l'utilisateur a démarré Ollama après
                    // Deckle, ou le service Windows a fini son init après
                    // les 3 essais retry du warmup). Single-shot 3s, exécuté
                    // sur thread pool pour éviter tout risque de deadlock
                    // sur le UI thread du message host.
                    bool reachableNow = false;
                    try
                    {
                        var ollama = new Llm.OllamaService(
                            () => _host.Llm.OllamaEndpoint);
                        var probeTask = Task.Run(() => ollama.IsAvailableAsync());
                        if (probeTask.Wait(TimeSpan.FromSeconds(4)))
                            reachableNow = probeTask.Result;
                    }
                    catch
                    {
                        // IsAvailableAsync is fail-soft (catch interne), mais
                        // filet sur Task.Run / Wait au cas où.
                    }

                    if (!reachableNow)
                    {
                        _log.Warning(
                            LogSource.Init,
                            "warmup flag | ollama_ok=false (live re-probe also failed)",
                            new UserFeedback(
                                Loc.Get("Engine_RewriterUnavailable_Title"),
                                Loc.Get("Engine_RewriterUnavailable_Body"),
                                UserFeedbackSeverity.Warning,
                                UserFeedbackRole.Overlay));
                    }
                    else
                    {
                        _log.Info(
                            LogSource.Init,
                            "warmup flag | ollama_ok=false but live re-probe ok — proceeding without warning");
                    }
                    // Proceed with recording — rewrite is optional.
                }
                if (!MicrophoneWarmupOk)
                {
                    _log.Warning(LogSource.Init, "warmup flag | mic_ok=false (live probe below)");
                }
            }

            // Probe the audio device BEFORE firing StatusChanged("Recording").
            // If the mic is absent/busy, short-circuit the entire pipeline:
            // no HUD chrono, no worker thread, no Transcribe(empty).
            var probe = _capture.Probe(_host.Capture.AudioInputDeviceId);
            if (!probe.Ok)
            {
                var (title, body) = LocalizeMicError(probe.Kind, probe.MmsysErr);
                _log.Error(
                    LogSource.Capture,
                    $"probe MMSYSERR={probe.MmsysErr} — {title}",
                    new UserFeedback(title, body,
                        UserFeedbackSeverity.Error, UserFeedbackRole.Replacement));
                return ToggleResult.IgnoredBusy;
            }

            _shouldPaste       = shouldPaste;
            _manualProfileName = manualProfileName;
            lock (_segmentsLock) _segments.Clear();

            // Cancel any pending idle unload — a new pipeline is starting.
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // CAS Starting → Recording. From this moment the worker thread
            // owns the state machine — Stop, cap-duration, and the worker's
            // terminal finally are the only legitimate writers of _state.
            // The only thread that can compete here is Dispose (which moves
            // any state to Disposed); guard explicitly.
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)PipelineState.Recording,
                    (int)PipelineState.Starting)
                != (int)PipelineState.Starting)
            {
                _log.Warning(LogSource.Hotkey, "starting → recording CAS lost (likely Dispose)");
                return ToggleResult.IgnoredDisposed;
            }

            // Spawn the worker thread. WorkerRun owns the Recording →
            // Stopping → Transcribing → Idle transitions (and the Stopping
            // → Transcribing CAS in particular — the cap-duration branch
            // inside Record() does the Recording → Stopping CAS).
            _worker = new Thread(WorkerRun) { IsBackground = true, Name = "WhispEngine.Worker" };
            _worker.Start();

            committed = true;
            return ToggleResult.Started;
        }
        finally
        {
            if (!committed)
            {
                RollbackToIdle();
            }
        }
    }

    // Resets the state machine to Idle from Starting (hotkey-thread early-
    // return path). Worker-owned terminal Idle transitions live in
    // WorkerRun's finally — they must NOT call this helper because they
    // need to also emit "Ready" while skipping if Disposed has won.
    private void RollbackToIdle()
    {
        // Only roll back from Starting — never overwrite Recording (worker
        // already running) or Disposed. The worker spawn site is the
        // commit point; if we reach here without committing, _state is
        // still Starting unless Dispose has intervened.
        if (Interlocked.CompareExchange(
                ref _state,
                (int)PipelineState.Idle,
                (int)PipelineState.Starting)
            != (int)PipelineState.Starting)
        {
            // Dispose won — leave _state alone, just signal idle for any
            // Dispose Wait that may be pending.
            _idleEvent.Set();
            return;
        }
        _idleEvent.Set();
    }

    // Worker thread body. Runs the full Record → Transcribe pipeline,
    // performs the worker-owned state transitions, and is the ONLY site
    // that emits "Ready" on the success path. Any RaiseStatus(Loc.Get("Status_Ready"))
    // elsewhere in this file (UnloadModel, Transcribe early-returns) must
    // also gate on _state == Idle to avoid clobbering this invariant.
    private void WorkerRun()
    {
        // Cancellation channel for this run — Stop / Dispose call Cancel()
        // on it to drain the capture polling loop. Recreated each run so
        // a previous Cancel() doesn't leak into the next session.
        _recordCts = new CancellationTokenSource();

        try
        {
            if (!EnsureModelLoaded())
            {
                RaiseFinished(TranscriptionOutcome.None);
                return;
            }

            _recordingSw = System.Diagnostics.Stopwatch.StartNew();
            RaiseStatus(Loc.Get("Status_Recording"));
            RaiseNarrative(LogSource.Capture, "Recording from the microphone. Capture continues until you press the hotkey again.");

            CaptureResult capture = _capture.Record(_recordingHost, _recordCts.Token);
            _recordDrainDuration = capture.DrainDuration;

            // Surface the post-capture narrative + cap-hit handling here in
            // the orchestrator (Capture only emits codes — no localized
            // text). The narratives mirror the legacy phrasing so existing
            // log readers stay calibrated.
            if (capture.Outcome == CaptureOutcome.MicError)
            {
                var (title, body) = LocalizeMicError(
                    MicErrorKind.Unavailable, capture.MmsysErr);
                _log.Error(
                    LogSource.Capture,
                    $"capture mic error MMSYSERR={capture.MmsysErr} — {title}",
                    new UserFeedback(title, body,
                        UserFeedbackSeverity.Error, UserFeedbackRole.Replacement));
                RaiseFinished(TranscriptionOutcome.None);
                return;
            }

            if (capture.Outcome == CaptureOutcome.CapHit)
            {
                int minutes = _host.Capture.MaxRecordingDurationSeconds / 60;
                RaiseNarrative(LogSource.Capture,
                    $"Recording hit the {minutes} min cap — stopping automatically. The audio captured so far will be transcribed.");
                // CAS Recording → Stopping ourselves so the rest of the
                // transition sequence below stays uniform with the user-
                // driven Stop path. If Dispose won (state already Disposed)
                // we just lose this CAS and the post-Record CAS fails too.
                //
                // _stopToPipelineSw timing semantics on cap-hit:
                // legacy code started this stopwatch at the moment the cap
                // was hit, BEFORE the drain phase. After extraction the
                // drain runs inside MicrophoneCapture.Record so we only
                // start the stopwatch on return — the metric now excludes
                // the ~100 ms drain on cap-hit (rare path; user-driven
                // Stop path is unchanged because RequestToggle starts the
                // stopwatch BEFORE Cancel()). Acceptable drift.
                if (Interlocked.CompareExchange(
                        ref _state,
                        (int)PipelineState.Stopping,
                        (int)PipelineState.Recording)
                    == (int)PipelineState.Recording)
                {
                    _stopToPipelineSw = System.Diagnostics.Stopwatch.StartNew();
                }
            }

            RaiseNarrative(LogSource.Capture,
                $"Captured {capture.Pcm.Length / 16000.0:F1} s of audio. Moving on to analysis and transcription.");

            // Auto-calibration enveloppe — pure compute lives in
            // MicrophoneCalibrationCalculator; the ring buffer + side
            // effects (SaveSettings, ApplyLevelWindow, log) stay here.
            if (capture.Telemetry is not null)
            {
                TryAutoCalibrate(capture.Telemetry);
            }

            float[] audio = capture.Pcm;

            // Record() returns either because RequestToggle CAS'd
            // Recording → Stopping (the user pressed Stop), or because the
            // cap-duration branch CAS'd it itself. Either way the state
            // should now be Stopping; transition to Transcribing.
            // If we lose this CAS, Dispose has won — skip Transcribe.
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)PipelineState.Transcribing,
                    (int)PipelineState.Stopping)
                != (int)PipelineState.Stopping)
            {
                _log.Verbose(LogSource.Transcribe,
                    $"skip transcribe | state={(PipelineState)Volatile.Read(ref _state)}");
                RaiseFinished(TranscriptionOutcome.None);
                return;
            }

            RaiseStatus(Loc.Get("Status_Transcribing"));
            Transcribe(audio);
            ResetIdleTimer();
        }
        catch (Exception ex)
        {
            _log.Error(
                LogSource.Transcribe,
                $"pipeline crashed: {ex.GetType().Name}: {ex.Message}",
                new UserFeedback(
                    Loc.Get("Engine_PipelineCrashed_Title"),
                    Loc.Get("Engine_PipelineCrashed_Body"),
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseFinished(TranscriptionOutcome.None);
        }
        finally
        {
            // Terminal Idle transition — owned by the worker thread, in this
            // exact order: state, worker reference, idle event, then status.
            // The status fires last so any subscriber that reads _state from
            // a StatusChanged handler (tray tooltip, HudWindow) sees Idle by
            // the time "Ready" arrives.
            //
            // ★ THIS IS THE ONLY SITE THAT EMITS "Ready" ON THE SUCCESS PATH.
            // UnloadModel mirrors it for the cold-load case but also gates
            // on _state == Idle so the two never race.
            //
            // CAS loop instead of Exchange so a concurrent Dispose
            // transitioning *→Disposed wins cleanly: every CAS attempt re-
            // reads _state, sees Disposed, and bails out. Disposed must
            // persist past the worker's exit; a "Ready" emitted post-
            // Dispose would re-arm the tray on a half-shut-down engine.
            // The loop terminates in at most 2 iterations under contention
            // (only Dispose can compete with the worker for _state writes).
            int prev;
            while (true)
            {
                prev = Volatile.Read(ref _state);
                if (prev == (int)PipelineState.Disposed) break;
                if (Interlocked.CompareExchange(
                        ref _state, (int)PipelineState.Idle, prev) == prev)
                {
                    break;
                }
            }
            bool reachedIdle = prev != (int)PipelineState.Disposed;
            _worker = null;

            // Dispose and clear the per-run cancellation token. Done before
            // _idleEvent.Set() so a Dispose Wait that races on the event
            // doesn't see a still-live CTS field — fields read from there
            // are observably consistent with the worker exit.
            try { _recordCts?.Dispose(); }
            catch (ObjectDisposedException) { /* worker raced our dispose */ }
            _recordCts = null;

            _idleEvent.Set();
            if (reachedIdle)
            {
                RaiseStatus(Loc.Get("Status_Ready"));
            }
        }
    }

    // ── Microphone error localization ──────────────────────────────────────────
    //
    // MicErrorKind → (title, body) for UI. Messages formulated for the end user
    // — no Win32 jargon. Raw MMSYSERR code is included verbatim in the
    // Unavailable_Body_Format path so users can paste it back when reporting.
    // Capture itself stays free of any Loc.Get dependency; the engine owns
    // the localization step.
    private static (string Title, string Body) LocalizeMicError(MicErrorKind kind, uint err) => kind switch
    {
        MicErrorKind.NotDetected => (Loc.Get("MicError_NotDetected_Title"), Loc.Get("MicError_NotDetected_Body")),
        MicErrorKind.InUse       => (Loc.Get("MicError_InUse_Title"),       Loc.Get("MicError_InUse_Body")),
        _                        => (Loc.Get("MicError_Unavailable_Title"), Loc.Format("MicError_Unavailable_Body_Format", err)),
    };


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
        var lw = _host.Capture.LevelWindow;
        if (!lw.AutoCalibrationEnabled) return;

        int needed = Math.Max(1, lw.AutoCalibrationSamples);

        _autoCalibBuffer.Enqueue(payload);
        while (_autoCalibBuffer.Count > needed) _autoCalibBuffer.Dequeue();
        if (_autoCalibBuffer.Count < needed) return;

        // Pure compute lives in MicrophoneCalibrationCalculator — the
        // constants (-5 dB / +5 dB margins, -75 floor, ≥10 dB spread,
        // [-90,-10] / [-60,-10] clamps, 0.5 dB no-change tolerance) are
        // preserved exactly. The enveloppe (ring buffer, SaveSettings,
        // ApplyLevelWindow, log) stays here because the side effects
        // belong to the orchestrator.
        var calib = MicrophoneCalibrationCalculator.Compute(
            _autoCalibBuffer, lw.MinDbfs, lw.MaxDbfs);
        if (!calib.ShouldUpdate) return;

        lw.MinDbfs = calib.NewMinDbfs;
        lw.MaxDbfs = calib.NewMaxDbfs;
        _host.SaveSettings();

        // Push live into HudChrono so the next sub-window already uses the
        // new calibration. The host owns the static-field write
        // (App.ApplyLevelWindow on the App side).
        _host.ApplyLevelWindow(lw);

        _log.Success(LogSource.Capture,
            $"Auto-calibrated level window: Min={calib.NewMinDbfs:F0} Max={calib.NewMaxDbfs:F0} dBFS "
          + $"(median over {needed} sessions, p25-5dB / p90+5dB margins)");
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
            int total = WhisperPInvoke.whisper_full_n_segments(ctx);
            int from  = total - n_new;
            // Lower bound of timestamp token IDs — above this are <|t.tt|>,
            // not text tokens. Cached per Transcribe call to avoid unnecessary
            // repeated P/Invoke (depends on model, not segment).
            int tokenBeg = _tokenBeg;
            for (int i = from; i < total; i++)
            {
                string segText = Marshal.PtrToStringUTF8(WhisperPInvoke.whisper_full_get_segment_text(ctx, i)) ?? "";
                long  t0  = WhisperPInvoke.whisper_full_get_segment_t0(ctx, i);
                long  t1  = WhisperPInvoke.whisper_full_get_segment_t1(ctx, i);
                float nsp = WhisperPInvoke.whisper_full_get_segment_no_speech_prob(ctx, i);

                // Per-segment confidence, aggregated over text tokens only.
                // p = linear probability of the token as sampled by Whisper.
                // avg = "is the sentence globally confident?", min = "weakest link / fabricated word?".
                int nTok = WhisperPInvoke.whisper_full_n_tokens(ctx, i);
                float sumP = 0f, minP = 1f;
                int textTok = 0;
                for (int k = 0; k < nTok; k++)
                {
                    int id = WhisperPInvoke.whisper_full_get_token_id(ctx, i, k);
                    if (id >= tokenBeg) continue; // skip tokens timestamp
                    float p = WhisperPInvoke.whisper_full_get_token_p(ctx, i, k);
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
                    RaiseNarrative(LogSource.Transcribe,
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

    private void Transcribe(float[] audio, CancellationToken ct = default)
    {
        IntPtr ctx = _ctx;
        if (ctx == IntPtr.Zero)
        {
            RaiseStatus(Loc.Get("Status_ModelNotReady"));
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        if (audio.Length == 0)
        {
            _log.Warning(LogSource.Transcribe, "empty audio buffer, nothing to transcribe");
            // No RaiseStatus here — WorkerRun's finally is the canonical
            // emission point for "Ready" on the success path. Emitting it
            // both here and there would just send the event twice.
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        IntPtr fullParamsPtr = WhisperPInvoke.whisper_full_default_params_by_ref(0);
        WhisperFullParams wparams = Marshal.PtrToStructure<WhisperFullParams>(fullParamsPtr);
        WhisperPInvoke.whisper_free_params(fullParamsPtr);

        wparams.print_progress = 0;

        // Snapshot user settings at the start of transcription.
        // Hot-reload fields (thresholds, VAD, suppress, context, decoding)
        // are applied here on each call — no context restart needed.
        // Heavy settings (model, use_gpu) are handled at LoadModel.
        var whispSettings = _host.Whisp;
        var nativeAllocs = WhisperParamsMapper.Apply(ref wparams, whispSettings, _host.ResolveModelsDirectory());

        // Cache the timestamp token bound once for the entire call — it's a model
        // property, not per-segment, no need to call for each token.
        _tokenBeg = WhisperPInvoke.whisper_token_beg(ctx);
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
        // Combined abort signal: repetition-loop guard (via _abortRequested)
        // OR external cancellation (warmup interrupted by a hotkey or Dispose).
        // The local capture is safe because whisper_full holds the function
        // pointer only for the duration of the call — once the lock below is
        // released, the delegate is no longer probed.
        _abortCallback = _ => _abortRequested || ct.IsCancellationRequested;
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
        string prompt = whispSettings.Transcription.InitialPrompt;
        bool carry = whispSettings.Transcription.CarryInitialPrompt;
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
        // Whisper init latency starts here — closed by the log hook on the
        // first whisper_vad line. Measures pre-VAD overhead inside whisper_full
        // (context init, mel computation, GPU upload). Distinct from VadMs,
        // which measures the VAD module itself.
        _whisperInitSw = System.Diagnostics.Stopwatch.StartNew();
        var sw = _transcribeSw;
        // ★ whisper.cpp is not thread-safe across concurrent calls on a
        // single context — two whisper_full on the same _ctx = native
        // segfault, no managed exception, the process dies. The pipeline
        // state machine prevents the user-driven path from re-entering
        // here, but Warmup() also calls Transcribe() at startup on its own
        // background thread, and that path bypasses the state machine. The
        // lock makes the invariant local to the call site instead of having
        // to reason about it across files.
        int result;
        lock (_transcribeLock)
        {
            result = WhisperPInvoke.whisper_full(ctx, wparams, audio, audio.Length);
        }
        sw.Stop();
        long transcribeMsTotal = sw.ElapsedMilliseconds;

        _vadCapturing = false;
        if (_vadSw is { IsRunning: true }) _vadSw.Stop();
        // Force-stop the upstream timers when whisper_full bailed before any
        // whisper_vad line was emitted (e.g. an init-phase failure). The hook
        // path stops them on the first VAD line; without that signal they
        // would keep running and report unbounded values into the payload.
        if (_whisperInitSw is { IsRunning: true }) _whisperInitSw.Stop();
        if (_stopToPipelineSw is { IsRunning: true }) _stopToPipelineSw.Stop();
        long vadMs = _vadSw?.ElapsedMilliseconds ?? 0;
        long whisperInitMs = _whisperInitSw?.ElapsedMilliseconds ?? 0;
        // Pure decoding time = total wall time of whisper_full minus the two
        // sub-phases observed via the log hook (pre-VAD setup + VAD itself).
        long whisperMs = Math.Max(0, transcribeMsTotal - vadMs - whisperInitMs);

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
                    Loc.Get("Engine_TranscriptionFailed_Title"),
                    Loc.Get("Engine_TranscriptionFailed_Body"),
                    UserFeedbackSeverity.Error,
                    UserFeedbackRole.Replacement));
            RaiseStatus(Loc.Get("Status_TranscriptionFailed"));
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
            RaiseNarrative(LogSource.Transcribe, $"Whisper transcribed the speech into {nSeg} segments in {transcribeMsTotal / 1000.0:F1} s.");
        }

        if (string.IsNullOrWhiteSpace(fullText))
        {
            RaiseStatus(Loc.Get("Status_Ready"));
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        // Warmup short-circuit. The expensive part — VAD + whisper_full +
        // first-time Vulkan kernel compile — is now paid. Skipping the
        // clipboard write, the LLM rewrite, and the paste keeps the user's
        // clipboard untouched at boot, avoids a cold Ollama hit, and prevents
        // a "Pasted" Narrative from leaking through. The Warmup() caller
        // logs its own success line.
        if (t_isWarmup)
        {
            return;
        }

        // Low-audio warning is emitted live by MicrophoneCapture once 5 s
        // of sustained sub-threshold signal has accumulated — see the
        // tracker in WaveInLoop.Pump and the OnCaptureLowAudioDetected
        // localizer above. Alerting during recording is the whole point
        // of that message: we want the user to stop talking into a broken
        // mic within seconds, not discover it 20 min later.

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
            RaiseStatus(Loc.Get("Status_Ready"));
            RaiseFinished(TranscriptionOutcome.None);
            return;
        }

        long llmMs           = 0;
        long ollamaLoadMs    = 0;
        long llmPromptEvalMs = 0;
        long llmEvalMs       = 0;
        int  llmPromptTokens = 0;
        int  llmEvalTokens   = 0;
        var llmSettings = _host.Llm;
        double recDurationSec = (_recordingSw?.Elapsed.TotalSeconds) ?? 0;
        int rawWordCount = Logging.TextMetrics.CountWords(fullText);

        // Rewrite profile resolution:
        // - manual rewrite hotkey → the profile name passed to StartRecording
        // - plain transcribe hotkey → first matching AutoRewriteRule (duration-based)
        RewriteProfile? profile = null;
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
            RewriteProfile? ResolveRuleProfile(string? id, string? name)
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
            RaiseStatus(Loc.Format("Status_Rewriting_Format", profile.Name));
            // Narrative only after the call settles — on success we know it
            // landed, on failure we say so explicitly. The previous pre-call
            // "is now rewriting" line lied on failure by implying completion
            // (no counter-narrative was ever emitted). HUD state + polling
            // heartbeat already cover live feedback during the wait.
            var swLlm = System.Diagnostics.Stopwatch.StartNew();
            var llmResult = _llm.Rewrite(fullText, llmSettings.OllamaEndpoint, profile);
            swLlm.Stop();
            // Wall-clock total (caller-side) is the authoritative number for
            // user-perceived latency — includes HTTP transit + JSON parse on
            // top of the server-side Ollama timings. The structured Ollama
            // metrics flow through to the LatencyPayload so we can later
            // compare wall vs server side and isolate transit overhead.
            llmMs           = swLlm.ElapsedMilliseconds;
            ollamaLoadMs    = llmResult.OllamaLoadMs;
            llmPromptEvalMs = llmResult.PromptEvalMs;
            llmEvalMs       = llmResult.EvalMs;
            llmPromptTokens = llmResult.PromptTokens;
            llmEvalTokens   = llmResult.EvalTokens;
            if (!string.IsNullOrWhiteSpace(llmResult.Text))
            {
                fullText = llmResult.Text;
                // If the post-rewrite copy fails, the raw transcript from the
                // first copy is still on the clipboard — degrade silently to
                // the raw text instead of making a loud noise about a failure
                // that doesn't hurt the user.
                CopyToClipboard(fullText);
                RaiseNarrative(LogSource.Llm, $"Rewrite complete in {swLlm.Elapsed.TotalSeconds:F1} s with the {profile.Name} profile — the polished text is ready to paste.");
            }
            else
            {
                RaiseNarrative(LogSource.Llm, $"Rewrite failed after {swLlm.Elapsed.TotalSeconds:F1} s — raw transcript kept. Check the log for the Ollama error.");
            }
        }

        long pasteMs = 0;
        bool pasteVerified = false;
        if (_shouldPaste)
        {
            // Synchronous rendezvous: the handler (App) hides the HUD and only
            // returns once SW_HIDE is effective on the UI thread. After this
            // point, nothing in Deckle touches activation until the end of
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
            RaiseNarrative(LogSource.Paste, $"Final text pasted into {exeName}.");
        }

        // Split recap into two Info lines (timings / outputs) that land under
        // Activity, plus the existing Narrative for the user-facing closing line.
        // The monolithic 200-char Verbose is gone — each line reads cleanly
        // in LogWindow and stays grep-friendly through the standard `k=v` format.
        // Outcome : Pasted on a verified paste delivery, ClipboardOnly when
        // the text made it to the clipboard but paste was disabled or refused
        // (target lost, Deckle itself, SendInput partial) — the HUD uses
        // this to flash "Copied" or the Ctrl+V reminder before hiding.
        var outcome = (_shouldPaste && pasteVerified) ? TranscriptionOutcome.Pasted
                                                      : TranscriptionOutcome.ClipboardOnly;
        int finalWordCount = Logging.TextMetrics.CountWords(fullText);

        // Snapshot stage timers once for both the log line and the telemetry
        // payload. Each can be null when the run skipped that stage (e.g.
        // _hotkeySw is null on the Warmup path) — coerce to 0 so the payload
        // stays well-formed.
        long hotkeyToCaptureMs = _hotkeySw?.ElapsedMilliseconds         ?? 0;
        long recordDrainMs     = (long)_recordDrainDuration.TotalMilliseconds;
        long stopToPipelineMs  = _stopToPipelineSw?.ElapsedMilliseconds ?? 0;
        // _vadInferenceMs is parsed from the whisper.cpp `vad time = X ms`
        // log line as a float; rounded to long here to match the inventory
        // ms-int convention. -1f means "no VAD line was parsed" (no-speech
        // short-circuit, hook miss) — coerce to 0 so the payload only carries
        // valid values.
        long vadInferenceMs    = _vadInferenceMs >= 0 ? (long)Math.Round(_vadInferenceMs) : 0;

        _log.Success(LogSource.Done, $"Done ({outcome})");
        _log.Verbose(LogSource.Done,
            $"timings | audio_sec={recDurationSec:F1} | model_load_ms={_modelLoadMs} | hotkey_to_capture_ms={hotkeyToCaptureMs} | record_drain_ms={recordDrainMs} | stop_to_pipeline_ms={stopToPipelineMs} | whisper_init_ms={whisperInitMs} | vad_ms={vadMs} | vad_inference_ms={vadInferenceMs} | whisper_ms={whisperMs} | llm_ms={llmMs} | clipboard_ms={swClip.ElapsedMilliseconds} | paste_ms={pasteMs}");
        _log.Verbose(LogSource.Done,
            $"llm_metrics | ollama_load_ms={ollamaLoadMs} | prompt_eval_ms={llmPromptEvalMs} | eval_ms={llmEvalMs} | prompt_tokens={llmPromptTokens} | eval_tokens={llmEvalTokens}");
        _log.Verbose(LogSource.Done,
            $"outputs | n_seg={nSeg} | chars={fullText.Length} | words={finalWordCount} | strategy={_strategyLabel} | profile={profile?.Name ?? "(none)"} | outcome={outcome}");
        RaiseNarrative(LogSource.Done, $"Done — {recDurationSec:F1} s of dictation processed. Ready for the next.");

        RaiseStatus(Loc.Get("Status_Ready"));
        _recordingSw?.Stop();

        Logging.TelemetryService.Instance.Latency(new Logging.LatencyPayload(
            AudioSec:          audioSec,
            ModelLoadMs:       _modelLoadMs,
            HotkeyToCaptureMs: hotkeyToCaptureMs,
            RecordDrainMs:     recordDrainMs,
            StopToPipelineMs:  stopToPipelineMs,
            WhisperInitMs:     whisperInitMs,
            VadMs:             vadMs,
            VadInferenceMs:    vadInferenceMs,
            WhisperMs:         whisperMs,
            LlmMs:             llmMs,
            OllamaLoadMs:      ollamaLoadMs,
            LlmPromptEvalMs:   llmPromptEvalMs,
            LlmEvalMs:         llmEvalMs,
            LlmPromptTokens:   llmPromptTokens,
            LlmEvalTokens:     llmEvalTokens,
            ClipboardMs:       swClip.ElapsedMilliseconds,
            PasteMs:           pasteMs,
            Strategy:          _strategyLabel,
            NSegments:         nSeg,
            TextChars:         fullText.Length,
            TextWords:         finalWordCount,
            Profile:           profile?.Name ?? "",
            Pasted:            pasteVerified,
            Outcome:           outcome.ToString()));

        // Corpus logging — captures the raw Whisper output only. We don't
        // persist the rewrite: prompts evolve, so paired (raw, rewrite)
        // samples go stale the moment the prompt is edited. Raw text stays
        // useful for benchmarking Whisper itself (initial prompt, VAD, model
        // swap). One file per rewrite profile so samples stay sliceable by
        // the workflow they came from, even without the rewrite payload.
        var telemetrySettings = _host.Telemetry;
        if (telemetrySettings.CorpusEnabled && profile is not null)
        {
            var whisperSettings = _host.Whisp.Transcription;
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
                    Loc.Get("Engine_ClipboardCopyFailed_Memory_Title"),
                    Loc.Get("Engine_ClipboardCopyFailed_Memory_Body"),
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
                    Loc.Get("Engine_ClipboardUnavailable_Title"),
                    Loc.Get("Engine_ClipboardUnavailable_Body"),
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
                    Loc.Get("Engine_ClipboardCopyFailed_Refused_Title"),
                    Loc.Get("Engine_ClipboardCopyFailed_Refused_Body"),
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
                        Loc.Get("Engine_ClipboardIncomplete_Unverified_Title"),
                        Loc.Get("Engine_ClipboardIncomplete_Unverified_Body"),
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
                            Loc.Get("Engine_ClipboardIncomplete_LengthMismatch_Title"),
                            Loc.Get("Engine_ClipboardIncomplete_LengthMismatch_Body"),
                            UserFeedbackSeverity.Warning,
                            UserFeedbackRole.Overlay));
                }
            }
            NativeMethods.CloseClipboard();
        }

        _log.Info(LogSource.Clipboard, "Copied to clipboard");
        RaiseNarrative(LogSource.Clipboard, $"The transcription is now on the clipboard — {text.Length} characters ready to paste anywhere.");
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
    // control type, foreground is Deckle itself — the text stays on the
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

        // Refuse if the foreground is a Deckle window itself (LogWindow, HUD,
        // Settings). Avoids the false positive where we would paste into our
        // own logs while the user reads them.
        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        if (fgPid == ownPid)
        {
            _log.Warning(LogSource.Paste, "skipped: foreground is Deckle itself. Clipboard holds the text — Ctrl+V in the right window.");
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

    // ── Dispose ───────────────────────────────────────────────────────────────

    // Tray Quit → App.QuitApp → here. The state machine flips to Disposed
    // unconditionally so any in-flight worker thread or stray hotkey lands
    // on a refusal path. Then we wait for the worker to actually exit
    // before freeing _ctx — whisper_free on a context with active inference
    // is a native segfault that no managed handler can rescue.
    //
    // Timeout: whisper_full on a 60 s recording can take 5-15 s on a GPU
    // backend, so 30 s is enough for normal cases. If it expires we log a
    // Warning and leak the worker thread — the process is exiting anyway,
    // and the alternative (tearing down _ctx underneath a running
    // whisper_full) is the very crash this method exists to prevent.
    private const int DISPOSE_WORKER_JOIN_TIMEOUT_MS = 30_000;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unblock a warmup still in flight at Quit time — without this, the
        // _transcribeLock acquisition at the end of Dispose can wait up to
        // ~800 ms for warmup's whisper_full to finish on its own. Cancelling
        // brings that down to ~50 ms.
        TrySignalWarmupCancel();

        // Capture before transitioning so the Verbose line below records
        // what the engine was actually doing when Dispose arrived.
        var prevState = (PipelineState)Volatile.Read(ref _state);
        Interlocked.Exchange(ref _state, (int)PipelineState.Disposed);

        // Tell the capture loop to stop, in case the worker is still in
        // there. WorkerRun's Stopping → Transcribing CAS will lose to our
        // Disposed write and skip Transcribe entirely, but it still needs
        // to exit MicrophoneCapture.Record() cleanly to release the waveIn
        // handles. CTS may already be disposed if WorkerRun raced ahead.
        try { _recordCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        var worker = _worker;
        if (worker is not null && worker.IsAlive)
        {
            _log.Verbose(LogSource.App,
                $"dispose | waiting on worker | prev_state={prevState} | timeout_ms={DISPOSE_WORKER_JOIN_TIMEOUT_MS}");
            var swJoin = System.Diagnostics.Stopwatch.StartNew();
            bool joined = worker.Join(DISPOSE_WORKER_JOIN_TIMEOUT_MS);
            swJoin.Stop();
            if (!joined)
            {
                _log.Warning(LogSource.App,
                    $"dispose timeout | join_ms={swJoin.ElapsedMilliseconds} — worker still alive, leaking thread (process exiting)");
            }
            else
            {
                _log.Verbose(LogSource.App, $"dispose | worker joined | join_ms={swJoin.ElapsedMilliseconds}");
            }
        }

        _idleTimer?.Dispose();

        // _idleEvent is intentionally NOT disposed — if the Join timed out,
        // the leaked worker may still call _idleEvent.Set() in its finally,
        // and Dispose'ing the event would turn that into an
        // ObjectDisposedException. The process is exiting anyway, so the
        // wait-handle leak doesn't matter; the OS reclaims it on exit.

        // _transcribeLock guarantees no whisper_full is in progress on _ctx
        // by the time we reach here — either WorkerRun joined (above) and
        // released the lock, or it timed out and we're shutting down anyway.
        // Acquiring the lock briefly here serialises against Warmup() if it
        // somehow outlived the worker join.
        lock (_transcribeLock)
        {
            if (_ctx != IntPtr.Zero)
            {
                WhisperPInvoke.whisper_free(_ctx);
                _ctx = IntPtr.Zero;
            }
        }

        _capture.Dispose();
    }
}
