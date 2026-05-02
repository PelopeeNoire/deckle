using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Deckle.Interop;
using Deckle.Logging;

namespace Deckle.Capture.Internal;

// Internal helper extracted from the legacy 331-line Record() body. Owns
// only the inner polling loop + post-Stop drain — buffer allocation and
// device open/close stay in MicrophoneCapture.
//
// Exit reason (LoopExit):
//   StopRequested  — cancellation token fired (user-driven Stop in the
//                    orchestrator).
//   CapHit         — the host's MaxRecordingDurationSeconds was reached
//                    inside the loop. The orchestrator transitions its
//                    state machine accordingly.
internal enum LoopExit
{
    StopRequested,
    CapHit,
}

// Result of Pump — separated from CaptureResult because the orchestrator
// still needs to call MicrophoneTelemetryCalculator to build the final
// payload, and to invoke PcmToFloat on the byte buffer. The drain duration
// is captured here (the loop owns the post-Stop drain phase).
internal readonly record struct PumpResult(
    byte[]      Pcm,
    LoopExit    Exit,
    int         BuffersReceived,
    System.TimeSpan DrainDuration);

internal static class WaveInLoop
{
    // Sub-window cadence ↔ host narration timing. Keep aligned with the
    // 50 ms allocation in MicrophoneCapture.Record (BYTES_PER_BUF).
    private const int BYTES_PER_50MS = 16000 * 2 * 50 / 1000; // 1600 bytes
    private const uint WHDR_DONE = 0x00000001;

    // Drain the waveIn polling loop until either ct fires or the duration
    // cap is hit. Accumulates PCM16 bytes and feeds the AudioLevel
    // emission via EmitSubWindows. Returns the full byte buffer +
    // DrainDuration measured around the post-Stop drain phase.
    //
    // The TEMP DIAG capture-lag instrumentation block (iter / wait_ms /
    // prev_iter_ms / gc deltas) is preserved verbatim from the legacy
    // Record() body — it's referenced in
    // memory/project_capture_lag and is sacred. Do not "clean up".
    public static PumpResult Pump(
        IntPtr            hWaveIn,
        IntPtr            hEvent,
        IntPtr[]          hdrPtrs,
        int               nBuffers,
        uint              hdrSize,
        IRecordingHost    host,
        LogService        log,
        List<float>       rmsLog,            // owned by MicrophoneCapture, cleared at start
        System.Action<float>? audioLevelCallback,
        System.Action?    onLowAudioDetected, // fires once when first 5 s lacked sustained voice
        CancellationToken ct)
    {
        // Single buffer, grows throughout the recording.
        // 1 sample = 2 bytes PCM16. At 16 kHz, 1 minute = 1.92M bytes.
        var allBytes = new List<byte>(capacity: 16000 * 2 * 60); // pre-reserve ~1 min

        log.Info(LogSource.Capture, "Recording start");
        log.Verbose(LogSource.Capture, "capture start | sample_rate=16 kHz | channels=mono");

        // Snapshot the cap at recording start so a mid-recording Settings
        // change doesn't shorten or extend a session already in progress.
        int maxDurationSec = host.MaxRecordingDurationSeconds;
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
        // dBFS. The old -35 dBFS threshold rejected typical condenser
        // mics even during active speech. -45 dBFS leaves headroom for
        // quieter setups while
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

        // TEMP DIAG (capture-lag investigation) — strip after collecting
        // 5–10 occurrences in the wild. Tells us which of GC pause /
        // CPU preemption / cold-start / heavy inline work caused the
        // 3-buffer pile-up. Plan:
        // C:\Users\Louis\.claude\plans\pourquoi-le-ring-buffer-effervescent-ritchie.md
        long diagIterationCount = 0;
        long diagLastIterMs     = 0;
        int  diagGcStart0       = System.GC.CollectionCount(0);
        int  diagGcStart1       = System.GC.CollectionCount(1);
        int  diagGcStart2       = System.GC.CollectionCount(2);
        var  diagWaitWatch      = new System.Diagnostics.Stopwatch();
        var  diagIterWatch      = new System.Diagnostics.Stopwatch();

        while (!ct.IsCancellationRequested)
        {
            diagWaitWatch.Restart();
            NativeMethods.WaitForSingleObject(hEvent, 100);
            long diagWaitMs = diagWaitWatch.ElapsedMilliseconds;

            diagIterWatch.Restart();
            diagIterationCount++;

            int bufferDoneCount = 0;
            for (int i = 0; i < nBuffers; i++)
            {
                WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    bufferDoneCount++;
                    if (hdr.dwBytesRecorded == 0)
                    {
                        log.Warning(LogSource.Capture, $"empty buffer | index={i}");
                    }
                    else
                    {
                        var data = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                        allBytes.AddRange(data);
                        EmitSubWindows(data, rmsLog, audioLevelCallback);
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
                            double bufferDbfs = PcmConversion.ComputeBufferDbfs(data);
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
                            // Technical log stays here; the localized
                            // UserFeedback overlay (Engine_LowAudio_Title /
                            // Body) is emitted by the orchestrator via the
                            // onLowAudioDetected callback so Capture stays
                            // free of any Loc.Get dependency.
                            log.Warning(
                                LogSource.Capture,
                                $"low audio detected | recording_ms={recordingMs} | no healthy voice ≥{NormalVoiceSustainedMs} ms above {NormalVoiceDbfsThreshold} dBFS");
                            onLowAudioDetected?.Invoke();
                        }
                    }

                    hdr.dwFlags &= ~WHDR_DONE;
                    Marshal.StructureToPtr(hdr, hdrPtrs[i], fDeleteOld: false);
                    NativeMethods.waveInAddBuffer(hWaveIn, hdrPtrs[i], hdrSize);
                }
            }

