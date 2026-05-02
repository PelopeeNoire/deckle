using Microsoft.UI.Xaml;
using Deckle.Capture;

namespace Deckle.Settings;

// ── SettingsHost ──────────────────────────────────────────────────────────
//
// App-side hooks the Settings UI pages and ViewModels need to drive
// concerns that only the App owns: theme broadcast across windows, the
// canonical AudioLevelMapper statics in Deckle.Capture (touched by the
// level-window slider), process restart, and the lazy SettingsWindow
// instance accessor used by dialogs to anchor their XamlRoot.
//
// Why a static delegate registry rather than a project reference back
// to the App assembly? Because that would close the dependency cycle
// (App → Deckle.Settings → App). The pattern mirrors what
// `HudChrono.MaxRecordingDurationSecondsProvider` does in
// Deckle.Chrono.Hud: the lib exposes static fields, the App wires them
// once at boot, the lib's call sites invoke them with `?.Invoke(...)`
// and degrade silently to no-op when nothing is wired (so the lib
// remains buildable / testable in isolation).
//
// All four hooks are intentionally `Action<...>` / `Func<...>` rather
// than a single interface — keeps the surface minimal, no boxing, and
// each hook can be wired independently if a future host implements only
// part of the contract (e.g. a settings preview window without a full
// app shell).
public static class SettingsHost
{
    // Broadcast the requested theme ("Light" | "Dark" | "System") to
    // every long-lived window the host tracks. Wired by App in
    // OnLaunched; no-op until then.
    public static Action<string>? ApplyTheme;

    // Push the new level-window curve into Capture.AudioLevelMapper
    // so the HUD reflects it live (the mapper is consulted from the
    // capture loop on every audio frame). Wired by App in OnLaunched.
    public static Action<LevelWindowSettings>? ApplyLevelWindow;

    // Restart the process, optionally returning to a Settings page
    // tag (e.g. "Deckle.Settings.WhisperPage") so the user lands back
    // on the page that triggered the restart. Wired by App.
    public static Action<string?>? RestartApp;

    // Accessor for the currently-open SettingsWindow so dialogs can
    // anchor their `XamlRoot` and resolve the parent hwnd. Returns
    // null when the window hasn't been lazily created yet.
    public static Func<Window?>? GetSettingsWindow;

    // Re-open the first-run setup wizard on demand (Browse model,
    // replace native runtime…). The wizard XAML and code live in the
    // App assembly (namespace Deckle.Shell.Setup) until they get
    // factored into a dedicated module — so we go through a hook here
    // to avoid taking a back-reference to the App.
    public static Action? OpenSetupWizard;
}
