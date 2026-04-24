using System;
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
// Layout is deliberately flat and always-visible: a sticky preview column
// on the left (target SelectorBar + Play/Pause + HudChrono / naked host)
// and a single scrollable column of Expanders on the right — everything
// fixed, no concept-level tabs. A draggable sash between the two lets Louis
// trade preview footprint for tuning width when a slider block gets tight.
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
    // A two-way composite (Slider + NumberBox sharing a value source) has
    // to guard against feedback loops: setting Slider.Value fires
    // ValueChanged which pushes into NumberBox.Value which fires its own
    // ValueChanged which writes back to Slider. The flag short-circuits
    // the paired event during every programmatic write.
    private bool _syncing;

    // ── Naked preview bundle ────────────────────────────────────────────
    //
    // HudComposition.CreateNakedMaskPreview returns a disposable bundle
    // that wraps the container visual + rotation property sets + brushes
    // + surfaces. We MUST Dispose it before mounting a new one, otherwise
    // every rebuild leaks two Forever ScalarKeyFrameAnimations on the
    // compositor — after enough slider moves the compositor saturates and
    // the rotation freezes mid-animation (the Conic preview regression
    // Louis reported). See NakedPreview class comment in HudComposition.
    private HudComposition.NakedPreview? _nakedPreview;

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
        // Min width 800: preview column min (400) + sash (6) + tuning
        // column min (360) + a little breathing room. Below this the sash
        // clamp runs out of room.
        presenter.PreferredMinimumWidth  = 800;
        presenter.PreferredMinimumHeight = 480;
        AppWindow.SetPresenter(presenter);

        // Close → hide, never destroy. Same contract as SettingsWindow /
        // LogWindow — App owns the single instance for its lifetime.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

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
                BuildTuningPanel();
                ApplyTarget();
            };
        }

        // Tap on empty background → move focus to RootGrid, which dismisses
        // the caret from a NumberBox the user just edited. Focused input
        // controls in WinUI 3 keep focus on background clicks unless some
        // other element claims it — same UX wart as Settings windows if
        // left unhandled. Tapped bubbles up only when no child control
        // marks it Handled, so clicks that land on a Slider / NumberBox /
        // Expander header don't trigger dismissal.
        RootGrid.Tapped += (_, _) => RootGrid.Focus(FocusState.Pointer);

        this.Closed += (_, _) =>
        {
            _rmsTimer.Stop();
            _rmsClock.Stop();
            _rebuildDebounce.Stop();
            // Detach the composition child first so the compositor stops
            // referencing the bundle's visual tree, then dispose. In the
            // singleton-hidden pattern (Closing→Hide) Closed only fires at
            // QuitApp so this is a belt-and-braces rather than a hot path.
            ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
            _nakedPreview?.Dispose();
            _nakedPreview = null;
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

    public void SetRecordingState(bool isRecording)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyRecordingState(isRecording);
        else DispatcherQueue.TryEnqueue(() => ApplyRecordingState(isRecording));
    }

    private void ApplyRecordingState(bool isRecording)
    {
        // Rebuild the ImageIconSource wholesale — in-place ImageSource
        // mutation doesn't propagate to the TitleBar visual.
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

    // ── Sash (column resize) ────────────────────────────────────────────
    //
    // Clamp the new PreviewCol width between its declared MinWidth and the
    // total available width minus the sash and TuningCol.MinWidth. This
    // mirrors how a GridSplitter would clamp — keeping both columns above
    // their minimums regardless of window size.
    private void OnSashDragDelta(SashThumb sender, double dx)
    {
        double total = RootGrid.ActualWidth;
        if (total <= 0) return;

        double sashWidth = 6;
        double previewMin = PreviewCol.MinWidth;
        double tuningMin  = TuningCol.MinWidth;
        double max = Math.Max(previewMin, total - sashWidth - tuningMin);

        double current = PreviewCol.ActualWidth;
        double target  = Math.Clamp(current + dx, previewMin, max);

        PreviewCol.Width = new GridLength(target);
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

    private void ApplyTarget()
    {
        // Tear down everything first.
        ChronoPreview.ApplyState(HudState.Hidden);
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        _nakedPreview?.Dispose();
        _nakedPreview = null;
        StopRmsPump();

        if (!_isPlaying)
        {
            // Pause = truly empty preview. Neither the chrono silhouette
            // nor the naked host should remain — no residual Charging-like
            // card. The outer Border (substrate) stays so the user sees
            // where the preview will reappear on Play.
            ChronoPreview.Visibility    = Visibility.Collapsed;
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

    private void AttachNakedPreview(HudComposition.NakedMaskPart part)
    {
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        _nakedPreview?.Dispose();
        _nakedPreview = null;

        var compositor = ElementCompositionPreview.GetElementVisual(NakedPreviewHost).Compositor;

        // Theme-aware arc fill: the arc mask surface is a solid colour +
        // alpha ramp, and only the ArcMask rail (no Conic colour behind
        // it) relies on that colour for visibility against the window's
        // LayerFillColorDefaultBrush. Light theme → LayerFill is near-white
        // so white-on-alpha vanishes; invert to black. Dark theme keeps
        // white. Combined goes through AlphaMaskEffect and ignores the
        // arc RGB, so the colour choice is harmless there.
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
    // Tuning panel — flat list of Expanders
    // ────────────────────────────────────────────────────────────────────
    //
    // Step frequencies are deliberately coarse so user-driven slider values
    // land on round numbers instead of artefacts like 0.2676 or 0.6783 —
    // the original playground used (max-min)/1000 which gave those. The
    // default map:
    //   fractional [0..1, ease Y ranges, saturation/opacity/hue]   → 0.05
    //   period seconds [0..60, 0.5..30]                            → 0.5
    //   short period (Swipe / fade curve)                          → 0.1
    //   exposure [-2..2]                                           → 0.1
    //   blend seconds [0..5]                                       → 0.1
    //   dBFS (MinDbfs / MaxDbfs)                                   → 1
    //   sim RMS values [0..0.3]                                    → 0.01
    //   WedgeCount (int)                                           → 1
    //
    // Pre-existing defaults may not align with these grids (e.g.
    // SwipeRiseAlpha=0.22, SwipeDecayAlpha=0.06); the initial Value is
    // displayed verbatim and only snaps to the grid on the first user
    // interaction. Direct keyboard input via the NumberBox still accepts
    // any value within range.

    private void BuildTuningPanel()
    {
        // Keep the "Tunables" title from the XAML, clear anything below it
        // (in case this is ever invoked twice), then append the expanders.
        while (TuningStack.Children.Count > 1)
            TuningStack.Children.RemoveAt(TuningStack.Children.Count - 1);

        AddPaletteExpander();
        AddConicFadeExpander();
        AddHueRotationExpander();
        AddArcRotationExpander();
        AddSwipeExpander();
        AddRecordingExpander();
        AddTranscribingExpander();
        AddRewritingExpander();
        AddAudioMappingExpander();
        AddSimulatedRmsExpander();
        AddParkedExpander();
    }

    // ── Expander groups ─────────────────────────────────────────────────

    private void AddPaletteExpander()
    {
        var stack = NewExpander("Palette (OKLCh)", ResetPalette);
        AddFloatRow(stack, "OklchLightness", 0, 1, 0.05, _tuning.OklchLightness,
            v => _tuning.OklchLightness = (float)v, rebuild: true);
        AddFloatRow(stack, "OklchChroma", 0, 0.4, 0.05, _tuning.OklchChroma,
            v => _tuning.OklchChroma = (float)v, rebuild: true);
        AddFloatRow(stack, "HueStart", 0, 1, 0.05, _tuning.HueStart,
            v => _tuning.HueStart = (float)v, rebuild: true);
        AddFloatRow(stack, "HueRange", 0, 1, 0.05, _tuning.HueRange,
            v => _tuning.HueRange = (float)v, rebuild: true);
        AddIntRow(stack, "WedgeCount", 16, 720, _tuning.WedgeCount,
            v => _tuning.WedgeCount = v, rebuild: true);
    }

    private void ResetPalette()
    {
        var d = new TuningModel();
        _tuning.OklchLightness = d.OklchLightness;
        _tuning.OklchChroma    = d.OklchChroma;
        _tuning.HueStart       = d.HueStart;
        _tuning.HueRange       = d.HueRange;
        _tuning.WedgeCount     = d.WedgeCount;
        RebuildTuningPanel();
    }

    private void AddConicFadeExpander()
    {
        var stack = NewExpander("Conic fade & span", ResetConicFade);
        AddFloatRow(stack, "ConicSpanTurns", 0.05, 1.0, 0.05, _tuning.ConicSpanTurns,
            v => _tuning.ConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicLeadFadeTurns", 0, 1, 0.05, _tuning.ConicLeadFadeTurns,
            v => _tuning.ConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicTailFadeTurns", 0, 1, 0.05, _tuning.ConicTailFadeTurns,
            v => _tuning.ConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ConicFadeCurve", 0.5, 10, 0.1, _tuning.ConicFadeCurve,
            v => _tuning.ConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "ArcMirror", _tuning.ArcMirror,
            v => _tuning.ArcMirror = v, rebuild: true);
    }

    private void ResetConicFade()
    {
        var d = new TuningModel();
        _tuning.ConicSpanTurns     = d.ConicSpanTurns;
        _tuning.ConicLeadFadeTurns = d.ConicLeadFadeTurns;
        _tuning.ConicTailFadeTurns = d.ConicTailFadeTurns;
        _tuning.ConicFadeCurve     = d.ConicFadeCurve;
        _tuning.ArcMirror          = d.ArcMirror;
        RebuildTuningPanel();
    }

    private void AddHueRotationExpander()
    {
        var stack = NewExpander("Hue rotation", ResetHueRotation);
        AddFloatRow(stack, "HuePeriodSeconds", 0, 60, 0.5, _tuning.HuePeriodSeconds,
            v => _tuning.HuePeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "HueDirection", _tuning.HueDirection,
            v => _tuning.HueDirection = v);
        AddFloatRow(stack, "HueEaseP1.X", 0, 1, 0.05, _tuning.HueEaseP1X,
            v => _tuning.HueEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP1.Y", -0.5, 1.5, 0.05, _tuning.HueEaseP1Y,
            v => _tuning.HueEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.X", 0, 1, 0.05, _tuning.HueEaseP2X,
            v => _tuning.HueEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "HueEaseP2.Y", -0.5, 1.5, 0.05, _tuning.HueEaseP2Y,
            v => _tuning.HueEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetHueRotation()
    {
        var d = new TuningModel();
        _tuning.HuePeriodSeconds = d.HuePeriodSeconds;
        _tuning.HueDirection     = d.HueDirection;
        _tuning.HueEaseP1X       = d.HueEaseP1X;
        _tuning.HueEaseP1Y       = d.HueEaseP1Y;
        _tuning.HueEaseP2X       = d.HueEaseP2X;
        _tuning.HueEaseP2Y       = d.HueEaseP2Y;
        RebuildTuningPanel();
    }

    private void AddArcRotationExpander()
    {
        var stack = NewExpander("Arc rotation", ResetArcRotation);
        AddFloatRow(stack, "ArcPeriodSeconds", 0.5, 30, 0.5, _tuning.ArcPeriodSeconds,
            v => _tuning.ArcPeriodSeconds = v, rebuild: true);
        AddDirectionRow(stack, "ArcDirection", _tuning.ArcDirection,
            v => _tuning.ArcDirection = v);
        AddFloatRow(stack, "ArcEaseP1.X", 0, 1, 0.05, _tuning.ArcEaseP1X,
            v => _tuning.ArcEaseP1X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP1.Y", -0.5, 1.5, 0.05, _tuning.ArcEaseP1Y,
            v => _tuning.ArcEaseP1Y = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.X", 0, 1, 0.05, _tuning.ArcEaseP2X,
            v => _tuning.ArcEaseP2X = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcEaseP2.Y", -0.5, 1.5, 0.05, _tuning.ArcEaseP2Y,
            v => _tuning.ArcEaseP2Y = (float)v, rebuild: true);
    }

    private void ResetArcRotation()
    {
        var d = new TuningModel();
        _tuning.ArcPeriodSeconds = d.ArcPeriodSeconds;
        _tuning.ArcDirection     = d.ArcDirection;
        _tuning.ArcEaseP1X       = d.ArcEaseP1X;
        _tuning.ArcEaseP1Y       = d.ArcEaseP1Y;
        _tuning.ArcEaseP2X       = d.ArcEaseP2X;
        _tuning.ArcEaseP2Y       = d.ArcEaseP2Y;
        RebuildTuningPanel();
    }

    private void AddSwipeExpander()
    {
        var stack = NewExpander("Swipe (Transcribing / Rewriting)", ResetSwipe);
        // Static mutables on HudChrono — read live each vsync, no rebuild.
        AddFloatRow(stack, "SwipeCycleSeconds", 0.1, 6.0, 0.1,
            HudChrono.SwipeCycleSeconds,
            v => HudChrono.SwipeCycleSeconds = (float)v);
        AddFloatRow(stack, "SwipeEaseP1.X", 0, 1, 0.05, HudChrono.SwipeEaseP1.X,
            v => HudChrono.SwipeEaseP1 = new Vector2((float)v, HudChrono.SwipeEaseP1.Y));
        AddFloatRow(stack, "SwipeEaseP1.Y", -0.5, 1.5, 0.05, HudChrono.SwipeEaseP1.Y,
            v => HudChrono.SwipeEaseP1 = new Vector2(HudChrono.SwipeEaseP1.X, (float)v));
        AddFloatRow(stack, "SwipeEaseP2.X", 0, 1, 0.05, HudChrono.SwipeEaseP2.X,
            v => HudChrono.SwipeEaseP2 = new Vector2((float)v, HudChrono.SwipeEaseP2.Y));
        AddFloatRow(stack, "SwipeEaseP2.Y", -0.5, 1.5, 0.05, HudChrono.SwipeEaseP2.Y,
            v => HudChrono.SwipeEaseP2 = new Vector2(HudChrono.SwipeEaseP2.X, (float)v));
        AddFloatRow(stack, "SwipeRiseAlpha", 0.05, 1.0, 0.05, HudChrono.SwipeRiseAlpha,
            v => HudChrono.SwipeRiseAlpha = (float)v);
        AddFloatRow(stack, "SwipeDecayAlpha", 0.05, 0.5, 0.05, HudChrono.SwipeDecayAlpha,
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
        RebuildTuningPanel();
        ApplyTarget();
    }

    private void AddRecordingExpander()
    {
        var stack = NewExpander("Recording", ResetRecording);
        AddFloatRow(stack, "RecordingConicSpanTurns", 0.05, 1, 0.05, _tuning.RecordingConicSpanTurns,
            v => _tuning.RecordingConicSpanTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicLeadFadeTurns", 0, 1, 0.05, _tuning.RecordingConicLeadFadeTurns,
            v => _tuning.RecordingConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicTailFadeTurns", 0, 1, 0.05, _tuning.RecordingConicTailFadeTurns,
            v => _tuning.RecordingConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingConicFadeCurve", 0.5, 10, 0.1, _tuning.RecordingConicFadeCurve,
            v => _tuning.RecordingConicFadeCurve = (float)v, rebuild: true);
        AddToggleRow(stack, "RecordingArcMirror", _tuning.RecordingArcMirror,
            v => _tuning.RecordingArcMirror = v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationDark", 0, 1, 0.05, _tuning.RecordingSaturationDark,
            v => _tuning.RecordingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingSaturationLight", 0, 1, 0.05, _tuning.RecordingSaturationLight,
            v => _tuning.RecordingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingHueShiftTurns", 0, 1, 0.05, _tuning.RecordingHueShiftTurns,
            v => _tuning.RecordingHueShiftTurns = (float)v, rebuild: true);
        // Exposure clamped to D2D1_EXPOSURE spec [-2, +2].
        AddFloatRow(stack, "RecordingExposureDark", -2, 2, 0.1, _tuning.RecordingExposureDark,
            v => _tuning.RecordingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingExposureLight", -2, 2, 0.1, _tuning.RecordingExposureLight,
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
        RebuildTuningPanel();
    }

    private void AddTranscribingExpander()
    {
        var stack = NewExpander("Transcribing", ResetTranscribing);
        AddFloatRow(stack, "TranscribingSaturationDark", 0, 1, 0.05, _tuning.TranscribingSaturationDark,
            v => _tuning.TranscribingSaturationDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingSaturationLight", 0, 1, 0.05, _tuning.TranscribingSaturationLight,
            v => _tuning.TranscribingSaturationLight = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingHueShiftTurns", 0, 1, 0.05, _tuning.TranscribingHueShiftTurns,
            v => _tuning.TranscribingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureDark", -2, 2, 0.1, _tuning.TranscribingExposureDark,
            v => _tuning.TranscribingExposureDark = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingExposureLight", -2, 2, 0.1, _tuning.TranscribingExposureLight,
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
        RebuildTuningPanel();
    }

    private void AddRewritingExpander()
    {
        var stack = NewExpander("Rewriting", ResetRewriting);
        AddFloatRow(stack, "RewritingSaturation", 0, 1, 0.05, _tuning.RewritingSaturation,
            v => _tuning.RewritingSaturation = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingHueShiftTurns", 0, 1, 0.05, _tuning.RewritingHueShiftTurns,
            v => _tuning.RewritingHueShiftTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingExposure", -2, 2, 0.1, _tuning.RewritingExposure,
            v => _tuning.RewritingExposure = (float)v, rebuild: true);
    }

    private void ResetRewriting()
    {
        var d = new TuningModel();
        _tuning.RewritingSaturation    = d.RewritingSaturation;
        _tuning.RewritingHueShiftTurns = d.RewritingHueShiftTurns;
        _tuning.RewritingExposure      = d.RewritingExposure;
        RebuildTuningPanel();
    }

    private void AddAudioMappingExpander()
    {
        var stack = NewExpander("Audio mapping (Recording)", ResetAudioMapping);
        // Static mutables on HudChrono — no rebuild, read live each sample.
        AddFloatRow(stack, "EmaAlpha", 0, 1, 0.05, HudChrono.EmaAlpha,
            v => HudChrono.EmaAlpha = (float)v);
        AddFloatRow(stack, "MinDbfs", -80, 0, 1, HudChrono.MinDbfs,
            v => HudChrono.MinDbfs = (float)v);
        AddFloatRow(stack, "MaxDbfs", -60, 0, 1, HudChrono.MaxDbfs,
            v => HudChrono.MaxDbfs = (float)v);
        AddFloatRow(stack, "DbfsCurveExponent", 0.5, 4, 0.05, HudChrono.DbfsCurveExponent,
            v => HudChrono.DbfsCurveExponent = (float)v);
    }

    private void ResetAudioMapping()
    {
        HudChrono.EmaAlpha          = 0.72f;
        HudChrono.MinDbfs           = -40f;
        HudChrono.MaxDbfs           = -22f;
        HudChrono.DbfsCurveExponent = 2.0f;
        RebuildTuningPanel();
    }

    private void AddSimulatedRmsExpander()
    {
        var stack = NewExpander("Simulated RMS (Recording only)", ResetSimulatedRms);
        // Step 0.001 (not 0.05/0.01) — the RMS range is 0..0.3 and the
        // meaningful defaults (0.013 = engine gate at -38 dBFS, 0.100 =
        // conversational mid -20 dBFS) need 3 decimals to display cleanly.
        // Coarser grid would snap 0.013 → 0.01 and destroy the semantic.
        AddFloatRow(stack, "SimRmsMin", 0, 0.3, 0.001, _simRmsMin,
            v => _simRmsMin = (float)v);
        AddFloatRow(stack, "SimRmsMax", 0, 0.3, 0.001, _simRmsMax,
            v => _simRmsMax = (float)v);
        AddFloatRow(stack, "SimRmsPeriodSeconds", 0.2, 10, 0.1, _simRmsPeriodSeconds,
            v => _simRmsPeriodSeconds = (float)v);
        AddToggleRow(stack, "Manual override", _simManualOverride,
            v => _simManualOverride = v);
        AddFloatRow(stack, "SimRmsManualValue", 0, 0.3, 0.001, _simManualValue,
            v => _simManualValue = (float)v);
    }

    private void ResetSimulatedRms()
    {
        _simRmsMin           = 0.013f;
        _simRmsMax           = 0.100f;
        _simRmsPeriodSeconds = 2.0f;
        _simManualOverride   = false;
        _simManualValue      = 0.012f;
        RebuildTuningPanel();
    }

    // Parked expander — fields only observable during a variant transition
    // or at stroke creation. Muted by construction in the single-target
    // playground; collapsed by default so they don't drown the active
    // knobs, but kept visible for default-tweaking.
    private void AddParkedExpander()
    {
        var stack = NewExpander("Variant transitions — parked", ResetParked, expanded: false);
        AddFloatRow(stack, "HuePhaseTurns", 0, 1, 0.05, _tuning.HuePhaseTurns,
            v => _tuning.HuePhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcPhaseTurns", 0, 1, 0.05, _tuning.ArcPhaseTurns,
            v => _tuning.ArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingArcPhaseTurns", 0, 1, 0.05, _tuning.RecordingArcPhaseTurns,
            v => _tuning.RecordingArcPhaseTurns = (float)v, rebuild: true);
        AddFloatRow(stack, "HueMinSpeedFraction", 0, 1, 0.05, _tuning.HueMinSpeedFraction,
            v => _tuning.HueMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "ArcMinSpeedFraction", 0, 1, 0.05, _tuning.ArcMinSpeedFraction,
            v => _tuning.ArcMinSpeedFraction = (float)v, rebuild: true);
        AddFloatRow(stack, "RecordingBlendSeconds", 0, 5, 0.1, _tuning.RecordingBlendSeconds,
            v => _tuning.RecordingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RecordingHuePeriodSeconds", 0, 60, 0.5, _tuning.RecordingHuePeriodSeconds,
            v => _tuning.RecordingHuePeriodSeconds = v, rebuild: true);
        AddFloatRow(stack, "TranscribingOpacity", 0, 1, 0.05, _tuning.TranscribingOpacity,
            v => _tuning.TranscribingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "TranscribingBlendSeconds", 0, 5, 0.1, _tuning.TranscribingBlendSeconds,
            v => _tuning.TranscribingBlendSeconds = v, rebuild: true);
        AddFloatRow(stack, "RewritingOpacity", 0, 1, 0.05, _tuning.RewritingOpacity,
            v => _tuning.RewritingOpacity = (float)v, rebuild: true);
        AddFloatRow(stack, "RewritingBlendSeconds", 0, 5, 0.1, _tuning.RewritingBlendSeconds,
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
        RebuildTuningPanel();
    }

    // Rebuild the whole tuning panel from the current _tuning state. Used
    // by every Reset*() — the alternative (tracking per-row controls in
    // a dictionary) would be more code for the same effect. The whole
    // panel is only dozens of rows of UI, so a wholesale reconstruction
    // is cheap and keeps the row factories tight.
    private void RebuildTuningPanel()
    {
        BuildTuningPanel();
        RequestRebuild();
    }

    // ── Expander factory ────────────────────────────────────────────────
    //
    // Expander header hosts the section title and an aligned-right "Reset"
    // HyperlinkButton. The HyperlinkButton short-circuits the click
    // (e.Handled = true in its own handler) so tapping Reset doesn't
    // collapse the expander.

    private StackPanel NewExpander(string title, Action resetAction, bool expanded = true)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var headerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleTb, 0);
        headerGrid.Children.Add(titleTb);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset",
            VerticalAlignment = VerticalAlignment.Center,
            // Tight padding so the Reset label doesn't bloat the header.
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, -4, 8, -4),
        };
        ToolTipService.SetToolTip(resetBtn, "Reset this section to defaults");
        resetBtn.Click += (_, _) => resetAction();
        Grid.SetColumn(resetBtn, 1);
        headerGrid.Children.Add(resetBtn);

        var expander = new Expander
        {
            Header = headerGrid,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
        };
        TuningStack.Children.Add(expander);
        return content;
    }

    // ── Row factories ───────────────────────────────────────────────────
    //
    // Slider + NumberBox composite. Both share the same value source; the
    // `_syncing` guard prevents the ValueChanged feedback loop. Rebuild-
    // triggering rows push through RequestRebuild so the CanvasDevice
    // doesn't thrash mid-drag.
    //
    // Layout: 3-column Grid [label 160 | slider * | NumberBox 120].
    // NumberBox MinWidth 120 dip fits "-99.99" + spin buttons without
    // clipping; the label column is narrower than before because the
    // tuning column is resizable now — if Louis wants more label room
    // he drags the sash.

    private void AddFloatRow(
        StackPanel stack, string label,
        double min, double max, double step, double value,
        Action<double> setter,
        bool rebuild = false)
    {
        // Clean up float→double promotion noise in the displayed default.
        // TuningModel stores floats; promoting to double for Slider.Value /
        // NumberBox.Value exposes binary-precision artefacts (0.4f shows
        // as 0.4000006, 0.013f as 0.01300027). Rounding to the step's
        // decimal count strips the noise without altering semantics.
        // digits formula: step 1 → 0, 0.5/0.1 → 1, 0.05/0.01 → 2, 0.001 → 3.
        int digits = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)));
        double displayValue = Math.Round(value, digits);

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = displayValue,
            StepFrequency = step,
            SmallChange = step,
            LargeChange = step * 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        var numberBox = new NumberBox
        {
            Value = displayValue,
            Minimum = min, Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
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
            MinWidth = 120,
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
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
