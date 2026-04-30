using System;
using System.IO;

namespace Deckle.Logging;

// ── WavCorpusWriter ─────────────────────────────────────────────────────────
//
// Binary side of corpus capture. Writes the raw 16 kHz mono PCM audio fed
// to whisper_full as a 16-bit signed WAV, one file per transcription,
// under <telemetry-root>/<slug>/audio/<timestamp>.wav. The slug is the
// profile identity; the audio folder lives inside it, next to the paired
// corpus.jsonl.
//
// Why 16-bit int PCM and not 32-bit float: the engine hands us float
// [-1, 1] samples (the exact input whisper.cpp consumed). Quantizing to
// int16 keeps the file listen-able in any WAV viewer and roughly halves
// disk footprint — offline re-transcription accepts either just fine.
//
// Called as a helper (not a sink) because the output path needs to feed
// back into the CorpusPayload.AudioFile slot on the paired JSONL event.
// Returns the relative path (audio/<stamp>.wav) on success so the corpus
// line stays portable — consumers resolve it against the profile directory
// that holds corpus.jsonl. Null on any failure — callers surface "no audio
// file" in the payload instead of crashing.
internal static class WavCorpusWriter
{
    private const int    SampleRate    = 16_000;
    private const short  BitsPerSample = 16;
    private const short  NumChannels   = 1;
    private const string AudioSubfolder = "audio";

    public static string? Write(string slugPrefix, float[] audio, DateTimeOffset timestamp)
    {
        if (audio is null || audio.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(slugPrefix)) return null;

        string root = CorpusPaths.GetDirectoryPath();

        try
        {
            string profileDir = Path.Combine(root, CorpusPaths.Sanitize(slugPrefix), AudioSubfolder);
            Directory.CreateDirectory(profileDir);

            // Millisecond-precision stamp in the filename: back-to-back
            // transcriptions stay ordered unambiguously and the name can
            // be joined directly to the paired CorpusPayload timestamp.
            string stamp = timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff");
            string fileName = stamp + ".wav";
            string path = Path.Combine(profileDir, fileName);
            WritePcm16(path, audio);

            // Relative to <telemetry>/<slug>/ so the corpus.jsonl line
            // documents its own neighbourhood without leaking absolute
            // paths that would break when the benchmark root moves.
            return $"{AudioSubfolder}/{fileName}";
        }
        catch
        {
            // Capture must never break the transcription.
            return null;
        }
    }

    private static void WritePcm16(string path, float[] audio)
    {
        int byteRate    = SampleRate * NumChannels * (BitsPerSample / 8);
        short blockAlign = (short)(NumChannels * (BitsPerSample / 8));
        int dataBytes   = audio.Length * (BitsPerSample / 8);
        int riffSize    = 36 + dataBytes;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        // RIFF header.
        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(riffSize);
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt subchunk — PCM (format code 1).
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(NumChannels);
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(BitsPerSample);

        // data subchunk.
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(dataBytes);

        // float [-1, 1] → int16 with clamp. Recording path already stays
        // in range; the clamp defends against the occasional out-of-band
        // sample that would wrap around on an unchecked cast.
        for (int i = 0; i < audio.Length; i++)
        {
            float s = audio[i];
            if (s >  1f) s =  1f;
            if (s < -1f) s = -1f;
            short v = (short)(s * short.MaxValue);
            bw.Write(v);
        }
    }
}
