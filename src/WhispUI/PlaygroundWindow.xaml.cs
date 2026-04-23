using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using WhispUI.Composition;
using WhispUI.Controls;
using WhispUI.Interop;
using WhispUI.Playground;
using WhispUI.Shell;

namespace WhispUI;

// ─── HUD playground window ────────────────────────────────────────────────────
//
// Long-lived tuning surface for the HUD composition stroke. Ported from the
// standalone `dev/HudPlayground` tool into a first-party WhispUI window.
// Lifecycle pattern mirrors SettingsWindow / LogWindow:
//   - Native TitleBar (ExtendsContentIntoTitleBar + SetTitleBar).
//   - Mica backdrop (app-persistent window, not transient).
//   - IconAssets-driven icon swapped idle/recording via SetRecordingState so
//     the whole app UI reflects engine state.
//   - Singleton: Closing → Cancel + Hide. Destroyed only at QuitApp.
//
// Layout: 2-column split.
//   Col 0: NavigationView + per-concept tuning panel (Palette, Hue rotation,
//          Arc rotation, Swipe, Recording, Transcribing, Rewriting, Audio
//          mapping, Simulated RMS, Parked). ContentPresenter host instead of
//          a Frame because panels are code-built StackPanels rather than Page
//          types — switch by reassigning ConceptHost.Content.
//   Col 1: sticky preview (target SelectorBar + Play/Pause RadioButtons +
//          HudChrono 272×78 OR naked mask preview 300×300). The preview
//          stays visible across concept pages so sliders on any page can be
//          observed against a live brush.
//
// Tuning is driven by TuningModel (mutable shadow of ConicArcStrokeConfig).
// Paint-time slider changes flag _rebuildPending and the debounced timer
// calls ApplyTarget 300 ms after the last movement — the CanvasDevice can't
// absorb one rebuild per DispatcherQueue tick during a drag.

public sealed partial class PlaygroundWindow : Window
{
    private readonly IntPtr _hwnd;

    // Icons shared with tray / LogWindow / SettingsWindow via IconAssets.
    // Swapping the .ico on disk propagates everywhere.
    private BitmapImage? _iconIdle;
    private BitmapImage? _iconRecording;
    private string? _iconIdlePath;
    private string? _iconRecordingPath;

    // ── Tuning state ────────────────────────────────────────────────────

    // Target enum drives both the SelectorBar selection and the preview
    // wiring. Names aligned with the SelectorBarItem.Text so the
    // ReferenceEquals dispatch in OnTargetSelectionChanged stays trivial.
    private enum Target
    {
        Charging,
        Recording,
        Transcribing,
        Rewriting,
        Conic,
        ArcMask,
        Combined,
    }

    private readonly TuningModel _tuning = new();

    // Sim RMS defaults chosen to mimic the RMS distribution of normal
    // conversational speech (50 ms window average):
    //   0.013 linear ≈ -38 dBFS — engine gate threshold, "breath".
    //   0.100 linear ≈ -20 dBFS — normal conversational mid-range.
    private float _simRmsMin           = 0.013f;
    private float _simRmsMax           = 0.100f;
    private float _simRmsPeriodSeconds = 2.0f;
    private bool  _simManualOverride   = false;
    private float _simManualValue      = 0.012f;

    // Simulate "digit changed during Recording" flags for the swipe reveal
    // preview on Transcribing / Rewriting. ON by default because observing
    // the swipe wave is the main reason Louis selects those targets here.
    private bool _simulateChangedDigits = true;

    private Target _currentTarget = Target.Transcribing;
    private bool   _isPlaying     = true;

    // ── Rebuild debounce ────────────────────────────────────────────────
    //
    // Paint-time slider changes rebuild the Win2D surfaces from scratch
    // (Canvas drawing session + oversized pxSquare × pxSquare bitmap
    // allocation + GPU upload). On a drag that's one rebuild per
    // DispatcherQueue tick — 50-100/s — and the CanvasDevice chokes.
    // A single DispatcherTimer accumulates pending and fires the actual
    // rebuild 300 ms after the last slider movement.
    private readonly DispatcherTimer _rebuildDebounce = new()
        { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _rebuildPending;

    // ── Simulated RMS pump ──────────────────────────────────────────────
    private readonly DispatcherTimer _rmsTimer = new()
        { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _rmsClock = new();

    // ── Slider ↔ NumberBox sync guard ───────────────────────────────────
    //
    // A two-way composite (Slider + NumberBox sharing a value source) has
    // to guard against feedback loops: setting Slider.Value fires
    // ValueChanged which pushes into NumberBox.Value which fires its own
    // ValueChanged which writes back to Slider. The flag is set around
    // every programmatic write so the paired ValueChanged is a no-op.
    private bool _syncing;

    // ── Concept panels ──────────────────────────────────────────────────
    //
    // Built once at Loaded, swapped into ConceptHost on nav selection.
    // Dictionary key = NavigationViewItem.Tag (nav tag, not type name,
    // because panels aren't Page types).
    private readonly Dictionary<string, StackPanel> _panels = new();

    public PlaygroundWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        LoadAppIcons();
        AppTitleBarIcon.ImageSource = _iconIdle;
        if (_iconIdlePath is not null) AppWindow.SetIcon(_iconIdlePath);

        // Native title bar. Standard height (not Tall) — no SearchBox in
        // this window so caption buttons don't need the extended chrome.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        SystemBackdrop = new MicaBackdrop();

        Title = "WhispUI Playground";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 880));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        // Min width 720 lets NavigationView sit in Left mode next to the
        // 440 dip sticky preview column without squeezing the tuning rows.
        presenter.PreferredMinimumWidth  = 720;
        presenter.PreferredMinimumHeight = 480;
        AppWindow.SetPresenter(presenter);

