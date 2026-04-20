using System;
using System.IO;

namespace WhispUI.Logging;

// ── WavCorpusLog ────────────────────────────────────────────────────────────
//
// Opt-in capture of the raw 16 kHz mono PCM audio fed to whisper_full,
// one WAV per transcription. Pairs with CorpusLog (text JSONL) so that
// offline benchmark runs can replay both sides — model output vs the
// actual audio that produced it. Target directory is a corpus-audio/
// subfolder of whatever CorpusLog resolves to, and each profile owns
// its own slugged subfolder so audio samples stay sliceable per
// workflow the same way text samples are.
//
// Audio is materialized as 16-bit signed PCM at 16 kHz mono. That's
// the exact input whisper.cpp consumed (we convert once from the
// native float[-1,1] signal), which keeps the sample listen-able in
// any WAV viewer and roughly halves disk footprint versus writing
// 32-bit float WAV.
//
// Separate opt-in from the text corpus because audio of the user's
// voice is a more sensitive data class than the transcript. The
// toggle gates in GeneralPage, the dedicated consent dialog
// spells out the audio dimension.
//
// Fail-soft: any IO error is swallowed so corpus capture can never
// take down the pipeline.
internal static class WavCorpusLog
{
    private const int SampleRate = 16_000;
    private const short BitsPerSample = 16;
    private const short NumChannels = 1;
    private const string AudioSubfolder = "corpus-audio";

    public static void Append(string slugPrefix, float[] audio, DateTimeOffset timestamp)
    {
        if (audio is null || audio.Length == 0) return;
        if (string.IsNullOrWhiteSpace(slugPrefix)) return;

        string? root = CorpusLog.GetDirectoryPath();
        if (root is null) return;

        try
        {
            string profileDir = Path.Combine(root, AudioSubfolder, Sanitize(slugPrefix));
            Directory.CreateDirectory(profileDir);

            // Millisecond-precision timestamp in filename keeps ordering
            // unambiguous even with fast back-to-back transcriptions.
            string stamp = timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(profileDir, stamp + ".wav");
            WritePcm16(path, audio);
        }
        catch
        {
            // Capture must never break the transcription.
        }
    }

    private static void WritePcm16(string path, float[] audio)
    {
        int byteRate   = SampleRate * NumChannels * (BitsPerSample / 8);
        short blockAlign = (short)(NumChannels * (BitsPerSample / 8));
        int dataBytes  = audio.Length * (BitsPerSample / 8);
        int riffSize   = 36 + dataBytes;

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

        // Convert float [-1, 1] → int16 with clamp. The audio we get
        // from the recording path is already in that range; clamping
        // defends against the occasional out-of-range sample.
        for (int i = 0; i < audio.Length; i++)
        {
            float s = audio[i];
            if (s >  1f) s =  1f;
            if (s < -1f) s = -1f;
            short v = (short)(s * short.MaxValue);
            bw.Write(v);
        }
    }

    private static string Sanitize(string s)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            s = s.Replace(invalid, '-');
        return s;
    }
}
