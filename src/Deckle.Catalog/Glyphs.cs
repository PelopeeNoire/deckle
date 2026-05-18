namespace Deckle.Catalog;

// ─── Glyphs ───────────────────────────────────────────────────────
//
// C# mirror of Themes/Icons.xaml. Code-behind that sets FontIcon.Glyph
// programmatically uses these constants instead of literal "\uXXXX"
// strings. Any time a key is added here, the matching entry must be
// added to Icons.xaml (and vice-versa) — the two artefacts are kept
// in sync by hand.
//
// Nested static classes mirror the dotted naming used in the XAML
// keys (Icon.Badge.Success → Glyphs.Badge.Success).
//
// Constants hold the actual Fluent Icons character (one per glyph).
// To look up the hex code (e.g. when matching a Figma spec), check
// the matching key in Themes/Icons.xaml which uses the explicit
// &#xE…; notation. In editors without the Fluent Icons font the
// characters render as boxes — that's expected.

public static class Glyphs
{
    // Navigation / Section headers
    public const string Home = "";
    public const string Microphone = "";
    public const string Speech = "";
    public const string Sparkle = "";
    public const string Lightbulb = "";
    public const string Diagnostics = "";
    public const string Logs = "";

    // Common semantic concepts
    public const string Shortcut = "";
    public const string Theme = "";
    public const string Overlay = "";
    public const string Model = "";
    public const string Paste = "";
    public const string Launch = "";
    public const string Lightning = "";
    public const string Setup = "";
    public const string Folder = "";
    public const string Speaker = "";
    public const string Latency = "";

    // Common actions
    public const string Reset = "";
    public const string Search = "";
    public const string Copy = "";
    public const string Save = "";
    public const string Delete = "";
    public const string Export = "";
    public const string Close = "";
    public const string Download = "";
    public const string Refresh = "";
    public const string OpenExternal = "";
    public const string Cancel = "";

    // Whisper transcription specifics
    public const string Language = "";
    public const string Prompt = "";
    public const string Gpu = "";
    public const string Filter = "";
    public const string Pattern = "";
    public const string Context = "";
    public const string Tokens = "";
    public const string Tuning = "";

    // Diagnostics specifics
    public const string AppLog = "";
    public const string AudioRecording = "";
    public const string VoiceLevel = "";

    // Ambient / Hue specifics
    public const string Bridge = "";
    public const string Endpoint = "";
    public const string Link = "";
    public const string List = "";

    // Lightbulb variant — same code-point as Launch but a clearer name
    // when the lightbulb metaphor is the design intent (Hue Identify
    // button in the Playground, etc.).
    public const string LightbulbFilled = "";

    public static class Transport
    {
        public const string Play = "";
        public const string Pause = "";
        public const string Stop = "";
    }

    public static class Badge
    {
        public const string Success = "";
        public const string Critical = "";
        public const string Warning = "";
        public const string Info = "";
    }
}