        // Close → hide, never destroy. Same contract as SettingsWindow /
        // LogWindow — App owns the single instance for its lifetime.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

        // Debounced rebuild tick — fires 300 ms after the last slider
        // movement. Drag-to-release feels instant; mid-drag the slider
        // value/text updates live but the preview keeps animating off
        // its previous surfaces.
        _rebuildDebounce.Tick += (_, _) =>
        {
            _rebuildDebounce.Stop();
            if (!_rebuildPending) return;
            _rebuildPending = false;
            ApplyTarget();
        };

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                BuildAllPanels();
                // Default landing page — Palette is the broadest set of
                // knobs, good first impression.
                Nav.SelectedItem = Nav.MenuItems[0];
                ApplyTarget();
            };
        }

        this.Closed += (_, _) =>
        {
            _rmsTimer.Stop();
            _rmsClock.Stop();
            _rebuildDebounce.Stop();
        };
    }

    // ── Lifecycle surface (called by App) ───────────────────────────────

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        AppWindow.Show();
        this.Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    // Beacon app icon (red = recording / grey = idle). Called from
    // WhispEngine.StatusChanged via App.xaml.cs — same pattern as
    // LogWindow.SetRecordingState.
    public void SetRecordingState(bool isRecording)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyRecordingState(isRecording);
        else DispatcherQueue.TryEnqueue(() => ApplyRecordingState(isRecording));
    }

    private void ApplyRecordingState(bool isRecording)
    {
        // Rebuild the ImageIconSource wholesale — mutating ImageSource in
        // place on the existing instance doesn't propagate to the
        // TitleBar visual (no routed PropertyChanged).
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = isRecording ? _iconRecording : _iconIdle,
        };

        var path = isRecording ? _iconRecordingPath : _iconIdlePath;
        if (path is not null) AppWindow.SetIcon(path);
    }

    private void LoadAppIcons()
    {
        _iconIdlePath      = IconAssets.ResolvePath(recording: false);
        _iconRecordingPath = IconAssets.ResolvePath(recording: true);

        if (_iconIdlePath is not null)
            _iconIdle = new BitmapImage(new Uri(_iconIdlePath));
        if (_iconRecordingPath is not null)
            _iconRecording = new BitmapImage(new Uri(_iconRecordingPath));
    }

    // ── Navigation ──────────────────────────────────────────────────────

    private void OnNavSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        if (item.Tag is not string tag) return;
        if (_panels.TryGetValue(tag, out var panel))
        {
            ConceptHost.Content = panel;
        }
    }

    // ── Target selector + Play/Pause ────────────────────────────────────

    private void OnTargetSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        if (selected is null) return;
        var next = ReferenceEquals(selected, TabCharging)     ? Target.Charging
                 : ReferenceEquals(selected, TabRecording)    ? Target.Recording
                 : ReferenceEquals(selected, TabTranscribing) ? Target.Transcribing
                 : ReferenceEquals(selected, TabRewriting)    ? Target.Rewriting
                 : ReferenceEquals(selected, TabConic)        ? Target.Conic
                 : ReferenceEquals(selected, TabArcMask)      ? Target.ArcMask
                                                              : Target.Combined;
        if (next == _currentTarget) return;
        _currentTarget = next;
        ApplyTarget();
    }

    private void OnPlayPauseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // RadioButtons.SelectedIndex: 0 = Play, 1 = Pause (order in XAML).
        _isPlaying = PlayPauseGroup.SelectedIndex == 0;
        ApplyTarget();
    }

    // ── Core: apply target + play state to the preview ──────────────────
    //
    // Tears down the current preview unconditionally, then (if playing)
    // brings up whichever preview matches _currentTarget. Full reset on
    // every call — the composition graph geometry is baked at creation
    // and the only in-place tweaks are the four live PropertySet scalars
    // (Saturation, Hue, Exposure, Opacity); everything else rebuilds.
    private void ApplyTarget()
    {
        ChronoPreview.ApplyState(HudState.Hidden);
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        StopRmsPump();

        if (!_isPlaying)
        {
            ChronoPreview.Visibility    = Visibility.Visible;
            NakedPreviewHost.Visibility = Visibility.Collapsed;
            return;
        }

        bool isNaked = _currentTarget is Target.Conic or Target.ArcMask or Target.Combined;
        ChronoPreview.Visibility    = isNaked ? Visibility.Collapsed : Visibility.Visible;
        NakedPreviewHost.Visibility = isNaked ? Visibility.Visible   : Visibility.Collapsed;

        if (isNaked)
        {
            var part = _currentTarget switch
            {
                Target.Conic    => HudComposition.NakedMaskPart.Conic,
                Target.ArcMask  => HudComposition.NakedMaskPart.ArcMask,
                _               => HudComposition.NakedMaskPart.Combined,
            };
            AttachNakedPreview(part);
            return;
        }

        var state = _currentTarget switch
        {
            Target.Charging     => HudState.Charging,
            Target.Recording    => HudState.Recording,
            Target.Transcribing => HudState.Transcribing,
            _                   => HudState.Rewriting,
        };

        // Arm the tuning-config override BEFORE ApplyState so the single
        // stroke creation inside AttachProcessingVisual picks it up —
        // otherwise ApplyState would create one stroke with shipping
        // defaults and we'd immediately dispose + recreate via RebuildStroke.
        if (_currentTarget is Target.Recording or Target.Transcribing or Target.Rewriting)
            ChronoPreview.SetNextStrokeConfig(_tuning.ToConfig());
        ChronoPreview.ApplyState(state);

        if (_simulateChangedDigits &&
            (_currentTarget == Target.Transcribing || _currentTarget == Target.Rewriting))
        {
            ChronoPreview.SimulateChangedDigits(
                min1: false, min2: false,
                sec1: true,  sec2: true,
                cs1:  true,  cs2:  true);
        }

        if (_currentTarget == Target.Recording)
            StartRmsPump();
    }

    // ── Naked preview attach ────────────────────────────────────────────

    private void AttachNakedPreview(HudComposition.NakedMaskPart part)
    {
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);

        var compositor = ElementCompositionPreview.GetElementVisual(NakedPreviewHost).Compositor;
        var naked = HudComposition.CreateNakedMaskPreview(
            compositor, NakedHudSize, _tuning.ToConfig(), part);

        // Factory returns a ContainerVisual sized pxSquare × pxSquare
        // (≈288 for a 272×78 HUD). The host Grid is 300×300 — centre
        // the visual inside so it lands over the reference Border.
        float inset = (NakedHostDim - naked.Size.X) / 2f;
        naked.Offset = new Vector3(inset, inset, 0f);

        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, naked);
    }

    private static readonly Vector2 NakedHudSize = new(272f, 78f);
    private const           float   NakedHostDim = 300f;

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

    // ────────────────────────────────────────────────────────────────────
    // Concept panels
    // ────────────────────────────────────────────────────────────────────

    private void BuildAllPanels()
    {
        _panels.Clear();
        _panels["palette"]      = NewConceptPanel(PopulatePalettePanel);
        _panels["hue"]          = NewConceptPanel(PopulateHuePanel);
        _panels["arc"]          = NewConceptPanel(PopulateArcPanel);
        _panels["swipe"]        = NewConceptPanel(PopulateSwipePanel);
        _panels["recording"]    = NewConceptPanel(PopulateRecordingPanel);
        _panels["transcribing"] = NewConceptPanel(PopulateTranscribingPanel);
        _panels["rewriting"]    = NewConceptPanel(PopulateRewritingPanel);
        _panels["audio"]        = NewConceptPanel(PopulateAudioPanel);
        _panels["rms"]          = NewConceptPanel(PopulateRmsPanel);
        _panels["parked"]       = NewConceptPanel(PopulateParkedPanel);
    }

    private static StackPanel NewConceptPanel(Action<StackPanel> populate)
    {
        var stack = new StackPanel
        {
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 4, // matches SettingsCardSpacing
        };
        populate(stack);
        return stack;
    }

    // ── Palette (baked OKLCh + conic fade + arc mirror) ─────────────────

    private void PopulatePalettePanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Palette",
            "Baked OKLCh palette, conic span and fade, arc mirror.");

        AddSectionHeader(stack, "OKLCh palette", ResetPalette);
        AddFloatRow(stack, "OklchLightness", 0, 1, 0.001, _tuning.OklchLightness,
            v => _tuning.OklchLightness = (float)v, rebuild: true);
        AddFloatRow(stack, "OklchChroma", 0, 0.4, 0.001, _tuning.OklchChroma,
            v => _tuning.OklchChroma = (float)v, rebuild: true);
        AddFloatRow(stack, "HueStart", 0, 1, 0.001, _tuning.HueStart,
            v => _tuning.HueStart = (float)v, rebuild: true);
        AddFloatRow(stack, "HueRange", 0, 1, 0.001, _tuning.HueRange,
            v => _tuning.HueRange = (float)v, rebuild: true);
        AddIntRow(stack, "WedgeCount", 16, 720, _tuning.WedgeCount,
            v => _tuning.WedgeCount = v, rebuild: true);

        AddSectionHeader(stack, "Conic fade & span", ResetPalette);
        AddFloatRow(stack, "ConicSpanTurns", 0.05, 1.0, 0.001, _tuning.ConicSpanTurns,
            v => _tuning.ConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicLeadFadeTurns", 0, 1, 0.001, _tuning.ConicLeadFadeTurns,
            v => _tuning.ConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicTailFadeTurns", 0, 1, 0.001, _tuning.ConicTailFadeTurns,
            v => _tuning.ConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicFadeCurve", 0.5, 10, 0.01, _tuning.ConicFadeCurve,
            v => _tuning.ConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "ArcMirror", _tuning.ArcMirror,
            v => _tuning.ArcMirror = v, rebuild: true);
    }

    private void ResetPalette()
    {
        var d = new TuningModel();
        _tuning.OklchLightness     = d.OklchLightness;
        _tuning.OklchChroma        = d.OklchChroma;
        _tuning.HueStart           = d.HueStart;
        _tuning.HueRange           = d.HueRange;
        _tuning.WedgeCount         = d.WedgeCount;
        _tuning.ConicSpanTurns     = d.ConicSpanTurns;
        _tuning.ConicLeadFadeTurns = d.ConicLeadFadeTurns;
        _tuning.ConicTailFadeTurns = d.ConicTailFadeTurns;
        _tuning.ConicFadeCurve     = d.ConicFadeCurve;
        _tuning.ArcMirror          = d.ArcMirror;
        PopulatePalettePanel(_panels["palette"]);
        RequestRebuild();
    }

    // ── Hue rotation ────────────────────────────────────────────────────

    private void PopulateHuePanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Hue rotation",
            "Period, direction and ease curve of the rotating hue phase.");

        AddSectionHeader(stack, "Hue rotation", ResetHue);
        AddFloatRow(stack, "HuePeriodSeconds", 0, 60, 0.1, _tuning.HuePeriodSeconds,
            v => _tuning.HuePeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "HueDirection", _tuning.HueDirection,
            v => _tuning.HueDirection = v);
        AddFloatRow(stack, "HueEaseP1.X", 0, 1, 0.001, _tuning.HueEaseP1X,
            v => _tuning.HueEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP1.Y", -0.5, 1.5, 0.001, _tuning.HueEaseP1Y,
            v => _tuning.HueEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.X", 0, 1, 0.001, _tuning.HueEaseP2X,
            v => _tuning.HueEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.Y", -0.5, 1.5, 0.001, _tuning.HueEaseP2Y,
            v => _tuning.HueEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetHue()
    {
        var d = new TuningModel();
        _tuning.HuePeriodSeconds = d.HuePeriodSeconds;
        _tuning.HueDirection     = d.HueDirection;
        _tuning.HueEaseP1X       = d.HueEaseP1X;
        _tuning.HueEaseP1Y       = d.HueEaseP1Y;
        _tuning.HueEaseP2X       = d.HueEaseP2X;
        _tuning.HueEaseP2Y       = d.HueEaseP2Y;
        PopulateHuePanel(_panels["hue"]);
        RequestRebuild();
    }

    // ── Arc rotation ────────────────────────────────────────────────────

    private void PopulateArcPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Arc rotation",
            "Period, direction and ease curve of the rotating arc mask.");

        AddSectionHeader(stack, "Arc rotation", ResetArc);
        AddFloatRow(stack, "ArcPeriodSeconds", 0.5, 30, 0.1, _tuning.ArcPeriodSeconds,
            v => _tuning.ArcPeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "ArcDirection", _tuning.ArcDirection,
            v => _tuning.ArcDirection = v);
        AddFloatRow(stack, "ArcEaseP1.X", 0, 1, 0.001, _tuning.ArcEaseP1X,
            v => _tuning.ArcEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP1.Y", -0.5, 1.5, 0.001, _tuning.ArcEaseP1Y,
            v => _tuning.ArcEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.X", 0, 1, 0.001, _tuning.ArcEaseP2X,
            v => _tuning.ArcEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.Y", -0.5, 1.5, 0.001, _tuning.ArcEaseP2Y,
            v => _tuning.ArcEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetArc()
    {
        var d = new TuningModel();
        _tuning.ArcPeriodSeconds = d.ArcPeriodSeconds;
        _tuning.ArcDirection     = d.ArcDirection;
        _tuning.ArcEaseP1X       = d.ArcEaseP1X;
        _tuning.ArcEaseP1Y       = d.ArcEaseP1Y;
        _tuning.ArcEaseP2X       = d.ArcEaseP2X;
        _tuning.ArcEaseP2Y       = d.ArcEaseP2Y;
        PopulateArcPanel(_panels["arc"]);
        RequestRebuild();
    }

    // ── Swipe (Transcribing / Rewriting) ────────────────────────────────

    private void PopulateSwipePanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Swipe",
            "Per-digit heat wave that reveals which digits changed during Recording. Applies to Transcribing and Rewriting.");

        AddSectionHeader(stack, "Swipe", ResetSwipe);
        AddFloatRow(stack, "SwipeCycleSeconds", 0.1, 6.0, 0.01,
            HudChrono.SwipeCycleSeconds,
            v => HudChrono.SwipeCycleSeconds = (float)v);
        AddFloatRow(stack, "SwipeEaseP1.X", 0, 1, 0.001, HudChrono.SwipeEaseP1.X,
            v => HudChrono.SwipeEaseP1 = new Vector2((float)v, HudChrono.SwipeEaseP1.Y));
        AddFloatRow(stack, "SwipeEaseP1.Y", -0.5, 1.5, 0.001, HudChrono.SwipeEaseP1.Y,
            v => HudChrono.SwipeEaseP1 = new Vector2(HudChrono.SwipeEaseP1.X, (float)v));
        AddFloatRow(stack, "SwipeEaseP2.X", 0, 1, 0.001, HudChrono.SwipeEaseP2.X,
            v => HudChrono.SwipeEaseP2 = new Vector2((float)v, HudChrono.SwipeEaseP2.Y));
        AddFloatRow(stack, "SwipeEaseP2.Y", -0.5, 1.5, 0.001, HudChrono.SwipeEaseP2.Y,
            v => HudChrono.SwipeEaseP2 = new Vector2(HudChrono.SwipeEaseP2.X, (float)v));
        AddFloatRow(stack, "SwipeRiseAlpha", 0.02, 1.0, 0.001, HudChrono.SwipeRiseAlpha,
            v => HudChrono.SwipeRiseAlpha = (float)v);
        AddFloatRow(stack, "SwipeDecayAlpha", 0.01, 0.5, 0.001, HudChrono.SwipeDecayAlpha,
            v => HudChrono.SwipeDecayAlpha = (float)v);
        AddToggleRow(stack, "Simulate changed digits",
            _simulateChangedDigits,
            v => { _simulateChangedDigits = v; ApplyTarget(); });
    }

    private void ResetSwipe()
    {
        HudChrono.SwipeCycleSeconds = 1.6f;
        HudChrono.SwipeEaseP1       = new Vector2(0.5f, 0f);
        HudChrono.SwipeEaseP2       = new Vector2(0.2f, 1f);
        HudChrono.SwipeRiseAlpha    = 0.22f;
        HudChrono.SwipeDecayAlpha   = 0.06f;
        _simulateChangedDigits      = true;
        PopulateSwipePanel(_panels["swipe"]);
        ApplyTarget();
    }

    // ── Recording variant ───────────────────────────────────────────────

    private void PopulateRecordingPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Recording",
            "Variant-specific knobs for the Recording state: paint-time mask shape and runtime colour-pipeline deltas.");

        AddSectionHeader(stack, "Recording", ResetRecording);
        AddFloatRow(stack, "RecordingConicSpanTurns", 0.05, 1, 0.001, _tuning.RecordingConicSpanTurns,
            v => _tuning.RecordingConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicLeadFadeTurns", 0, 1, 0.001, _tuning.RecordingConicLeadFadeTurns,
            v => _tuning.RecordingConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicTailFadeTurns", 0, 1, 0.001, _tuning.RecordingConicTailFadeTurns,
            v => _tuning.RecordingConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicFadeCurve", 0.5, 10, 0.01, _tuning.RecordingConicFadeCurve,
            v => _tuning.RecordingConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "RecordingArcMirror", _tuning.RecordingArcMirror,
            v => _tuning.RecordingArcMirror = v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationDark", 0, 1, 0.001, _tuning.RecordingSaturationDark,
            v => _tuning.RecordingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationLight", 0, 1, 0.001, _tuning.RecordingSaturationLight,
            v => _tuning.RecordingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingHueShiftTurns", 0, 1, 0.001, _tuning.RecordingHueShiftTurns,
            v => _tuning.RecordingHueShiftTurns = (float)v, rebuild: true);
        // Exposure clamped to D2D1_EXPOSURE spec [-2, +2]. Values outside
        // produce driver-level assertions on rebuild.
        AddFloatRow(stack, "RecordingExposureDark", -2, 2, 0.01, _tuning.RecordingExposureDark,
            v => _tuning.RecordingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingExposureLight", -2, 2, 0.01, _tuning.RecordingExposureLight,
            v => _tuning.RecordingExposureLight = (float)v, rebuild: true);
    }

    private void ResetRecording()
    {
        var d = new TuningModel();
        _tuning.RecordingConicSpanTurns     = d.RecordingConicSpanTurns;
        _tuning.RecordingConicLeadFadeTurns = d.RecordingConicLeadFadeTurns;
        _tuning.RecordingConicTailFadeTurns = d.RecordingConicTailFadeTurns;
        _tuning.RecordingConicFadeCurve     = d.RecordingConicFadeCurve;
        _tuning.RecordingArcMirror          = d.RecordingArcMirror;
        _tuning.RecordingSaturationDark     = d.RecordingSaturationDark;
        _tuning.RecordingSaturationLight    = d.RecordingSaturationLight;
        _tuning.RecordingHueShiftTurns      = d.RecordingHueShiftTurns;
        _tuning.RecordingExposureDark       = d.RecordingExposureDark;
        _tuning.RecordingExposureLight      = d.RecordingExposureLight;
        PopulateRecordingPanel(_panels["recording"]);
        RequestRebuild();
    }

    // ── Transcribing variant ────────────────────────────────────────────

    private void PopulateTranscribingPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Transcribing",
            "Runtime colour-pipeline deltas for the Transcribing state.");

        AddSectionHeader(stack, "Transcribing", ResetTranscribing);
        AddFloatRow(stack, "TranscribingSaturationDark", 0, 1, 0.001, _tuning.TranscribingSaturationDark,
            v => _tuning.TranscribingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingSaturationLight", 0, 1, 0.001, _tuning.TranscribingSaturationLight,
            v => _tuning.TranscribingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingHueShiftTurns", 0, 1, 0.001, _tuning.TranscribingHueShiftTurns,
            v => _tuning.TranscribingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureDark", -2, 2, 0.01, _tuning.TranscribingExposureDark,
            v => _tuning.TranscribingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureLight", -2, 2, 0.01, _tuning.TranscribingExposureLight,
            v => _tuning.TranscribingExposureLight = (float)v, rebuild: true);
    }

    private void ResetTranscribing()
    {
        var d = new TuningModel();
        _tuning.TranscribingSaturationDark  = d.TranscribingSaturationDark;
        _tuning.TranscribingSaturationLight = d.TranscribingSaturationLight;
        _tuning.TranscribingHueShiftTurns   = d.TranscribingHueShiftTurns;
        _tuning.TranscribingExposureDark    = d.TranscribingExposureDark;
        _tuning.TranscribingExposureLight   = d.TranscribingExposureLight;
        PopulateTranscribingPanel(_panels["transcribing"]);
        RequestRebuild();
    }

    // ── Rewriting variant ───────────────────────────────────────────────

    private void PopulateRewritingPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Rewriting",
            "Runtime colour-pipeline deltas for the Rewriting state.");

        AddSectionHeader(stack, "Rewriting", ResetRewriting);
        AddFloatRow(stack, "RewritingSaturation", 0, 1, 0.001, _tuning.RewritingSaturation,
            v => _tuning.RewritingSaturation = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingHueShiftTurns", 0, 1, 0.001, _tuning.RewritingHueShiftTurns,
            v => _tuning.RewritingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingExposure", -2, 2, 0.01, _tuning.RewritingExposure,
            v => _tuning.RewritingExposure = (float)v, rebuild: true);
    }

    private void ResetRewriting()
    {
        var d = new TuningModel();
        _tuning.RewritingSaturation    = d.RewritingSaturation;
        _tuning.RewritingHueShiftTurns = d.RewritingHueShiftTurns;
        _tuning.RewritingExposure      = d.RewritingExposure;
        PopulateRewritingPanel(_panels["rewriting"]);
        RequestRebuild();
    }

    // ── Audio mapping (dBFS → opacity) ──────────────────────────────────

    private void PopulateAudioPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Audio mapping",
            "EMA smoothing, dBFS window and curve for the Recording audio-to-opacity mapping.");

        AddSectionHeader(stack, "Audio mapping", ResetAudio);
        AddFloatRow(stack, "EmaAlpha", 0, 1, 0.001, HudChrono.EmaAlpha,
            v => HudChrono.EmaAlpha = (float)v);
        AddFloatRow(stack, "MinDbfs", -80, 0, 0.1, HudChrono.MinDbfs,
            v => HudChrono.MinDbfs = (float)v);
        AddFloatRow(stack, "MaxDbfs", -60, 0, 0.1, HudChrono.MaxDbfs,
            v => HudChrono.MaxDbfs = (float)v);
        AddFloatRow(stack, "DbfsCurveExponent", 0.5, 4, 0.01, HudChrono.DbfsCurveExponent,
            v => HudChrono.DbfsCurveExponent = (float)v);
    }

    private void ResetAudio()
    {
        HudChrono.EmaAlpha          = 0.72f;
        HudChrono.MinDbfs           = -40f;
        HudChrono.MaxDbfs           = -22f;
        HudChrono.DbfsCurveExponent = 2.0f;
        PopulateAudioPanel(_panels["audio"]);
    }

    // ── Simulated RMS ───────────────────────────────────────────────────

    private void PopulateRmsPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Simulated RMS",
            "Audio level pump driving Recording. Sine sweep by default, manual override for static inspection.");

        AddSectionHeader(stack, "Simulated RMS", ResetRms);
        AddFloatRow(stack, "SimRmsMin", 0, 0.3, 0.001, _simRmsMin,
            v => _simRmsMin = (float)v);
        AddFloatRow(stack, "SimRmsMax", 0, 0.3, 0.001, _simRmsMax,
            v => _simRmsMax = (float)v);
        AddFloatRow(stack, "SimRmsPeriodSeconds", 0.2, 10, 0.01, _simRmsPeriodSeconds,
            v => _simRmsPeriodSeconds = (float)v);
        AddToggleRow(stack, "Manual override", _simManualOverride,
            v => _simManualOverride = v);
        AddFloatRow(stack, "SimRmsManualValue", 0, 0.3, 0.001, _simManualValue,
            v => _simManualValue = (float)v);
    }

    private void ResetRms()
    {
        _simRmsMin           = 0.013f;
        _simRmsMax           = 0.100f;
        _simRmsPeriodSeconds = 2.0f;
        _simManualOverride   = false;
        _simManualValue      = 0.012f;
        PopulateRmsPanel(_panels["rms"]);
    }

    // ── Parked (variant transition knobs, muted in single-target mode) ──

    private void PopulateParkedPanel(StackPanel stack)
    {
        stack.Children.Clear();
        AddPageTitle(stack, "Parked",
            "Fields whose effect is only visible during a variant transition. Muted by construction in the playground (single target at a time); kept for default-tweaking.");

        AddSectionHeader(stack, "Parked", ResetParked);
        AddFloatRow(stack, "HuePhaseTurns", 0, 1, 0.001, _tuning.HuePhaseTurns,
            v => _tuning.HuePhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcPhaseTurns", 0, 1, 0.001, _tuning.ArcPhaseTurns,
            v => _tuning.ArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingArcPhaseTurns", 0, 1, 0.001, _tuning.RecordingArcPhaseTurns,
            v => _tuning.RecordingArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "HueMinSpeedFraction", 0, 1, 0.001, _tuning.HueMinSpeedFraction,
            v => _tuning.HueMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcMinSpeedFraction", 0, 1, 0.001, _tuning.ArcMinSpeedFraction,
            v => _tuning.ArcMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingBlendSeconds", 0, 5, 0.01, _tuning.RecordingBlendSeconds,
            v => _tuning.RecordingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RecordingHuePeriodSeconds", 0, 60, 0.1, _tuning.RecordingHuePeriodSeconds,
            v => _tuning.RecordingHuePeriodSeconds = v, rebuild: true);
        AddFloatRow(stack, "TranscribingOpacity", 0, 1, 0.001, _tuning.TranscribingOpacity,
            v => _tuning.TranscribingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingBlendSeconds", 0, 5, 0.01, _tuning.TranscribingBlendSeconds,
            v => _tuning.TranscribingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RewritingOpacity", 0, 1, 0.001, _tuning.RewritingOpacity,
            v => _tuning.RewritingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingBlendSeconds", 0, 5, 0.01, _tuning.RewritingBlendSeconds,
            v => _tuning.RewritingBlendSeconds = v, rebuild: true);
    }

    private void ResetParked()
    {
        var d = new TuningModel();
        _tuning.HuePhaseTurns             = d.HuePhaseTurns;
        _tuning.ArcPhaseTurns             = d.ArcPhaseTurns;
        _tuning.RecordingArcPhaseTurns    = d.RecordingArcPhaseTurns;
        _tuning.HueMinSpeedFraction       = d.HueMinSpeedFraction;
        _tuning.ArcMinSpeedFraction       = d.ArcMinSpeedFraction;
        _tuning.RecordingBlendSeconds     = d.RecordingBlendSeconds;
        _tuning.RecordingHuePeriodSeconds = d.RecordingHuePeriodSeconds;
        _tuning.TranscribingOpacity       = d.TranscribingOpacity;
        _tuning.TranscribingBlendSeconds  = d.TranscribingBlendSeconds;
        _tuning.RewritingOpacity          = d.RewritingOpacity;
        _tuning.RewritingBlendSeconds     = d.RewritingBlendSeconds;
        PopulateParkedPanel(_panels["parked"]);
        RequestRebuild();
    }

    // ────────────────────────────────────────────────────────────────────
    // Row + section factories
    // ────────────────────────────────────────────────────────────────────

    private static void AddPageTitle(StackPanel stack, string title, string description)
    {
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    // Section header — pattern from GeneralPage.xaml:72-86. Title on the
    // left, Reset HyperlinkButton aligned bottom-right. Invoking Reset
    // repopulates the section fields and rebuilds the preview.
    private void AddSectionHeader(StackPanel stack, string title, Action resetAction)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            Style = (Style)(RootGrid.Resources["SettingsSectionHeaderTextBlockStyle"]
                     ?? Application.Current.Resources["BodyStrongTextBlockStyle"]),
        };
        Grid.SetColumn(titleTb, 0);
        grid.Children.Add(titleTb);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset",
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        };
        ToolTipService.SetToolTip(resetBtn, "Reset this section to defaults");
        resetBtn.Click += (_, _) => resetAction();
        Grid.SetColumn(resetBtn, 1);
        grid.Children.Add(resetBtn);

        stack.Children.Add(grid);
    }

    // ── Slider + NumberBox composite ────────────────────────────────────
    //
    // Two-way sync: slider move → NumberBox.Value updates; NumberBox typed
    // value → Slider.Value updates. The `_syncing` guard prevents the
    // feedback loop. Both controls call the setter on every commit;
    // rebuild-triggering rows go through RequestRebuild so CanvasDevice
    // doesn't thrash mid-drag.

    private void AddFloatRow(
        StackPanel stack, string label,
        double min, double max, double step, double value,
        Action<double> setter,
        bool rebuild = false)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = step,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        var numberBox = new NumberBox
        {
            Value = value,
            Minimum = min, Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 128,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                numberBox.Value = e.NewValue;
                setter(e.NewValue);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };
        numberBox.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            if (double.IsNaN(e.NewValue)) return;
            _syncing = true;
            try
            {
                double clamped = Math.Clamp(e.NewValue, min, max);
                slider.Value = clamped;
                setter(clamped);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };

        stack.Children.Add(WrapRow(label, slider, numberBox));
    }

    private void AddIntRow(
        StackPanel stack, string label,
        int min, int max, int value,
        Action<int> setter,
        bool rebuild = false)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = 1,
            SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        var numberBox = new NumberBox
        {
            Value = value,
            Minimum = min, Maximum = max,
            SmallChange = 1, LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 128,
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                int iv = (int)Math.Round(e.NewValue);
                numberBox.Value = iv;
                setter(iv);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };
        numberBox.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            if (double.IsNaN(e.NewValue)) return;
            _syncing = true;
            try
            {
                int iv = Math.Clamp((int)Math.Round(e.NewValue), min, max);
                slider.Value = iv;
                setter(iv);
                if (rebuild) RequestRebuild();
            }
            finally { _syncing = false; }
        };

        stack.Children.Add(WrapRow(label, slider, numberBox));
    }

    private void AddToggleRow(
        StackPanel stack, string label,
        bool value, Action<bool> setter,
        bool rebuild = false)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = "on",
            OffContent = "off",
        };
        toggle.Toggled += (_, _) =>
        {
            setter(toggle.IsOn);
            if (rebuild) RequestRebuild();
        };

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(labelTb);
        grid.Children.Add(toggle);
        stack.Children.Add(grid);
    }

    // Rotation direction is semantically ±1. A slider would smuggle a
    // hidden speed multiplier via fractional values. ToggleSwitch snaps
    // strictly to -1 or +1.
    private void AddDirectionRow(
        StackPanel stack, string label,
        float value, Action<float> setter)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value >= 0f,
            OnContent = "CW (+1)",
            OffContent = "CCW (-1)",
        };
        toggle.Toggled += (_, _) =>
        {
            setter(toggle.IsOn ? 1f : -1f);
            RequestRebuild();
        };

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(labelTb);
        grid.Children.Add(toggle);
        stack.Children.Add(grid);
    }

    private static Grid WrapRow(string label, Slider slider, NumberBox numberBox)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(numberBox, 2);
        grid.Children.Add(labelTb);
        grid.Children.Add(slider);
        grid.Children.Add(numberBox);
        return grid;
    }
}
