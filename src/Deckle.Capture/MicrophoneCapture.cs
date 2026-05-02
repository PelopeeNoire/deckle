using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Deckle.Capture.Internal;
using Deckle.Capture.Telemetry;
using Deckle.Interop;
using Deckle.Logging;

namespace Deckle.Capture;

// Microphone capture engine — owns the waveIn handle, the polling loop,
// the per-recording RMS log, and the post-recording telemetry payload.
//
// Designed as a service consumed by orchestrators (today: WhispEngine;
// future: Ask-Ollama). Capture has no notion of pipeline state — it just
// blocks in Record() until the cancellation token fires or the duration
// cap hits, then returns the captured float[] and a structured outcome.
//
// Lifetime: one instance per orchestrator, created at orchestrator
// construction, disposed at orchestrator disposal. Probe() and Record()
// are NOT re-entrant on a single instance — the orchestrator's state
// machine (Idle → Starting → Recording → Stopping) already serializes.
//
// All public events fire from the recording thread (the thread that
// called Record()). Subscribers are responsible for marshaling to the
// UI thread.
public sealed class MicrophoneCapture : System.IDisposable
{
    // Constants (matches the legacy Record() body). 50 ms buffers (1600
    // bytes) so AudioLevel events fire at a steady ~20 Hz spread across
    // time, not in bursts of 10 every 500 ms. 500 ms bursts were the
    // original size — trivial driver workload back then but catastrophic
    // for a real-time HUD animation because the outline couldn't react
    // inside a spoken word. 4 circular buffers give 200 ms of headroom
    // if the drain loop stalls, still enough on any modern scheduler.
    // 20 waveIn callbacks/s is a no-op for modern drivers (WASAPI defaults
    // to 10 ms periods).
    private const uint WAVE_MAPPER    = 0xFFFFFFFF;
    private const uint CALLBACK_EVENT = 0x00050000;
    private const int  N_BUFFERS      = 4;
    private const int  BYTES_PER_BUF  = 16000 * 2 * 50 / 1000; // 50ms × 16kHz × 2 bytes/sample

    private readonly LogService _log;

    // Per-recording RMS history — one linear-RMS sample per 50 ms sub-window
    // (so ~20 Hz). Cleared at every Record() entry, fed by EmitSubWindows
    // in flow order (in WaveInLoop). Drives:
    //   - the Tail-600 ms diagnostic at Stop (last 12 samples — sidesteps the
    //     bytes-buffer ordering ambiguity of the old re-computation path),
    //   - the per-recording mic telemetry summary (min / percentiles / max
    //     in dBFS) used to calibrate MinDbfs / MaxDbfs against the actual
    //     hardware response.
    // Pre-reserved for ~10 minutes at 20 Hz; the List grows past that without
    // a resize allocation explosion thanks to standard doubling.
    private readonly List<float> _rmsLog = new(capacity: 20 * 60 * 10);

    // Microphone level, linear RMS [0, 1], throttled ~20 Hz (one emission per
    // 50 ms sub-window of the captured audio). Fired from the recording thread.
    public event System.Action<float>? AudioLevel;

    // Fired after waveInStart succeeds — the mic is now live and the first
    // 50 ms buffer is on its way. Used by the orchestrator to close the
    // hotkey-to-capture latency stopwatch (_hotkeySw in WhispEngine).
    public event System.Action? CaptureStarted;

    // Fired once during a Record() call when the live low-audio tracker
    // crossed its threshold (no sustained healthy voice in the first 5 s).
    // The orchestrator is expected to surface a localized UserFeedback
    // overlay (Engine_LowAudio_Title / Body) — Capture itself stays free
    // of any Loc.Get dependency.
    public event System.Action? LowAudioDetected;

    public MicrophoneCapture(LogService log)
    {
        _log = log;
    }

    // ── Pre-flight probe ──────────────────────────────────────────────────────
    //
    // Attempts waveInOpen + waveInClose in sequence with the target format and
    // configured device. If it passes, we know the recording session can start;
    // otherwise the MMSYSERR code maps to a MicErrorKind for the orchestrator
    // to localize. Measured cost ~1-2 ms on a healthy device — negligible vs
    // Whisper latency.
    public ProbeResult Probe(int deviceId)
    {
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

        uint deviceIdRaw = deviceId < 0 ? WAVE_MAPPER : (uint)deviceId;

        uint err = NativeMethods.waveInOpen(
            out System.IntPtr hWaveIn,
            deviceIdRaw,
            ref wfx,
            System.IntPtr.Zero,
            System.IntPtr.Zero,
            0u);
        if (err != 0)
        {
            return new ProbeResult(false, MapMmsysErr(err), err);
        }

        NativeMethods.waveInClose(hWaveIn);
        return new ProbeResult(true, MicErrorKind.None, 0);
    }

