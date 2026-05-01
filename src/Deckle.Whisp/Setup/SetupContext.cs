using System.Collections.Generic;

namespace Deckle.Whisp.Setup;

// ── SetupContext ─────────────────────────────────────────────────────────────
//
// State shared across the wizard's pages. Each page reads its initial
// state from here, mutates user choices, and (in the install page) writes
// back the per-item results. The Window observes this object to enable/
// disable Next, format the summary on the final page, etc.
//
// Plain POCO — no Action delegates for navigation. Pages call
// `Frame.Navigate(typeof(NextPage), context)` directly, which keeps the
// setup classes free of UI types.
public sealed class SetupContext
{
    // Where to install. Defaults to whatever AppPaths resolved at start-up
    // (LOCALAPPDATA or env-var override). The wizard's location section
    // doesn't yet support changing this in-process — a custom path needs
    // an app restart in V1, so this stays read-only after construction.
    public string Location { get; init; } = AppPaths.UserDataRoot;

    // Speech model the user picked in the Choices page. Defaults to the
    // catalog's default model so the page can pre-select it.
    public ModelEntry SelectedModel { get; set; } = SpeechModels.DefaultWhisperModel;

    // True after the user has clicked Install on the Choices page — gates
    // the transition to the Installing page.
    public bool ChoicesConfirmed { get; set; }

    // Per-item results captured by the Installing page, displayed on the
    // Summary page. Populated in order: native runtime first, then the
    // chosen model, then the VAD model.
    public List<InstallResult> Results { get; } = new();

    // True when every Results entry is Success — drives the Summary page's
    // success vs error rendering.
    public bool AllSucceeded
    {
        get
        {
            foreach (var r in Results) if (!r.Success) return false;
            return Results.Count > 0;
        }
    }
}

// ── InstallResult ────────────────────────────────────────────────────────────
//
// One row on the Summary page. Captures what the wizard tried, whether
// it worked, and (on failure) the human-readable reason. Bytes is the
// installed size after success — null for native runtime entries that
// only count files.
public sealed record InstallResult(
    string ItemId,
    string DisplayName,
    bool Success,
    string? ErrorMessage,
    long? Bytes);
