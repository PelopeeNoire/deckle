using System.Text.RegularExpressions;

namespace Deckle.Logging;

// Whitespace-tolerant word counter. Every maximal run of non-whitespace
// counts as one word — resilient to multiple spaces, tabs, CRLFs, mixed
// punctuation. No locale/NLP assumptions.
internal static class TextMetrics
{
    private static readonly Regex _token = new(@"\S+", RegexOptions.Compiled);

    public static int CountWords(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : _token.Matches(text).Count;
}
