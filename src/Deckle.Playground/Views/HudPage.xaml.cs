using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Audio;
using Deckle.Chrono.Hud;
using Deckle.Composition;
using Deckle.Playground.ViewModels;

namespace Deckle.Playground;

// ─── HUD tuning page ─────────────────────────────────────────────────────────
//
// Owns the HUD stroke preview composition + the programmatic tuning panel.
// Extracted from the monolithic PlaygroundWindow.xaml.cs in 2026-05-19 ;
// behaviour preserved verbatim except for the toolbar refonte (target
// picker is a DropDownButton + flat MenuFlyout, transport is Play / Stop
// with enable-bound state) and the tuning expander list filters by
// HudViewModel.ActiveTuningSections so only knobs relevant to the active
// target appear.
//
// Code is split across partial files :
//   • HudPage.xaml.cs  — fields, ctor, lifecycle, VM observer, target /
//                        transport handlers, ApplyTarget, RMS pump,
//                        OnResetAllClick, BuildTuningPanel dispatcher.
//   • HudPage.Expanders.cs    — one Add/Reset pair per logical knob
//                               group (palette, hue rotation, recording
//                               variant, etc.).
//   • HudPage.RowFactories.cs — programmatic Slider+NumberBox composite
//                               builders and the Expander header helper.
//
// State that survives navigation lives as Page fields here under
// NavigationCacheMode.Required ; PlaygroundWindow disposes the runtime
// composition + timers via DisposeResources at window-close so the
// compositor never holds dangling Forever animations.
//
// Tuning is in-memory only — TuningModel is a mutable POCO captured by
// the slider lambdas. RebuildTuningPanel re-creates the visuals after
// a target switch or a Reset* action ; the captured `_tuning` reference
// stays stable across rebuilds so a single field swap (in OnResetAllClick)
// suffices to point every lambda at the new instance.
public sealed partial class HudPage : Page
{
    public HudViewModel ViewModel { get; } = new();

    // ── Tuning state ─────────────────────────────────────────────────────────
    //
    // Not readonly : OnResetAllClick swaps the whole instance for a fresh
    // `new TuningModel()` to snap fields back to compiled defaults. Every
    // row-factory lambda captures `_tuning` by closure over `this`, so the
    // lambdas see the swapped instance on the next edit — no need to
    // rebuild the panel purely for the swap.
    private TuningModel _tuning = new();

    // Sim RMS defaults chosen to mimic the RMS distribution of normal
    // conversational speech (50 ms window average) :
    //   0.013 linear ≈ -38 dBFS — engine gate threshold, "breath".
    //   0.100 linear ≈ -20 dBFS — normal conversational mid-range.
    private float _simRmsMin           = 0.013f;
    private float _simRmsMax           = 0.100f;
    private float _simRmsPeriodSeconds = 2.0f;
    private bool  _simManualOverride   = false;
    private float _simManualValue      = 0.012f;

    // Simulate "digit changed during Recording" flags for the swipe
    // reveal preview on Transcribing / Rewriting. ON by default because
    // observing the swipe wave is the main reason these targets are
    // selected here.
    private bool _simulateChangedDigits = true;

    // ── Rebuild debounce ────────────────────────────────────────────────
    //
    // Paint-time slider changes rebuild the Win2D surfaces from scratch —
    // one rebuild per DispatcherQueue tick during a drag overloads the
    // CanvasDevice. The debounce fires the actual rebuild 300 ms after
    // the last slider movement.
    private readonly DispatcherTimer _rebuildDebounce = new()
        { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _rebuildPending;

    // ── Simulated RMS pump ──────────────────────────────────────────────
    private readonly DispatcherTimer _rmsTimer = new()
        { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _rmsClock = new();

    // ── Slider ↔ NumberBox sync guard ───────────────────────────────────
    //
    // A two-way composite (Slider + NumberBox sharing a value source)
    // has to guard against feedback loops : setting Slider.Value fires
    // ValueChanged which pushes into NumberBox.Value which fires its
    // own ValueChanged which writes back to Slider. The flag short-
    // circuits the paired event during every programmatic write.
    // Read by row factories in HudPage.RowFactories.cs.
    private bool _syncing;

    // ── Naked preview bundle ────────────────────────────────────────────
    //
    // HudComposition.CreateNakedMaskPreview returns a disposable bundle
    // that wraps the container visual + rotation property sets + brushes
    // + surfaces. We MUST Dispose it before mounting a new one,
    // otherwise every rebuild leaks two Forever ScalarKeyFrameAnimations
    // on the compositor — after enough slider moves the compositor
    // saturates and the rotation freezes mid-animation (the Conic
    // preview regression that was reported). See NakedPreview class
    // comment in HudComposition.
    private HudComposition.NakedPreview? _nakedPreview;

    // Fixed natural footprint of the naked-mask preview bundle. 272×78
    // is the HUD's actual stroke surface ; the bundle is centered
    // inside a 300×300 host so the Conic primitive's outer radius
    // (270 dip diameter) doesn't clip on the corners. Constants —
    // never change at runtime.
    private static readonly Vector2 NakedHudSize = new(272f, 78f);
    private const           float   NakedHostDim = 300f;

    public HudPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        _rebuildDebounce.Tick += (_, _) =>
        {
            _rebuildDebounce.Stop();
            if (!_rebuildPending) return;
            _rebuildPending = false;
            ApplyTarget();
        };

        // VM observer : on target change → rebuild the tuning panel (the
        // active section set changed) + re-mount the preview ; on play-
        // state change → re-mount the preview (Pause / Play visual
        // flips).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += OnPageLoaded;
        }
    }

