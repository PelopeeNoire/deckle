using System;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Deckle.Localization;

// ─── Loc ──────────────────────────────────────────────────────────
//
// Single entry point for code-behind string lookups. XAML uses
// `x:Uid="MyKey"` to bind to entries in Strings/en-US/Resources.resw,
// resolved automatically by the WinUI 3 framework. C# code that
// builds UI programmatically (ConsentDialogs, engine status,
// UserFeedback, HUD, tray, setup wizard pages) goes through Loc.
//
// Key naming conventions live in
// docs/reference--localization--0.1.md. Summary:
//
//   x:Uid in XAML       UidValue.Property        e.g. "LogWindowSearchBox.PlaceholderText"
//   C# direct lookup    Surface_Purpose          e.g. "CorpusConsent_Title"
//   C# parameterized    Surface_Purpose_Format   e.g. "Status_Rewriting_Format"
//   Common reusable     Common_Purpose           e.g. "Common_Cancel"
//
// Technical strings (file names, URLs, product names like "Ollama" /
// "Silero VAD" / "Deckle") stay hardcoded — never go through Loc.

internal static class Loc
{
    // Lazy-initialized so the ResourceLoader is built on first use, after
    // the Windows App SDK runtime is bootstrapped in App.OnLaunched.
    // Default constructor binds to the "Resources" map (single monolithic
    // .resw under Strings/<lang>/Resources.resw).
    private static readonly Lazy<ResourceLoader> _loader =
        new(static () => new ResourceLoader());

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>. In
    /// DEBUG builds, missing keys surface as a bracket marker so empty
    /// TextBlocks and button labels can't ship silently. In RELEASE,
    /// the underlying contract (empty string on miss) is preserved.
    /// </summary>
    public static string Get(string key)
    {
        var s = _loader.Value.GetString(key);
#if DEBUG
        if (string.IsNullOrEmpty(s))
        {
            return "[!" + key + "]";
        }
#endif
        return s;
    }

    /// <summary>
    /// Returns the format-string entry for <paramref name="key"/> with
    /// <paramref name="args"/> substituted using
    /// <see cref="CultureInfo.CurrentCulture"/>. The .resw entry is
    /// expected to use composite-format placeholders ({0}, {1}, ...).
    /// </summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