            // Capture lag — fire once per recording when the ring buffer is
            // really under pressure. With 4 buffers × 50 ms and a 100 ms
            // wait, finding 1-2 buffers WHDR_DONE per iteration is normal;
            // 3+ means the consumer fell at least 150 ms behind the producer.
            //
            // TEMP DIAG fields decode the cause:
            //   iter         — iteration index when the lag fires (low → cold-start)
            //   wait_ms      — time spent in WaitForSingleObject (high → GC pause / CPU preemption during sleep)
            //   prev_iter_ms — time the previous scan loop took (high → heavy inline work let buffers pile up)
            //   gcN          — GC count delta from start of recording (gen1/gen2 bump → STW pause)
            if (!captureLagWarned && bufferDoneCount >= 3)
            {
                captureLagWarned = true;
                int diagGcNow0 = System.GC.CollectionCount(0);
                int diagGcNow1 = System.GC.CollectionCount(1);
                int diagGcNow2 = System.GC.CollectionCount(2);
                log.Warning(LogSource.Capture,
                    $"capture lag | buffers_ready={bufferDoneCount} iter={diagIterationCount} wait_ms={diagWaitMs} prev_iter_ms={diagLastIterMs} gc0={diagGcStart0}->{diagGcNow0} gc1={diagGcStart1}->{diagGcNow1} gc2={diagGcStart2}->{diagGcNow2}");
            }

            // Duration cap — forces a stop as if the user had pressed the
            // hotkey. Audio captured so far still flows through the full
            // pipeline. Only triggers once per session.
            //
            // The orchestrator's user-driven Stop path arrives via the
            // CancellationToken; the cap-hit branch must escape the loop by
            // setting `capHit` and breaking, since we no longer share a
            // single _stopFlag with the orchestrator. The orchestrator
            // observes Exit == LoopExit.CapHit on return and runs its
            // Recording → Stopping CAS itself.
            double curSec = allBytes.Count / 32000.0;
            if (!capHit && maxDurationSec > 0 && curSec >= maxDurationSec)
            {
                capHit = true;
                log.Warning(LogSource.Capture,
                    $"duration cap reached | audio_sec={curSec:F1} | cap_sec={maxDurationSec}");
                diagLastIterMs = diagIterWatch.ElapsedMilliseconds;
                break;
            }

            diagLastIterMs = diagIterWatch.ElapsedMilliseconds;
        }

        // Drain phase starts here — measured separately from the in-loop
        // recording time so the LatencyPayload can show how much of
        // StopToPipelineMs is spent draining the mic alone (the 100 ms guard
        // sleep below is the obvious lower bound).
        var drainSw = System.Diagnostics.Stopwatch.StartNew();

        NativeMethods.waveInStop(hWaveIn);
        Thread.Sleep(100);

        for (int i = 0; i < nBuffers; i++)
        {
            WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[i]);
            if ((hdr.dwFlags & WHDR_DONE) != 0 && hdr.dwBytesRecorded > 0)
            {
                var data = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, data, 0, (int)hdr.dwBytesRecorded);
                allBytes.AddRange(data);
                // Push the drained tail through the same sub-window mill so
                // _rmsLog covers the full session (the in-loop EmitSubWindows
                // path stops as soon as the cancellation token fires, leaving
                // the last 1-3 buffers undrained without this explicit pass).
                EmitSubWindows(data, rmsLog, audioLevelCallback);
            }
            NativeMethods.waveInUnprepareHeader(hWaveIn, hdrPtrs[i], hdrSize);
        }

        drainSw.Stop();

        return new PumpResult(
            Pcm:             allBytes.ToArray(),
            Exit:            capHit ? LoopExit.CapHit : LoopExit.StopRequested,
            BuffersReceived: buffersReceived,
            DrainDuration:   drainSw.Elapsed);
    }

    // waveIn delivers 50ms PCM16 buffers (BYTES_PER_50MS = 1600 bytes); the
    // sub-window walker below loops at most once per call but keeps the
    // pattern in case the buffer size changes. AudioLevel fires at ~20 Hz —
    // fine enough for a smooth contour animation without swamping
    // subscribers. RMS is linear [0, 1], clamped so a rare overshoot from
    // quantization never escapes the range.
    //
    // Collect side-effect: every sub-window RMS is appended to rmsLog. The
    // accumulation runs unconditionally (independent of audioLevelCallback
    // subscription) so the Stop-time mic-telemetry summary reflects the
    // entire session even when the HUD isn't listening.
    private static void EmitSubWindows(
        byte[]                pcm16,
        List<float>           rmsLog,
        System.Action<float>? audioLevelCallback)
    {
        const int SubWindowMs      = 50;
        const int BytesPerSubWin   = 16000 * 2 * SubWindowMs / 1000; // 1600 bytes
        const int SamplesPerSubWin = BytesPerSubWin / 2;             // 800 samples

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
            double rms = System.Math.Sqrt(sumSq / SamplesPerSubWin);
            if (rms > 1.0) rms = 1.0;
            float rmsF = (float)rms;
            rmsLog.Add(rmsF);
            audioLevelCallback?.Invoke(rmsF);
            offset += BytesPerSubWin;
        }
    }
}
