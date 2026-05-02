namespace Deckle.Capture.Internal;

// PCM16 mono ↔ float helpers shared between the polling loop and the
// telemetry calculator. All functions are pure; they never touch waveIn
// state. `internal` because Capture is the only consumer — the warmup
// path inside WhispEngine loads its priming WAV via TryLoadWarmupClip,
// not through this conversion.
internal static class PcmConversion
{
    // PCM16 little-endian → float [-1, 1]. Allocates a single output array
    // sized to the sample count. Used at the very end of MicrophoneCapture
    // .Record to hand a flat float buffer to whisper_full.
    public static float[] PcmToFloat(byte[] pcm)
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

    // Linear RMS → dBFS. Floors at -120 dBFS for a pure-zero buffer so the
    // log domain never sees 0 (the conventional "digital silence" floor —
    // the -96.7 dBFS we used to see corresponded to a single-LSB residual
    // from a zero-initialised buffer, indistinguishable from true silence
    // and historically misleading).
    public static double ToDbfs(float linear) =>
        linear > 0f ? 20.0 * System.Math.Log10(linear) : -120.0;

    // Full-buffer dBFS — single pass over a PCM16 mono buffer, returns the
    // 20*log10(rms) value floored at -120 dBFS for a pure-zero buffer (so the
    // log domain never sees 0). Used by the live low-audio tracker in the
    // recording polling loop; intentionally coarse (whole-buffer average
    // rather than per sub-window) because the tracker is already running
    // at buffer cadence and we don't need finer granularity for a "did it
    // stay quiet for 5 s?" check.
    public static double ComputeBufferDbfs(byte[] pcm16)
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
        double rms = System.Math.Sqrt(sumSq / nSamples);
        return rms > 0 ? 20.0 * System.Math.Log10(rms) : -120.0;
    }
}
