using CommunityToolkit.Mvvm.ComponentModel;

namespace Deckle.Playground.ViewModels;

// ─── HUD playground target enum ──────────────────────────────────────────────
//
// Four HUD states (Charging / Recording / Transcribing / Rewriting) and three
// underlying primitives (Conic / ArcMask / Combined) the developer might want
// to inspect in isolation. The DropDownButton in HudPage groups them under
// two sub-menus ; persistence is in-memory only so the enum doesn't need to
// be stable across releases.
public enum HudTarget
{
    Charging,
    Recording,
    Transcribing,
    Rewriting,
    Conic,
    ArcMask,
    Combined,
}

// Logical groups of tuning expanders. The HUD tuning panel is rebuilt
// every time the target changes, showing only the expanders relevant to
// the active target. Parked is always available (collapsed by default)
// so the developer can tweak transition-only defaults regardless of
// what's on the preview stage.
public enum HudTuningSection
{
    Geometry,
    Palette,
    ConicFade,
    HueRotation,
    ArcRotation,
    Swipe,
    Recording,
    Transcribing,
    Rewriting,
    AudioMapping,
    SimulatedRms,
    Parked,
}

// ─── HUD playground ViewModel ────────────────────────────────────────────────
//
// Thin transport + target state. The actual stroke tuning lives in the
// mutable TuningModel POCO directly mutated by the slider lambdas in
// HudPage code-behind (60+ fields, programmatic Slider/NumberBox composite
// — wrapping each one as [ObservableProperty] would add ~3× the code for
// no x:Bind payoff since the panel itself is built programmatically).
//
// The ViewModel exposes only what HudPage.xaml actually binds : the
// current target, the play state, and the computed enable-flags for the
// Play / Pause / Stop transport buttons. Mirrors the GeneralViewModel
// pattern (CommunityToolkit.Mvvm partial properties, _isSyncing guard
// — here named _suppressTargetEvents for the same role) without the
// PushToSettings step since HUD tuning isn't persisted.
public partial class HudViewModel : ObservableObject
{
    // Suppresses change side-effects when the page is programmatically
    // setting the target during initialisation or during a card-click
    // mutual-exclusion sweep. The page sets this true around any
    // bulk-write block, mirrors the `_isSyncing` flag from GeneralViewModel.
    private bool _suppressTargetEvents;

    public bool SuppressTargetEvents
    {
        get => _suppressTargetEvents;
        set => _suppressTargetEvents = value;
    }

    [ObservableProperty]
    public partial HudTarget CurrentTarget { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    public HudViewModel()
    {
        // Default target = Transcribing, mirrors the previous in-memory
        // default that the original PlaygroundWindow seeded. Pause by
        // default so the window opens to an empty preview, the user
        // clicks Play to start.
        CurrentTarget = HudTarget.Transcribing;
        IsPlaying     = false;
    }

    // Computed transport enable-flags. Derived from IsPlaying ; the
    // partial change method below fires change notifications for these
    // so x:Bind picks them up on the toolbar. Two-button transport
    // (Play / Stop) ; Pause was retired in 2026-05-19 because freezing
    // the Composition Forever animations would require new entry points
    // in Deckle.Chrono.Hud and Deckle.Composition, out of scope here.

    public bool IsPlayEnabled => !IsPlaying;
    public bool IsStopEnabled => IsPlaying;

    // Human-readable label shown on the DropDownButton. Same case as the
    // enum members — they read as proper UI labels by design ("Recording",
    // "Conic"…).
    public string CurrentTargetLabel => CurrentTarget.ToString();

    partial void OnCurrentTargetChanged(HudTarget value)
    {
        OnPropertyChanged(nameof(CurrentTargetLabel));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlayEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
    }

    // Forces the preview back to its idle, non-animated state — invoked
    // by the Stop button and by PlaygroundWindow.ShowAndActivate so each
    // window re-show lands on a known state regardless of where the user
    // left it.
    public void ForceStop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
    }

    // Returns the set of tuning expanders relevant to the active target.
    // HudPage's BuildTuningPanel reads this and only creates the matching
    // expanders. The Parked group is appended unconditionally below.
    public IReadOnlyCollection<HudTuningSection> ActiveTuningSections =>
        CurrentTarget switch
        {
            HudTarget.Charging =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                    HudTuningSection.HueRotation,
                    HudTuningSection.ArcRotation,
                    HudTuningSection.Swipe,
                },
            HudTarget.Recording =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                    HudTuningSection.HueRotation,
                    HudTuningSection.Recording,
                    HudTuningSection.AudioMapping,
                    HudTuningSection.SimulatedRms,
                },
            HudTarget.Transcribing =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                    HudTuningSection.HueRotation,
                    HudTuningSection.Transcribing,
                    HudTuningSection.Swipe,
                },
            HudTarget.Rewriting =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                    HudTuningSection.HueRotation,
                    HudTuningSection.Rewriting,
                    HudTuningSection.Swipe,
                },
            HudTarget.Conic =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                },
            HudTarget.ArcMask =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.ArcRotation,
                },
            _ /* Combined */ =>
                new[]
                {
                    HudTuningSection.Geometry,
                    HudTuningSection.Palette,
                    HudTuningSection.ConicFade,
                    HudTuningSection.HueRotation,
                    HudTuningSection.ArcRotation,
                    HudTuningSection.Swipe,
                },
        };
}
