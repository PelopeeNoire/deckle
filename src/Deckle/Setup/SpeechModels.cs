using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Deckle.Setup;

// ── ModelEntry ───────────────────────────────────────────────────────────────
//
// One Whisper or Silero model the wizard knows how to install. Url + Sha256
// + SizeBytes drive the downloader; Url empty means the entry can only be
// satisfied by a local copy. SizeBytes is nominal — used to size the
// progress bar and budget the disk estimate, not for verification.
internal sealed record ModelEntry(
    string Id,
    string FileName,
    string DisplayName,
    string Url,
    long SizeBytes,
    string? Sha256 = null);

// ── SpeechModels ─────────────────────────────────────────────────────────────
//
// **Single source of truth** for every speech model the app understands.
// WhispEngine, the wizard, Settings — they all read filenames, default
// IDs, and download URLs from this catalog instead of hard-coding them.
//
// V1 catalog: two Whisper models (base, large-v3) plus the mandatory
// Silero VAD model. SHA-256 fields are placeholders — HuggingFace doesn't
// publish a canonical hash format compatible with our verifier yet.
// They'll be filled when the redist pipeline computes them.
internal static class SpeechModels
{
    // Default Whisper model the engine targets when no override is set.
    // Single source of truth — WhispEngine reads this rather than its
    // own copy of the filename. Swap it here when bumping the default.
    public const string DefaultModelFileName = "ggml-large-v3.bin";

    // VAD model used by SpeechDetection. Required for voice activity
    // detection inside whisper_full when SpeechDetection.Enabled = true.
    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";

    public static IReadOnlyList<ModelEntry> WhisperModels { get; } = new[]
    {
        new ModelEntry(
            Id:          "whisper-base",
            FileName:    "ggml-base.bin",
            DisplayName: "Whisper base — multilingual, fast (~150 MB)",
            Url:         "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            SizeBytes:   147_964_211L),
        new ModelEntry(
            Id:          "whisper-large-v3",
            FileName:    DefaultModelFileName,
            DisplayName: "Whisper large-v3 — multilingual, best accuracy (~3 GB)",
            Url:         "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin",
            SizeBytes:   3_094_623_691L),
    };

    public static ModelEntry VadModel { get; } = new(
        Id:          "silero-vad",
        FileName:    VadModelFileName,
        DisplayName: "Silero VAD",
        Url:         "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin",
        SizeBytes:   716_800L);

    // Catalog handle for the engine's default model. The wizard surfaces
    // this as the pre-selected radio in the Choices page.
    public static ModelEntry DefaultWhisperModel =>
        WhisperModels.First(m => m.FileName == DefaultModelFileName);

    public static bool IsInstalled(ModelEntry entry)
    {
        try
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, entry.FileName);
            if (!File.Exists(path)) return false;
            return new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDefaultInstalled() => IsInstalled(DefaultWhisperModel);

    public static bool IsVadInstalled() => IsInstalled(VadModel);
}