    // MMSYSERR → MicErrorKind (canonical mapping, mirrors the original
    // DescribeMicError table). The orchestrator does the Loc.Get translation.
    private static MicErrorKind MapMmsysErr(uint err) => err switch
    {
        0 => MicErrorKind.None,
        2 => MicErrorKind.NotDetected, // MMSYSERR_BADDEVICEID
        6 => MicErrorKind.NotDetected, // MMSYSERR_NODRIVER
        4 => MicErrorKind.InUse,       // MMSYSERR_ALLOCATED
        _ => MicErrorKind.Unavailable,
    };

    // ── Audio recording ──────────────────────────────────────────────────────
    //
    // Captures the microphone continuously into a single resizable buffer.
    // When `ct` fires (set by RequestToggle on Recording → Stopping CAS in
    // the orchestrator) or the cap-duration branch hits internally, returns
    // all accumulated audio as float[] (PCM16 → float [-1, 1]). Whisper
    // handles its own internal windowing (30s + dynamic seek) and inter-
    // window context propagation via tokens — no chunking here.
    public CaptureResult Record(IRecordingHost host, CancellationToken ct)
    {
        System.IntPtr hEvent = NativeMethods.CreateEvent(
            System.IntPtr.Zero, bManualReset: false, bInitialState: false, null);

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

        // Device selected in Settings. -1 = WAVE_MAPPER (system default).
        int configuredDevice = host.AudioInputDeviceId;
        uint deviceId = configuredDevice < 0 ? WAVE_MAPPER : (uint)configuredDevice;

        uint err = NativeMethods.waveInOpen(
            out System.IntPtr hWaveIn, deviceId, ref wfx, hEvent,
            System.IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            _log.Error(LogSource.Capture, $"waveInOpen error {err}");
            NativeMethods.CloseHandle(hEvent);
            return new CaptureResult(
                Pcm:            System.Array.Empty<float>(),
                Telemetry:      null,
                Outcome:        CaptureOutcome.MicError,
                DrainDuration:  System.TimeSpan.Zero,
                MmsysErr:       err);
        }

        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        var hdrPtrs = new System.IntPtr[N_BUFFERS];
        var bufPtrs = new System.IntPtr[N_BUFFERS];

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
        // Hotkey-to-capture latency closes here — the mic is now live and the
        // first 50 ms buffer is on its way. The orchestrator's stopwatch
        // (_hotkeySw in WhispEngine) is closed via the CaptureStarted event.
        CaptureStarted?.Invoke();

        // Reset the per-recording RMS series — the previous session's tail
        // must not leak into this run's telemetry summary.
        _rmsLog.Clear();

        PumpResult pump = default;
        try
        {
            pump = WaveInLoop.Pump(
                hWaveIn:             hWaveIn,
                hEvent:              hEvent,
                hdrPtrs:             hdrPtrs,
                nBuffers:            N_BUFFERS,
                hdrSize:             hdrSize,
                host:                host,
                log:                 _log,
                rmsLog:              _rmsLog,
                audioLevelCallback:  rms => AudioLevel?.Invoke(rms),
                onLowAudioDetected:  () => LowAudioDetected?.Invoke(),
                ct:                  ct);
        }
        finally
        {
            // waveInUnprepareHeader runs inside Pump's drain loop already; we
            // still need to free the unmanaged buffers + the device + event
            // handle. Order matters: Close the device, then free per-buffer
            // memory, then close the event.
            for (int i = 0; i < N_BUFFERS; i++)
            {
                Marshal.FreeHGlobal(bufPtrs[i]);
                Marshal.FreeHGlobal(hdrPtrs[i]);
            }
            NativeMethods.waveInClose(hWaveIn);
            NativeMethods.CloseHandle(hEvent);
        }

        // pump.Pcm is null only if Pump threw before producing a byte buffer
        // (which would have already propagated through the finally above —
        // we'd never reach this line). Defensive guard kept for clarity.
        byte[] allBytes = pump.Pcm ?? System.Array.Empty<byte>();
        double totalSec = allBytes.Length / 32000.0;

        // Full-buffer aggregate: mean RMS + peak amplitude over the entire
        // recording. Single pass over allBytes, cost is negligible vs the
        // upcoming whisper_full call (~1 ms for a minute of audio at 16 kHz).
        // dbfs_avg = 20*log10(rms_avg), floored at -120 dBFS when the buffer
        // is pure zero to avoid −∞ in the log.
        double aggSumSq = 0;
        double aggPeak  = 0;
        int nAggSamples = allBytes.Length / 2;
        for (int i = 0; i < nAggSamples; i++)
        {
            short s = (short)(allBytes[i * 2] | (allBytes[i * 2 + 1] << 8));
            double v = s / 32768.0;
            aggSumSq += v * v;
            double av = v < 0 ? -v : v;
            if (av > aggPeak) aggPeak = av;
        }
        double rmsAvg  = nAggSamples > 0 ? System.Math.Sqrt(aggSumSq / nAggSamples) : 0;
        double dbfsAvg = rmsAvg > 0 ? 20.0 * System.Math.Log10(rmsAvg) : -120.0;

        _log.Info(LogSource.Capture, $"Recording complete ({totalSec:F1} s)");
        _log.Verbose(LogSource.Capture,
            $"capture complete | audio_sec={totalSec:F1} | buffers={pump.BuffersReceived} | bytes={allBytes.Length} | rms_avg={rmsAvg:F4} | rms_peak={aggPeak:F4} | dbfs_avg={dbfsAvg:F1}");

        // Mic telemetry — distribution + tail summary derived from _rmsLog.
        // Replaces the previous Tail-on-allBytes computation, which was
        // returning RMS=0 (= -96.7 dBFS, the "uninitialised buffer" floor)
        // even on sessions Whisper transcribed perfectly. Root cause:
        // the post-Stop drain loop concatenates buffers in WHDR index order
        // 0..N-1, which does not always match temporal order at Stop, so
        // the last 600 ms read from the byte tail could land on an
        // out-of-order or partially-zeroed buffer. _rmsLog is fed in flow
        // order by EmitSubWindows (during Recording AND in the drain
        // pass above), so its tail genuinely reflects the final ~600 ms
        // of audio.
        MicrophoneTelemetryPayload? telemetry = null;
        var tail = MicrophoneTelemetryCalculator.ComputeTail(_rmsLog);
        if (tail is null)
        {
            _log.Warning(LogSource.Capture, "Mic telemetry: no RMS samples captured (recording too short or audio thread starved)");
        }
        else
        {
            // User-facing tail headline — the line is read in the Activity
            // selector to tell whether you stopped after a silence (the
            // natural case) or while still speaking (often a hotkey hit
            // too early — last words may be clipped).
            _log.Info(LogSource.Capture,
                $"{tail.Value.TailHeadline} (last {tail.Value.TailMs} ms at {tail.Value.TailDbfs:F1} dBFS)");

            telemetry = MicrophoneTelemetryCalculator.Compute(_rmsLog, tail.Value);
        }

        // Gate emission by the host's MicrophoneTelemetryEnabled toggle.
        // The payload is always *computed* (auto-calibration depends on
        // it); this flag only decides whether it's broadcast. Auto-
        // calibration itself runs on the orchestrator side — it has access
        // to the host's settings + ApplyLevelWindow callback.
        if (telemetry is not null && host.MicrophoneTelemetryEnabled)
        {
            TelemetryService.Instance.Microphone(telemetry);
        }

        float[] pcmFloat = PcmConversion.PcmToFloat(allBytes);

        var outcome = pump.Exit switch
        {
            LoopExit.CapHit => CaptureOutcome.CapHit,
            _               => CaptureOutcome.Completed,
        };

        return new CaptureResult(
            Pcm:           pcmFloat,
            Telemetry:     telemetry,
            Outcome:       outcome,
            DrainDuration: pump.DrainDuration,
            MmsysErr:      0);
    }

    public void Dispose()
    {
        // No persistent native resource — all waveIn handles are scoped to a
        // single Record() call and freed in its `finally`. Method is here to
        // honour the contract advertised in the API surface and to give the
        // class a stable shutdown point for future fields (e.g. a cached
        // device-info cache).
    }
}
