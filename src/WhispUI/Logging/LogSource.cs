namespace WhispUI.Logging;

// Typed source constants — closed vocabulary. No magic strings in caller code.
// Brackets are added by LogEntry.Text formatting, so values must NOT include
// their own brackets. Uppercase, ASCII, dot-separated for sub-components.
// Max ~12 chars for visual alignment in the LogWindow.
public static class LogSource
{
    // ── App lifecycle ────────────────────────────────────────────────────────
    public const string App    = "APP";
    public const string Status = "STATUS";
    public const string Crash  = "CRASH";

    // ── Engine pipeline ──────────────────────────────────────────────────────
    public const string Hotkey     = "HOTKEY";
    public const string Model      = "MODEL";
    public const string Init       = "INIT";
    public const string Record     = "RECORD";
    public const string Transcribe = "TRANSCRIBE";
    public const string Callback   = "CALLBACK";
    public const string Clipboard  = "CLIPBOARD";
    public const string Paste      = "PASTE";
    public const string Done       = "DONE";
    public const string Whisper    = "WHISPER";

    // ── Services ─────────────────────────────────────────────────────────────
    public const string Llm = "LLM";

    // ── Settings pages ───────────────────────────────────────────────────────
    public const string Settings   = "SETTINGS";
    public const string SetGeneral = "SET.GENERAL";
    public const string SetWhisper = "SET.WHISPER";
    public const string SetLlm     = "SET.LLM";

    // ── UI windows ───────────────────────────────────────────────────────────
    public const string LogWin = "LOGWIN";
    public const string Hud    = "HUD";
    public const string Tray   = "TRAY";
}
