using System.Runtime.InteropServices;

// INPUT aplati : représente un événement clavier pour SendInput.
//
// La struct Windows INPUT contient une union C (clavier, souris, matériel).
// Ici on l'aplatit pour éviter les problèmes de layout avec struct imbriquée.
//
// Taille totale sur Windows 64 bits = 40 octets :
//   offset  0 : type (uint, 4 octets)
//   offset  4 : padding (4 octets — alignement 8 imposé par les IntPtr dans l'union)
//   offset  8 : début de l'union  ← KEYBDINPUT et MOUSEINPUT partagent cet espace
//   offset 40 : fin
//
// L'union est dimensionnée par MOUSEINPUT (le plus grand membre) :
//   dx(4) + dy(4) + mouseData(4) + dwFlags(4) + time(4) + padding(4) + dwExtraInfo(8) = 32 octets
//
// KEYBDINPUT.dwExtraInfo est à l'offset 16 dans KEYBDINPUT (padding interne après time),
// soit offset 24 dans INPUT (8 + 16).
//
// Le champ _pad à l'offset 32 force Marshal.SizeOf à retourner 40
// (8 octets à partir de l'offset 32 → fin à 40, multiple de 8 ✓).
[StructLayout(LayoutKind.Explicit)]
struct INPUT
{
    [FieldOffset(0)]  public uint   type;
    [FieldOffset(8)]  public ushort ki_wVk;
    [FieldOffset(10)] public ushort ki_wScan;
    [FieldOffset(12)] public uint   ki_dwFlags;
    [FieldOffset(16)] public uint   ki_time;
    [FieldOffset(24)] public IntPtr ki_dwExtraInfo;
    [FieldOffset(32)] public long   _pad;            // padding pour atteindre 40 octets (taille MOUSEINPUT)
}

[StructLayout(LayoutKind.Sequential)]
struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint   nSamplesPerSec;
    public uint   nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
struct WAVEHDR
{
    public IntPtr lpData;           // pointeur vers le buffer de données audio
    public uint   dwBufferLength;   // taille totale du buffer (octets)
    public uint   dwBytesRecorded;  // octets effectivement écrits par le driver
    public IntPtr dwUser;           // donnée utilisateur libre (non utilisé ici)
    public uint   dwFlags;          // flags : WHDR_DONE = buffer rempli par le driver
    public uint   dwLoops;          // nombre de boucles (lecture seulement)
    public IntPtr lpNext;           // usage interne driver
    public IntPtr reserved;         // usage interne driver
}

[StructLayout(LayoutKind.Sequential)]
struct WhisperContextParams
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
struct WhisperFullParams
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
