using System;
using System.Runtime.InteropServices;

namespace Deckle.Interop;

// Whisper.cpp structs — referenced by [DllImport] signatures in
// Deckle.Whisp.Native.WhisperPInvoke. Namespace stays Deckle.Interop to keep
// the cross-module convention consistent (every P/Invoke struct sits there);
// physically they live in Deckle.Whisp.dll because they describe a Whisper-
// specific binary contract.

[StructLayout(LayoutKind.Sequential)]
public struct WhisperContextParams
{
    public byte    use_gpu;
    public byte    flash_attn;
    public int     gpu_device;
    public byte    dtw_token_timestamps;
    public int     dtw_aheads_preset;
    public int     dtw_n_top;
    public UIntPtr dtw_aheads_n_heads;
    public IntPtr  dtw_aheads_heads;
    public UIntPtr dtw_mem_size;
}

[StructLayout(LayoutKind.Sequential)]
public struct WhisperFullParams
{
    public int   strategy;
    public int   n_threads;
    public int   n_max_text_ctx;
    public int   offset_ms;
    public int   duration_ms;
    public byte  translate;
    public byte  no_context;
    public byte  no_timestamps;
    public byte  single_segment;
    public byte  print_special;
    public byte  print_progress;
    public byte  print_realtime;
    public byte  print_timestamps;
    public byte  token_timestamps;
    public float thold_pt;
    public float thold_ptsum;
    public int   max_len;
    public byte  split_on_word;
    public int   max_tokens;
    public byte  debug_mode;
    public int   audio_ctx;
    public byte  tdrz_enable;
    public IntPtr suppress_regex;
    public IntPtr initial_prompt;
    public byte   carry_initial_prompt;
    public IntPtr prompt_tokens;
    public int    prompt_n_tokens;
    public IntPtr language;
    public byte   detect_language;
    public byte  suppress_blank;
    public byte  suppress_nst;
    public float temperature;
    public float max_initial_ts;
    public float length_penalty;
    public float temperature_inc;
    public float entropy_thold;
    public float logprob_thold;
    public float no_speech_thold;
    public int   greedy_best_of;
    public int   beam_search_beam_size;
    public float beam_search_patience;
    public IntPtr new_segment_callback;
    public IntPtr new_segment_callback_user_data;
    public IntPtr progress_callback;
    public IntPtr progress_callback_user_data;
    public IntPtr encoder_begin_callback;
    public IntPtr encoder_begin_callback_user_data;
    public IntPtr abort_callback;
    public IntPtr abort_callback_user_data;
    public IntPtr logits_filter_callback;
    public IntPtr logits_filter_callback_user_data;
    public IntPtr  grammar_rules;
    public UIntPtr n_grammar_rules;
    public UIntPtr i_start_rule;
    public float   grammar_penalty;
    public byte   vad;
    public IntPtr vad_model_path;
    public float vad_threshold;
    public int   vad_min_speech_duration_ms;
    public int   vad_min_silence_duration_ms;
    public float vad_max_speech_duration_s;
    public int   vad_speech_pad_ms;
    public float vad_samples_overlap;
}
