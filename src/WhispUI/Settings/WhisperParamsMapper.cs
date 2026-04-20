using System;
using System.IO;
using System.Runtime.InteropServices;
using WhispUI.Interop;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ── WhisperParamsMapper ───────────────────────────────────────────────────────
//
// Pont entre AppSettings (modèle orienté utilisateur) et WhisperFullParams
// (struct C native). Appelé par WhispEngine.Transcribe() juste avant
// whisper_full(), après que la struct a été initialisée via
// whisper_full_default_params_by_ref().
//
// Ne touche QUE les champs hot-reload : tout ce qui n'exige pas de relancer
// le contexte whisper_init. Le choix du modèle et use_gpu sont appliqués
// séparément au moment du LoadModelAsync() (voir WhispEngine._modelPath).
//
// Allocations non managées retournées : le caller est responsable de les
// libérer après whisper_full() via FreeAllocations().
internal static class WhisperParamsMapper
{
    private static readonly LogService _log = LogService.Instance;

    internal readonly struct NativeAllocations
    {
        public readonly IntPtr Language;
        public readonly IntPtr InitialPrompt;
        public readonly IntPtr SuppressRegex;
        public readonly IntPtr VadModelPath;

        public NativeAllocations(IntPtr lang, IntPtr prompt, IntPtr regex, IntPtr vadPath)
        {
            Language = lang;
            InitialPrompt = prompt;
            SuppressRegex = regex;
            VadModelPath = vadPath;
        }

        public void Free()
        {
            if (Language != IntPtr.Zero) Marshal.FreeHGlobal(Language);
            if (InitialPrompt != IntPtr.Zero) Marshal.FreeHGlobal(InitialPrompt);
            if (SuppressRegex != IntPtr.Zero) Marshal.FreeHGlobal(SuppressRegex);
            if (VadModelPath != IntPtr.Zero) Marshal.FreeHGlobal(VadModelPath);
        }
    }

    // Applique les paramètres utilisateur sur la struct native. La struct
    // est passée par ref : on écrase les champs concernés, on laisse les
    // autres (strategy, callbacks, n_threads...) tels que whisper.cpp les
    // a initialisés par défaut.
    public static NativeAllocations Apply(ref WhisperFullParams wparams, AppSettings s)
    {
        // ── Transcription ─────────────────────────────────────────────────
        IntPtr langPtr = Marshal.StringToHGlobalAnsi(s.Transcription.Language);
        IntPtr promptPtr = Marshal.StringToHGlobalAnsi(s.Transcription.InitialPrompt);
        wparams.language = langPtr;
        wparams.initial_prompt = promptPtr;
        wparams.carry_initial_prompt = (byte)(s.Transcription.CarryInitialPrompt ? 1 : 0);

        // ── Seuils de confiance ───────────────────────────────────────────
        wparams.entropy_thold = (float)s.Confidence.EntropyThreshold;
        wparams.logprob_thold = (float)s.Confidence.LogprobThreshold;
        wparams.no_speech_thold = (float)s.Confidence.NoSpeechThreshold;

        // ── Décodage ──────────────────────────────────────────────────────
        wparams.temperature = (float)s.Decoding.Temperature;
        wparams.temperature_inc = (float)s.Decoding.TemperatureIncrement;

        // Beam search: strategy 1 = WHISPER_SAMPLING_BEAM_SEARCH.
        // Explores multiple decoding paths and keeps the best overall
        // sequence. Better quality than greedy (strategy 0), slower.
        if (s.Decoding.UseBeamSearch)
        {
            wparams.strategy = 1;
            wparams.beam_search_beam_size = s.Decoding.BeamSize;
        }

        // ── Filtres de sortie ─────────────────────────────────────────────
        wparams.suppress_blank = (byte)(s.OutputFilters.SuppressBlank ? 1 : 0);
        wparams.suppress_nst = (byte)(s.OutputFilters.SuppressNonSpeechTokens ? 1 : 0);

        IntPtr regexPtr = IntPtr.Zero;
        if (!string.IsNullOrEmpty(s.OutputFilters.SuppressRegex))
        {
            regexPtr = Marshal.StringToHGlobalAnsi(s.OutputFilters.SuppressRegex);
            wparams.suppress_regex = regexPtr;
        }

        // ── Contexte et segmentation ──────────────────────────────────────
        // UseContext (UI) = inverse de no_context (natif).
        wparams.no_context = (byte)(s.Context.UseContext ? 0 : 1);
        // MaxTokens <= 0 means "auto" — leave whisper.cpp's default (16384).
        // Writing -1 here makes whisper.cpp compute
        // max_prompt_ctx = min(-1, n_text_ctx/2) = -1 then clamp the initial
        // prompt to 1 token, surfacing a confusing "initial prompt is too long"
        // warning on every transcription.
        if (s.Context.MaxTokens > 0)
            wparams.n_max_text_ctx = s.Context.MaxTokens;

        // ── VAD ───────────────────────────────────────────────────────────
        IntPtr vadPathPtr = IntPtr.Zero;
        wparams.vad = (byte)(s.SpeechDetection.Enabled ? 1 : 0);

        if (s.SpeechDetection.Enabled)
        {
            // Modèle Silero cherché dans le dossier de modèles résolu par le
            // SettingsService (ModelsDirectory utilisateur ou fallback shared/).
            // Si absent, VAD désactivé avec log warning — pas de crash natif.
            string vadModelPath = Path.Combine(
                SettingsService.Instance.ResolveModelsDirectory(), "ggml-silero-v6.2.0.bin");

            if (File.Exists(vadModelPath))
            {
                vadPathPtr = Marshal.StringToHGlobalAnsi(vadModelPath);
                wparams.vad_model_path = vadPathPtr;
                wparams.vad_threshold = s.SpeechDetection.Threshold;
                wparams.vad_min_speech_duration_ms = s.SpeechDetection.MinSpeechDurationMs;
                wparams.vad_min_silence_duration_ms = s.SpeechDetection.MinSilenceDurationMs;
                wparams.vad_max_speech_duration_s = s.SpeechDetection.MaxSpeechDurationSec;
                wparams.vad_speech_pad_ms = s.SpeechDetection.SpeechPadMs;
                wparams.vad_samples_overlap = s.SpeechDetection.SamplesOverlap;
            }
            else
            {
                wparams.vad = 0;
                _log.Warning(LogSource.Whisper,
                    $"Silero VAD model not found at {vadModelPath} — VAD disabled. " +
                    $"Download from https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin");
            }
        }

        return new NativeAllocations(langPtr, promptPtr, regexPtr, vadPathPtr);
    }
}
