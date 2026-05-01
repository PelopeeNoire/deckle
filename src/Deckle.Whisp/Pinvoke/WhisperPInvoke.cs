using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Deckle.Interop;

namespace Deckle.Whisp.Pinvoke;

// ── WhisperPInvoke ──────────────────────────────────────────────────────────
//
// Whisper.cpp P/Invokes plus a managed DllImportResolver that loads
// libwhisper.dll from <UserDataRoot>\native\ rather than from the application
// binary directory. Both pieces are Whisp-domain — they leave Deckle.Core
// unburdened of Whisper-specific knowledge while keeping the resolver wired
// to THIS assembly so the [DllImport("libwhisper")] attributes below resolve
// correctly.
//
// libwhisper.dll and its transitive ggml-*.dll dependencies live under
// <UserDataRoot>\native\, NOT alongside the application binary. The
// initializer wires a managed resolver that loads libwhisper from that
// directory; Windows then resolves the ggml-*.dll dependencies from the same
// directory automatically (DLL load order: directory of the loaded DLL first).
//
// Runs on first access to any WhisperPInvoke member, before any
// [DllImport(PInvokeKey)] P/Invoke is executed — guaranteed by the CLR's
// static-constructor contract.
//
// The PInvokeKey constant below MUST stay in sync with the literal in every
// [DllImport("libwhisper")] attribute. C# requires a constant literal in the
// attribute, so the duplication is unavoidable — keep PInvokeKey as the
// documented match-target.
//
// Falls through to default resolution when NativeDirectory doesn't hold
// the DLLs yet: the first-run wizard catches the missing-deps state and
// prompts the user before the engine boots, so a DllNotFoundException
// shouldn't happen in practice. The fallback exists for the edge case
// where the wizard is bypassed (env var override, manual DLL placement
// next to the exe in dev).
public static class WhisperPInvoke
{
    private const string PInvokeKey = "libwhisper";
    private const string EntryDll   = "libwhisper.dll";

    static WhisperPInvoke()
    {
        NativeLibrary.SetDllImportResolver(typeof(WhisperPInvoke).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != PInvokeKey) return IntPtr.Zero;

        string candidate = Path.Combine(AppPaths.NativeDirectory, EntryDll);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            return handle;

        return IntPtr.Zero;
    }

    // ── libwhisper.dll ────────────────────────────────────────────────────────

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_context_default_params_by_ref();

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_context_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_init_from_file_with_params(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        WhisperContextParams cparams);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_default_params_by_ref(int strategy);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free_params(IntPtr ptr);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full(IntPtr ctx, WhisperFullParams wparams, float[] samples, int n_samples);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_segments(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t0(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern long whisper_full_get_segment_t1(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern float whisper_full_get_segment_no_speech_prob(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_n_tokens(IntPtr ctx, int i_segment);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_full_get_token_id(IntPtr ctx, int i_segment, int i_token);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern float whisper_full_get_token_p(IntPtr ctx, int i_segment, int i_token);

    // Renvoie l'id à partir duquel les tokens sont des timestamps (<|0.00|>, <|5.30|>…).
    // Tout token dont l'id est >= à cette valeur est un token de timestamp, pas du texte.
    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern int whisper_token_beg(IntPtr ctx);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_free(IntPtr ctx);

    // ── whisper_log_set : callback global pour les logs internes ──────────────
    //
    // whisper.cpp émet en permanence des lignes de log (chargement modèle,
    // démarrage du décodage, métriques GPU, timings, etc.). Par défaut elles
    // partent sur stderr où elles sont perdues. En branchant un callback on
    // redirige tout ça vers la LogWindow.
    //
    // Signature C : void (*)(enum ggml_log_level level, const char *text, void *user_data)
    // Niveaux ggml_log_level : 0=None, 1=Info, 2=Warn, 3=Error, 4=Debug, 5=Cont.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void WhisperLogCallback(int level, IntPtr text, IntPtr user_data);

    [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
    public static extern void whisper_log_set(WhisperLogCallback log_callback, IntPtr user_data);
}