    // First-load initialisation : build the tuning panel from the active
    // target's section list, project the initial preview. Loaded fires
    // once for the cached Page instance ; subsequent navigations land
    // in OnNavigatedTo, which doesn't rebuild the panel (nothing
    // changed on the VM yet). The first user interaction on a slider
    // or the target menu triggers the next rebuild path.
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        BuildTuningPanel();
        ApplyTarget();
    }

    // ── Public surface called by PlaygroundWindow ────────────────────────────

    // Forces Play→Stop on every window re-show, so the user lands on a
    // known state regardless of where they left the preview. Invoked by
    // PlaygroundWindow.ShowAndActivate. (The window also destroys itself
    // on close so this only matters during the lifetime of one window
    // instance.)
    public void ForcePause()
    {
        if (!ViewModel.IsPlaying) return;
        ViewModel.IsPlaying = false;
        // ApplyTarget runs through the VM observer below ; no need to
        // call it twice.
    }

    // Releases runtime composition + timers. Called by PlaygroundWindow
    // on Closed.
    public void DisposeResources()
    {
        _rmsTimer.Stop();
        _rmsClock.Stop();
        _rebuildDebounce.Stop();
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        _nakedPreview?.Dispose();
        _nakedPreview = null;
    }

    // ── VM observer ──────────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HudViewModel.CurrentTarget):
                // Target switch : the relevant tuning sections change so
                // the panel is rebuilt to surface the right knobs. Then
                // the preview is reprojected — ApplyTarget tears down
                // the old visual and instantiates the new state.
                RebuildTuningPanel();
                ApplyTarget();
                break;

            case nameof(HudViewModel.IsPlaying):
                // Play / Stop flip : the preview re-projects. Tuning
                // panel is unchanged.
                ApplyTarget();
                break;
        }
    }

    // ── Target picker (DropDownButton MenuFlyout) ────────────────────────────

    private void OnTargetMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<HudTarget>(tag, out var next)) return;

        // ViewModel setter raises PropertyChanged → OnViewModelPropertyChanged
        // → RebuildTuningPanel + ApplyTarget. No need to do anything
        // else here ; the visual update flows through the observer.
        ViewModel.CurrentTarget = next;
    }

    // ── Transport (Start / Stop) ─────────────────────────────────────────────

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPlaying = true;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPlaying = false;
    }

    // ── Core : apply target + play state to the preview ──────────────────────

    private void ApplyTarget()
    {
        // Tear down everything first.
        ChronoPreview.ApplyState(HudState.Hidden);
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        _nakedPreview?.Dispose();
        _nakedPreview = null;
        StopRmsPump();

        if (!ViewModel.IsPlaying)
        {
            // Stop = truly empty preview. Neither the chrono silhouette
            // nor the naked host should remain — no residual Charging-
            // like card. The outer Border (substrate) stays so the user
            // sees where the preview will reappear on Start.
            ChronoPreview.Visibility    = Visibility.Collapsed;
            NakedPreviewHost.Visibility = Visibility.Collapsed;
            return;
        }

        var currentTarget = ViewModel.CurrentTarget;
        bool isNaked = currentTarget is HudTarget.Conic
                                     or HudTarget.ArcMask
                                     or HudTarget.Combined;
        ChronoPreview.Visibility    = isNaked ? Visibility.Collapsed : Visibility.Visible;
        NakedPreviewHost.Visibility = isNaked ? Visibility.Visible   : Visibility.Collapsed;

        if (isNaked)
        {
            var part = currentTarget switch
            {
                HudTarget.Conic    => HudComposition.NakedMaskPart.Conic,
                HudTarget.ArcMask  => HudComposition.NakedMaskPart.ArcMask,
                _                  => HudComposition.NakedMaskPart.Combined,
            };
            AttachNakedPreview(part);
            return;
        }

        var state = currentTarget switch
        {
            HudTarget.Charging     => HudState.Charging,
            HudTarget.Recording    => HudState.Recording,
            HudTarget.Transcribing => HudState.Transcribing,
            _                      => HudState.Rewriting,
        };

        // Arm the tuning-config override BEFORE ApplyState so the
        // single stroke creation inside AttachProcessingVisual picks
        // it up — otherwise ApplyState would create one stroke with
        // shipping defaults and we'd immediately dispose + recreate
        // via RebuildStroke.
        if (currentTarget is HudTarget.Recording
                          or HudTarget.Transcribing
                          or HudTarget.Rewriting)
        {
            ChronoPreview.SetNextStrokeConfig(_tuning.ToConfig());
        }
        ChronoPreview.ApplyState(state);

        if (_simulateChangedDigits &&
            (currentTarget == HudTarget.Transcribing || currentTarget == HudTarget.Rewriting))
        {
            ChronoPreview.SimulateChangedDigits(
                min1: false, min2: false,
                sec1: true,  sec2: true,
                cs1:  true,  cs2:  true);
        }

        if (currentTarget == HudTarget.Recording)
            StartRmsPump();
    }

    private void AttachNakedPreview(HudComposition.NakedMaskPart part)
    {
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        _nakedPreview?.Dispose();
        _nakedPreview = null;

        var compositor = ElementCompositionPreview.GetElementVisual(NakedPreviewHost).Compositor;

        // Theme-aware arc fill : the arc mask surface is a solid colour
        // + alpha ramp, and only the ArcMask rail (no Conic colour
        // behind it) relies on that colour for visibility against the
        // window's LayerFillColorDefaultBrush. Light theme → LayerFill
        // is near-white so white-on-alpha vanishes ; invert to black.
        // Dark theme keeps white. Combined goes through AlphaMaskEffect
        // and ignores the arc RGB, so the colour choice is harmless
        // there.
        bool isLightTheme =
            Content is FrameworkElement fe &&
            fe.ActualTheme == ElementTheme.Light;
        var arcFill = isLightTheme ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;

        _nakedPreview = HudComposition.CreateNakedMaskPreview(
            compositor, NakedHudSize, _tuning.ToConfig(), part, arcFill);

        float inset = (NakedHostDim - _nakedPreview.Container.Size.X) / 2f;
        _nakedPreview.Container.Offset = new Vector3(inset, inset, 0f);

        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, _nakedPreview.Container);
    }

    private void RequestRebuild()
    {
        _rebuildPending = true;
        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    // ── RMS pump (Recording only) ───────────────────────────────────────

    private void StartRmsPump()
    {
        if (_rmsTimer.IsEnabled) return;
        _rmsClock.Restart();
        _rmsTimer.Tick -= RmsTimerTick;
        _rmsTimer.Tick += RmsTimerTick;
        _rmsTimer.Start();
    }

    private void StopRmsPump()
    {
        if (!_rmsTimer.IsEnabled) return;
        _rmsTimer.Stop();
        _rmsTimer.Tick -= RmsTimerTick;
        _rmsClock.Stop();
    }

    private void RmsTimerTick(object? sender, object e)
    {
        float rms;
        if (_simManualOverride)
        {
            rms = _simManualValue;
        }
        else
        {
            double t = _rmsClock.Elapsed.TotalSeconds;
            float sweep = (float)(0.5 + 0.5 * Math.Sin(
                2 * Math.PI * t / Math.Max(0.1, _simRmsPeriodSeconds)));
            rms = _simRmsMin + sweep * (_simRmsMax - _simRmsMin);
        }
        ChronoPreview.UpdateAudioLevel(rms);
    }

    // ── Reset all ───────────────────────────────────────────────────────
    //
    // The Playground has no persistence : tuning lives in memory for
    // the current process and dies on app exit. "Reset all" is just a
    // "snap back to compiled defaults" affordance — useful mid-session
    // to start over without quitting the app. We go through the
    // shipping-default objects directly instead of chaining the per-
    // section Reset*() methods : those would call RebuildTuningPanel
    // once each, here we want a single atomic transition.
    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        // TuningModel : swap for a fresh instance — its field
        // initialisers ARE the shipping defaults.
        _tuning = new TuningModel();

        // Sim fields : keep aligned with the defaults documented at
        // the field declarations above. Source of truth duplicated
        // once here and in the field initialisers — acceptable for
        // this playground scope.
        _simRmsMin             = 0.013f;
        _simRmsMax             = 0.100f;
        _simRmsPeriodSeconds   = 2.0f;
        _simManualOverride     = false;
        _simManualValue        = 0.012f;
        _simulateChangedDigits = true;

        // Swipe + Audio mapping expanders — same values the individual
        // Reset* methods use. Swipe statics live on SwipeWaveAnimator
        // since 2026-05-02 (Deckle.Composition) ; audio mapping
        // statics still belong to AudioLevelMapper (Deckle.Audio).
        SwipeWaveAnimator.SwipeCycleSeconds = 3.0f;
        SwipeWaveAnimator.SwipeEaseP1       = new Vector2(0.5f, 0f);
        SwipeWaveAnimator.SwipeEaseP2       = new Vector2(0.2f, 1f);
        SwipeWaveAnimator.SwipeRiseAlpha    = 0.1f;
        SwipeWaveAnimator.SwipeDecayAlpha   = 0.025f;
        SwipeWaveAnimator.SwipeHeadDomain   = 8;
        AudioLevelMapper.EmaAlpha          = 0.25f;
        AudioLevelMapper.MinDbfs           = -55f;
        AudioLevelMapper.MaxDbfs           = -32f;
        AudioLevelMapper.DbfsCurveExponent = 1.0f;

        // HudComposition geometry mutables tuned via the HUD geometry
        // expander.
        HudComposition.InsetDip = -2f;

        BuildTuningPanel();
        ApplyTarget();
    }

    // ── Tuning panel dispatcher ─────────────────────────────────────────
    //
    // Filters the expander list by ViewModel.ActiveTuningSections so
    // only the knobs that affect the active target are rendered. Parked
    // is always appended (collapsed by default) for the transition-only
    // knobs the developer still wants to tweak from any target. The
    // per-section Add/Reset methods live in HudPage.Expanders.cs.

    private void BuildTuningPanel()
    {
        // Keep the static header (title + description + Reset all)
        // from XAML, clear anything below it (in case this is ever
        // invoked twice), then append only the expanders for the
        // active target.
        while (TuningStack.Children.Count > 1)
            TuningStack.Children.RemoveAt(TuningStack.Children.Count - 1);

        var sections = ViewModel.ActiveTuningSections;
        foreach (var section in sections)
        {
            switch (section)
            {
                case HudTuningSection.Geometry:      AddHudGeometryExpander();    break;
                case HudTuningSection.Palette:       AddPaletteExpander();        break;
                case HudTuningSection.ConicFade:     AddConicFadeExpander();      break;
                case HudTuningSection.HueRotation:   AddHueRotationExpander();    break;
                case HudTuningSection.ArcRotation:   AddArcRotationExpander();    break;
                case HudTuningSection.Swipe:         AddSwipeExpander();          break;
                case HudTuningSection.Recording:     AddRecordingExpander();      break;
                case HudTuningSection.Transcribing:  AddTranscribingExpander();   break;
                case HudTuningSection.Rewriting:     AddRewritingExpander();      break;
                case HudTuningSection.AudioMapping:  AddAudioMappingExpander();   break;
                case HudTuningSection.SimulatedRms:  AddSimulatedRmsExpander();   break;
            }
        }

        AddParkedExpander();
    }

    // Rebuild the whole tuning panel from the current _tuning state.
    // Used by every Reset*() and by the target switch path — the
    // alternative (tracking per-row controls in a dictionary) would be
    // more code for the same effect. The whole panel is only dozens of
    // rows of UI, so a wholesale reconstruction is cheap and keeps the
    // row factories tight.
    private void RebuildTuningPanel()
    {
        BuildTuningPanel();
        RequestRebuild();
    }
}
