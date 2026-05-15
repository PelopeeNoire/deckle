namespace Deckle.Logging;

// Typed source constants — closed vocabulary. No magic strings in caller code.
// Brackets are added by TelemetryEvent.Text formatting, so values must NOT include
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
    public const string Engine     = "ENGINE";
    public const string Model      = "MODEL";
    public const string Init       = "INIT";
    // Capture (microphone polling, ring buffer, RMS) — emitted from
    // Deckle.Audio. Replaces the legacy "RECORD" value: the module is
    // shared between Whisp and future modules (Ask-Ollama), so the source
    // tag now reflects the capability ("CAPTURE") rather than one
    // orchestrator's intent.
    public const string Capture    = "CAPTURE";
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

    // ── Shell infrastructure ────────────────────────────────────────────────
    public const string MsgHost = "MSGHOST";

    // ── First-run setup wizard ──────────────────────────────────────────────
    public const string Setup = "SETUP";

    // ── Ambient lighting pipeline ───────────────────────────────────────────
    // Screen capture (Windows.Graphics.Capture session, frame pool polling)
    // emitted from Deckle.Vision. Naming follows the same capability-not-
    // module rule as CAPTURE: the source tag points at what the events
    // describe (screen capture), not at the module that owns the code.
    // Once the ambient lighting analysis and driver layers ship, their tags
    // will sit alongside this one (Hue, Wled, Dmx for drivers ; a dedicated
    // tag for the analysis pipeline if its events warrant separation from
    // SCREEN).
    public const string Screen = "SCREEN";

    // Hue bridge driver (cloud discovery, link-button pairing, group list,
    // REST CLIP v1 colour push) emitted from Deckle.Lighting.Hue. Covers
    // the whole life of a Hue REST session ; the Entertainment v2 DTLS
    // path (deferred) will reuse the same source tag since the bridge
    // identity stays the same. Sibling driver tags (Wled, Dmx, HomeAssist)
    // will sit alongside as those land.
    public const string Hue = "HUE";

    // AmbientEngine orchestration (capture frame → analysis → driver
    // push) emitted from Deckle.Lighting.Ambient. The engine itself is
    // protocol-agnostic — it sees an ILightOutput, not a Hue / Wled /
    // HA-specific driver — so this tag tracks the pipeline lifecycle
    // (start, stop, push cadence, frame drops) rather than the wire
    // calls those drivers emit under SCREEN / HUE / future tags.
    public const string Ambient = "AMBIENT";
}
