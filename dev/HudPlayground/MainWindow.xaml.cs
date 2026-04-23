using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WhispUI.Composition;
using WhispUI.Controls;

namespace HudPlayground;

// Single-preview playground. The SelectorBar picks one of 7 targets —
// the 4 HudChrono states (Charging / Recording / Transcribing /
// Rewriting) plus the 3 naked diagnostic previews (Conic palette /
// ArcMask alpha / Combined masks without silhouette). Exactly one is
// live at any moment: the others are torn down so Louis isn't fighting
// four concurrent Composition graphs while tuning the brush he
// actually wants to observe.
//
// This is the rewrite after the first-pass playground (which ran all
// four states + three naked masks concurrently) exposed two issues:
//   1. Cross-talk between the four concurrent Composition graphs —
//      rebuilds on one surface destabilised animations on another,
//      with Forever animations going silent mid-cycle. Single-preview
//      removes the class of bug entirely by keeping at most one graph
//      alive at any moment.
//   2. Tunables that were surfaced speculatively but had no visible
//      effect on a static target (BlendSeconds, Opacity runtime slots,
//      PhaseTurns on spinning strokes, *MinSpeedFraction now that the
//      out-in ease curve removes the plateau). Parked in the "Variant
//      transitions" expander at the bottom, collapsed by default.
//
// The play/pause toggle tears down the active preview entirely when
// unchecked — Composition doesn't expose a clean "pause all animations
// on this graph" so we just dispose. On re-check, the preview is
// rebuilt from scratch (same path as a slider rebuild). Good enough
// for tuning; not a feature for shipping.
public sealed partial class MainWindow : Window
{
    // Target enum drives both the SelectorBar selection and the preview
    // wiring. Keep the names aligned with the SelectorBarItem.Text so
    // the string-tag matching in OnTargetSelectionChanged stays trivial.
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

    private readonly TuningModel    _tuning   = new();
    private readonly DispatcherTimer _rmsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch      _rmsClock = new();

    // Simulated RMS tunables — live, no rebuild needed (the pump reads
    // these fields directly on each 50 ms tick).
    // Sim RMS defaults chosen to mimic the RMS distribution of normal
    // conversational speech (50 ms window average):
    //   0.013 linear ≈ -38 dBFS — engine gate threshold, "breath".
    //   0.100 linear ≈ -20 dBFS — normal conversational mid-range.
    // Sweep between the two over 2 s produces the breathing stroke
    // animation Louis sees during a real recording. Old defaults
    // (0 .. 0.025) never left -32 dBFS so the stroke never lit up past
    // ~0.3 opacity under the new RMS window.
    private float _simRmsMin           = 0.013f;
    private float _simRmsMax           = 0.100f;
    private float _simRmsPeriodSeconds = 2.0f;
    private bool  _simManualOverride   = false;
    private float _simManualValue      = 0.012f;

    // Current playground state. `_currentTarget` mirrors the selected
    // SelectorBarItem; `_isPlaying` reflects the toggle. Both can
    // change independently — changing target while paused just updates
    // which preview *would* be rebuilt on next Play, without touching
    // the current (torn-down) preview.
    private Target _currentTarget             = Target.Transcribing;
    private bool   _isPlaying                 = true;

    // When Transcribing / Rewriting are selected, simulate a handful of
    // "digit was modified during Recording" flags so the swipe reveal
    // has something to paint red. Otherwise the swipe runs (wave
    // progress advances) but every character stays primary and the
    // motion is invisible. Default ON because observing the swipe is
    // the main reason Louis selects Transcribing / Rewriting in the
    // playground.
    private bool _simulateChangedDigits = true;

    // ── Rebuild debounce ─────────────────────────────────────────────
    //
    // Paint-time slider changes rebuild the Win2D surfaces from scratch
    // (Canvas drawing session + oversized pxSquare × pxSquare bitmap
    // allocation + GPU upload). On a drag that's one rebuild per
    // DispatcherQueue tick — easily 50–100/s — and the CanvasDevice
    // chokes, visuals freeze, sometimes the whole composition thread
    // stalls for a second.
    //
    // A single DispatcherTimer accumulates a "rebuild pending" flag
    // while the slider is hot; on every change we Stop+Start (resets
    // the countdown) so the actual rebuild only fires 300 ms after the
    // last slider movement. Drag-to-release feels instant; mid-drag
    // the slider value/text updates live but the preview keeps
    // animating off its previous surfaces.
    private readonly DispatcherTimer _rebuildDebounce = new()
        { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _rebuildPending;

    public MainWindow()
    {
        InitializeComponent();

        // Window sized to fit all three columns at their default widths
        // with some breathing room on the middle rail (so the preview
        // doesn't dock against the splitters).
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 960));

        // Bind the log ListView to the shared DevLog collection. Because
        // DevLog.Entries is an ObservableCollection, new entries appear
        // automatically — we only need to hook EntryAppended to pin the
        // viewport to the tail (ListView wouldn't auto-scroll otherwise).
        LogListView.ItemsSource = DevLog.Entries;
        DevLog.EntryAppended += ScrollLogToTail;

        // ── Composition instrumentation ─────────────────────────────────
        //
        // Subscribe to HudComposition's process-wide diagnostic events so
        // the log shows every stroke creation / disposal with its unique
        // CreationId + the current live count. When LiveStrokeCount
        // climbs beyond 1 without decrementing, the Dispose chain is
        // failing — that's the freeze bug's signature (compositor
        // saturated, Forever animations go silent).
        //
        // Both events may fire from Composition's render thread (or,
        // for DeviceLost, from Win2D's internal worker) — marshal to
        // the UI thread via the window's DispatcherQueue before
        // touching the ObservableCollection DevLog owns.
        HudComposition.StrokeLifecycle   += OnStrokeLifecycle;
        HudComposition.CanvasDeviceLost  += OnCanvasDeviceLost;

        _rebuildDebounce.Tick += (_, _) =>
        {
            _rebuildDebounce.Stop();
            if (!_rebuildPending) return;
            _rebuildPending = false;
            ApplyTarget();
            DevLog.Write("REBUILD", $"applied target={_currentTarget}");
        };

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                BuildTuningPanel();
                ApplyTarget();
                DevLog.Write("LIFECYCLE", $"Playground ready (target={_currentTarget})");
            };
        }

        this.Closed += (_, _) =>
        {
            _rmsTimer.Stop();
            _rmsClock.Stop();
            _rebuildDebounce.Stop();
            DevLog.EntryAppended            -= ScrollLogToTail;
            HudComposition.StrokeLifecycle  -= OnStrokeLifecycle;
            HudComposition.CanvasDeviceLost -= OnCanvasDeviceLost;
        };
    }

    // Stroke creation / disposal callback. Invoked from whichever thread
    // Composition marshalled the event on — can be the render thread.
    // DispatcherQueue.TryEnqueue hops back onto the UI thread before
    // touching DevLog (which updates an ObservableCollection bound to
    // the ListView).
    private void OnStrokeLifecycle(int creationId, string eventName)
    {
        this.DispatcherQueue.TryEnqueue(() =>
            DevLog.Write("STROKE",
                $"#{creationId} {eventName} — total={HudComposition.TotalStrokesCreated} " +
                $"live={HudComposition.LiveStrokeCount}"));
    }

    // Device-lost callback — SURPRISE event, fires exactly once when the
    // shared CanvasDevice invalidates. Logged red-hot ("CRITICAL") so
    // it stands out from the routine STROKE / REBUILD traffic. If this
    // fires right when Louis sees the animation freeze, the Dispose
    // leak isn't the whole story and we need a device-recovery path.
    private void OnCanvasDeviceLost(string reason)
    {
        this.DispatcherQueue.TryEnqueue(() =>
            DevLog.Write("CRITICAL", reason));
    }

    // ── Target selection + play/pause ───────────────────────────────

    private void OnTargetSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
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
        DevLog.Write("STATE", $"target → {_currentTarget}");
        ApplyTarget();
    }

    private void OnPlayToggleClicked(object sender, RoutedEventArgs e)
    {
        _isPlaying = PlayToggle.IsChecked ?? false;
        PlayToggleLabel.Text = _isPlaying ? "Playing" : "Paused";
        DevLog.Write("STATE", _isPlaying ? "resumed" : "paused");
        ApplyTarget();
    }

    // Core of the playground. Tears down the current preview
    // unconditionally, then (if playing) brings up whichever preview
    // matches _currentTarget. Called on target change, play/pause
    // toggle, and debounced slider rebuild. Calling this is a full
    // reset — no fancy incremental updates — because the composition
    // graph geometry is baked at creation and in-place tweaks are
    // limited to the four live PropertySet scalars (Saturation, Hue,
    // Exposure, Opacity); everything else requires a rebuild.
    private void ApplyTarget()
    {
        // Tear down everything.
        ChronoPreview.ApplyState(HudState.Hidden);
        ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
        StopRmsPump();

        if (!_isPlaying)
        {
            ChronoPreview.Visibility    = Visibility.Visible;   // stays visible showing the hidden state card
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

        // HudChrono path. ApplyState owns the stroke attach (Recording
        // path differs from Transcribing / Rewriting — see HudChrono).
        var state = _currentTarget switch
        {
            Target.Charging    => HudState.Charging,
            Target.Recording   => HudState.Recording,
            Target.Transcribing => HudState.Transcribing,
            _                  => HudState.Rewriting,
        };

        // Arm the tuning-config override BEFORE ApplyState so the single
        // stroke creation inside AttachProcessingVisual picks it up —
        // otherwise ApplyState would create one stroke with shipping
        // defaults and we'd immediately dispose + recreate via
        // RebuildStroke, doubling the lifecycle churn per target change.
        // Charging has no stroke — override is harmless, it just won't
        // be consumed and will stick around until the next state that
        // does create one (not a problem: playground always rebuilds
        // via ApplyTarget which re-arms).
        if (_currentTarget is Target.Recording or Target.Transcribing or Target.Rewriting)
            ChronoPreview.SetNextStrokeConfig(_tuning.ToConfig());
        ChronoPreview.ApplyState(state);

        // Simulate changed digits for the swipe preview, then start the
        // RMS pump if the target is Recording.
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

    // ── Naked preview attach ────────────────────────────────────────

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
        DevLog.Write("REBUILD", $"naked preview attached ({part})");
    }

    private static readonly Vector2 NakedHudSize = new(272f, 78f);
    private const           float   NakedHostDim = 300f;

    // ── Rebuild dispatcher ──────────────────────────────────────────
    //
    // Slider handlers that mutate `_tuning` call this to schedule a
    // debounced rebuild. Non-rebuild tunables (static mutables on
    // HudChrono, simulated RMS parameters) don't go through this path —
    // they read live on the next vsync / sample.
    private void RequestRebuild()
    {
        _rebuildPending = true;
        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    // ── RMS pump ────────────────────────────────────────────────────
    //
    // Runs only while Recording is the active target. DispatcherTimer
    // at 20 Hz (matches the audio engine cadence). Either a sine sweep
    // between SimRmsMin and SimRmsMax, or a manual override slider.
    private void StartRmsPump()
    {
        if (_rmsTimer.IsEnabled) return;
        _rmsClock.Restart();
        _rmsTimer.Tick -= RmsTimerTick;
        _rmsTimer.Tick += RmsTimerTick;
        _rmsTimer.Start();
        DevLog.Write("RMS", $"pump started (sweep {_simRmsMin:F3}..{_simRmsMax:F3} over {_simRmsPeriodSeconds:F2}s)");
    }

    private void StopRmsPump()
    {
        if (!_rmsTimer.IsEnabled) return;
        _rmsTimer.Stop();
        _rmsTimer.Tick -= RmsTimerTick;
        _rmsClock.Stop();
        DevLog.Write("RMS", "pump stopped");
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

    // Auto-scroll the ListView to the tail on each new entry.
    private void ScrollLogToTail()
    {
        if (DevLog.Entries.Count == 0) return;
        var last = DevLog.Entries[^1];
        LogListView.ScrollIntoView(last);
    }

    // ── Tuning panel construction ───────────────────────────────────
    //
    // ~60 sliders organised into expanders by concern. Code-behind
    // (not XAML) because the setter lambda visually anchors to the
    // slider declaration, which keeps the "describe / range / target"
    // triple together when scanning the file.
    //
    // The last expander ("Variant transitions — parked") groups the
    // fields that only have a visible effect during a transition
    // between variants. The playground holds a single target at a
    // time, so these are muted by construction — kept visible so
    // Louis can still tweak the default for the shipping app, but
    // collapsed by default so they don't drown the active tunables.

    private void BuildTuningPanel()
    {
        TuningStack.Children.Clear();
        TuningStack.Children.Add(new TextBlock
        {
            Text = "Tunables",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Margin = new Thickness(0, 0, 0, 8),
        });

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

    private void AddPaletteExpander()
    {
        var stack = NewExpander("Palette (baked, OKLCh)");
        // OklchLightness 0..1: 0=black, 1=white; ~0.75 vivid mid-tone.
        AddFloatSlider(stack, "OklchLightness", 0, 1, _tuning.OklchLightness,
            v => _tuning.OklchLightness = (float)v, rebuild: true);
        // OklchChroma 0..0.4: 0=greyscale, ~0.15 vivid pastel, ~0.22
        // near gamut limit at L=0.75. Above ~0.25 colours clip hard on
        // most hues — the slider allows exploration into the clipped
        // range so Louis can see what the wall feels like.
        AddFloatSlider(stack, "OklchChroma", 0, 0.4, _tuning.OklchChroma,
            v => _tuning.OklchChroma = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueStart", 0, 1, _tuning.HueStart,
            v => _tuning.HueStart = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueRange", 0, 1, _tuning.HueRange,
            v => _tuning.HueRange = (float)v, rebuild: true);
        AddIntSlider(stack, "WedgeCount", 16, 720, _tuning.WedgeCount,
            v => _tuning.WedgeCount = v, rebuild: true);
    }

    private void AddConicFadeExpander()
    {
        var stack = NewExpander("Conic fade & span");
        AddFloatSlider(stack, "ConicSpanTurns", 0.05, 1.0, _tuning.ConicSpanTurns,
            v => _tuning.ConicSpanTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "ConicLeadFadeTurns", 0, 1, _tuning.ConicLeadFadeTurns,
            v => _tuning.ConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "ConicTailFadeTurns", 0, 1, _tuning.ConicTailFadeTurns,
            v => _tuning.ConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "ConicFadeCurve", 0.5, 10, _tuning.ConicFadeCurve,
            v => _tuning.ConicFadeCurve = (float)v, rebuild: true);
        AddToggle(stack, "ArcMirror", _tuning.ArcMirror,
            v => _tuning.ArcMirror = v, rebuild: true);
    }

    private void AddHueRotationExpander()
    {
        var stack = NewExpander("Hue rotation");
        AddFloatSlider(stack, "HuePeriodSeconds", 0, 60, _tuning.HuePeriodSeconds,
            v => _tuning.HuePeriodSeconds = v, rebuild: true);
        AddDirectionToggle(stack, "HueDirection (CW / CCW)", _tuning.HueDirection,
            v => _tuning.HueDirection = v);
        AddFloatSlider(stack, "HueEaseP1.X", 0, 1, _tuning.HueEaseP1X,
            v => _tuning.HueEaseP1X = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueEaseP1.Y", -0.5, 1.5, _tuning.HueEaseP1Y,
            v => _tuning.HueEaseP1Y = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueEaseP2.X", 0, 1, _tuning.HueEaseP2X,
            v => _tuning.HueEaseP2X = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueEaseP2.Y", -0.5, 1.5, _tuning.HueEaseP2Y,
            v => _tuning.HueEaseP2Y = (float)v, rebuild: true);
    }

    private void AddArcRotationExpander()
    {
        var stack = NewExpander("Arc rotation");
        AddFloatSlider(stack, "ArcPeriodSeconds", 0.5, 30, _tuning.ArcPeriodSeconds,
            v => _tuning.ArcPeriodSeconds = v, rebuild: true);
        AddDirectionToggle(stack, "ArcDirection (CW / CCW)", _tuning.ArcDirection,
            v => _tuning.ArcDirection = v);
        AddFloatSlider(stack, "ArcEaseP1.X", 0, 1, _tuning.ArcEaseP1X,
            v => _tuning.ArcEaseP1X = (float)v, rebuild: true);
        AddFloatSlider(stack, "ArcEaseP1.Y", -0.5, 1.5, _tuning.ArcEaseP1Y,
            v => _tuning.ArcEaseP1Y = (float)v, rebuild: true);
        AddFloatSlider(stack, "ArcEaseP2.X", 0, 1, _tuning.ArcEaseP2X,
            v => _tuning.ArcEaseP2X = (float)v, rebuild: true);
        AddFloatSlider(stack, "ArcEaseP2.Y", -0.5, 1.5, _tuning.ArcEaseP2Y,
            v => _tuning.ArcEaseP2Y = (float)v, rebuild: true);
    }

    private void AddSwipeExpander()
    {
        var stack = NewExpander("Swipe (Transcribing / Rewriting)");
        // Static mutables on HudChrono; they read live on the next
        // vsync, so no stroke rebuild needed.
        AddFloatSlider(stack, "SwipeCycleSeconds", 0.1, 6.0,
            HudChrono.SwipeCycleSeconds,
            v => HudChrono.SwipeCycleSeconds = (float)v);
        AddFloatSlider(stack, "SwipeEaseP1.X", 0, 1, HudChrono.SwipeEaseP1.X,
            v => HudChrono.SwipeEaseP1 = new Vector2((float)v, HudChrono.SwipeEaseP1.Y));
        AddFloatSlider(stack, "SwipeEaseP1.Y", -0.5, 1.5, HudChrono.SwipeEaseP1.Y,
            v => HudChrono.SwipeEaseP1 = new Vector2(HudChrono.SwipeEaseP1.X, (float)v));
        AddFloatSlider(stack, "SwipeEaseP2.X", 0, 1, HudChrono.SwipeEaseP2.X,
            v => HudChrono.SwipeEaseP2 = new Vector2((float)v, HudChrono.SwipeEaseP2.Y));
        AddFloatSlider(stack, "SwipeEaseP2.Y", -0.5, 1.5, HudChrono.SwipeEaseP2.Y,
            v => HudChrono.SwipeEaseP2 = new Vector2(HudChrono.SwipeEaseP2.X, (float)v));
        // Per-digit heat rise / decay. Rise controls how fast a digit
        // reaches full red once the swipe head lands on it; Decay
        // controls how slowly it fades back to primary. Keep Decay
        // smaller than Rise to preserve the comet-trail character of
        // the wave — if Decay ≥ Rise the digits look like they flash
        // simultaneously, not sequentially.
        AddFloatSlider(stack, "SwipeRiseAlpha", 0.02, 1.0, HudChrono.SwipeRiseAlpha,
            v => HudChrono.SwipeRiseAlpha = (float)v);
        AddFloatSlider(stack, "SwipeDecayAlpha", 0.01, 0.5, HudChrono.SwipeDecayAlpha,
            v => HudChrono.SwipeDecayAlpha = (float)v);
        // Controls whether the preview fakes "digits changed during
        // Recording" flags on Transcribing / Rewriting targets. ON = the
        // swipe wave is visible (sec/cs digits flip to red when the
        // head passes). OFF = matches shipping behaviour on a first
        // entry into Transcribing after a zero-digit-change recording
        // (vanishingly rare but possible).
        AddToggle(stack, "Simulate changed digits",
            _simulateChangedDigits,
            v => { _simulateChangedDigits = v; ApplyTarget(); });
    }

    private void AddRecordingExpander()
    {
        var stack = NewExpander("Recording");
        AddFloatSlider(stack, "RecordingConicSpanTurns", 0.05, 1, _tuning.RecordingConicSpanTurns,
            v => _tuning.RecordingConicSpanTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingConicLeadFadeTurns", 0, 1, _tuning.RecordingConicLeadFadeTurns,
            v => _tuning.RecordingConicLeadFadeTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingConicTailFadeTurns", 0, 1, _tuning.RecordingConicTailFadeTurns,
            v => _tuning.RecordingConicTailFadeTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingConicFadeCurve", 0.5, 10, _tuning.RecordingConicFadeCurve,
            v => _tuning.RecordingConicFadeCurve = (float)v, rebuild: true);
        AddToggle(stack, "RecordingArcMirror", _tuning.RecordingArcMirror,
            v => _tuning.RecordingArcMirror = v, rebuild: true);
        AddFloatSlider(stack, "RecordingSaturationDark", 0, 1, _tuning.RecordingSaturationDark,
            v => _tuning.RecordingSaturationDark = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingSaturationLight", 0, 1, _tuning.RecordingSaturationLight,
            v => _tuning.RecordingSaturationLight = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingHueShiftTurns", 0, 1, _tuning.RecordingHueShiftTurns,
            v => _tuning.RecordingHueShiftTurns = (float)v, rebuild: true);
        // Exposure range clamped to [-2, +2] — ExposureEffect wraps
        // D2D1_EXPOSURE whose spec only validates values in this range.
        // Values outside the spec produce driver-level assertions on
        // rebuild (symptom observed: Transcribing Exposure slider crash
        // with -4..+4 range), so we cap the playground UI to the valid
        // range instead of relying on the effect to clamp internally.
        AddFloatSlider(stack, "RecordingExposureDark", -2, 2, _tuning.RecordingExposureDark,
            v => _tuning.RecordingExposureDark = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingExposureLight", -2, 2, _tuning.RecordingExposureLight,
            v => _tuning.RecordingExposureLight = (float)v, rebuild: true);
    }

    private void AddTranscribingExpander()
    {
        var stack = NewExpander("Transcribing");
        AddFloatSlider(stack, "TranscribingSaturationDark", 0, 1, _tuning.TranscribingSaturationDark,
            v => _tuning.TranscribingSaturationDark = (float)v, rebuild: true);
        AddFloatSlider(stack, "TranscribingSaturationLight", 0, 1, _tuning.TranscribingSaturationLight,
            v => _tuning.TranscribingSaturationLight = (float)v, rebuild: true);
        AddFloatSlider(stack, "TranscribingHueShiftTurns", 0, 1, _tuning.TranscribingHueShiftTurns,
            v => _tuning.TranscribingHueShiftTurns = (float)v, rebuild: true);
        // Exposure clamped to D2D1_EXPOSURE spec [-2, +2] — see
        // AddRecordingExpander for the crash context.
        AddFloatSlider(stack, "TranscribingExposureDark", -2, 2, _tuning.TranscribingExposureDark,
            v => _tuning.TranscribingExposureDark = (float)v, rebuild: true);
        AddFloatSlider(stack, "TranscribingExposureLight", -2, 2, _tuning.TranscribingExposureLight,
            v => _tuning.TranscribingExposureLight = (float)v, rebuild: true);
    }

    private void AddRewritingExpander()
    {
        var stack = NewExpander("Rewriting");
        AddFloatSlider(stack, "RewritingSaturation", 0, 1, _tuning.RewritingSaturation,
            v => _tuning.RewritingSaturation = (float)v, rebuild: true);
        AddFloatSlider(stack, "RewritingHueShiftTurns", 0, 1, _tuning.RewritingHueShiftTurns,
            v => _tuning.RewritingHueShiftTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "RewritingExposure", -2, 2, _tuning.RewritingExposure,
            v => _tuning.RewritingExposure = (float)v, rebuild: true);
    }

    private void AddAudioMappingExpander()
    {
        var stack = NewExpander("Audio mapping (Recording)");
        // Static mutables on HudChrono — no rebuild, read live each sample.
        AddFloatSlider(stack, "EmaAlpha", 0, 1, HudChrono.EmaAlpha,
            v => HudChrono.EmaAlpha = (float)v);
        AddFloatSlider(stack, "MinDbfs", -80, 0, HudChrono.MinDbfs,
            v => HudChrono.MinDbfs = (float)v);
        AddFloatSlider(stack, "MaxDbfs", -60, 0, HudChrono.MaxDbfs,
            v => HudChrono.MaxDbfs = (float)v);
        // Power curve on the dBFS→opacity mapping. 1 = linear; 2
        // (default) compresses the low half and expands the high
        // half — matches the "conversational speech sits mid-ramp,
        // only emphatic speech hits ceiling" target. Values up to 4
        // exaggerate the effect further for tuning experiments.
        AddFloatSlider(stack, "DbfsCurveExponent", 0.5, 4, HudChrono.DbfsCurveExponent,
            v => HudChrono.DbfsCurveExponent = (float)v);
    }

    private void AddSimulatedRmsExpander()
    {
        var stack = NewExpander("Simulated RMS (Recording only)");
        // Slider ranges extended to 0.3 (≈ -10 dBFS) so the simulated
        // sweep can reach the MaxDbfs clipping ceiling (-14 dBFS ≈ 0.2
        // linear). Old range 0..0.05 capped out at -26 dBFS which left
        // the upper half of the opacity ramp unreachable from the
        // playground — Louis had to load a real recording to see the
        // stroke saturate.
        AddFloatSlider(stack, "SimRmsMin", 0, 0.3, _simRmsMin,
            v => _simRmsMin = (float)v);
        AddFloatSlider(stack, "SimRmsMax", 0, 0.3, _simRmsMax,
            v => _simRmsMax = (float)v);
        AddFloatSlider(stack, "SimRmsPeriodSeconds", 0.2, 10, _simRmsPeriodSeconds,
            v => _simRmsPeriodSeconds = (float)v);
        AddToggle(stack, "Manual override", _simManualOverride,
            v => _simManualOverride = v);
        AddFloatSlider(stack, "SimRmsManualValue", 0, 0.3, _simManualValue,
            v => _simManualValue = (float)v);
    }

    // Parked expander — fields whose effect is only observable during a
    // variant transition or at the moment of stroke creation. With a
    // single-target playground these are muted, but kept visible for
    // inventory / default-tweaking purposes. Collapsed by default.
    //
    //   PhaseTurns          baked into the stroke's initial rotation
    //                       offset. After one period the cycle has
    //                       wrapped and the phase is indistinguishable
    //                       from any other start. Useful on Recording
    //                       where Arc is frozen and phase parks the
    //                       lobes, so still worth a tweak slot.
    //   *BlendSeconds       duration of the cross-fade between two
    //                       variants. No transition in the playground =
    //                       nothing to blend.
    //   RewritingOpacity /
    //   TranscribingOpacity  animated by ApplyVariant during a
    //                       transition; the baseline is 1 and a fixed
    //                       target holds opacity at 1 irrespective of
    //                       this slider.
    //   RecordingHuePeriodSeconds  non-zero only makes sense when
    //                       Recording saturation is > 0 (at Sat = 0 the
    //                       HSV palette collapses to a uniform value
    //                       regardless of hue). Default 0 (freeze).
    //   *MinSpeedFraction   made vestigial by the out-in ease curve
    //                       (0.125, 0.375, 0.875, 0.625) whose endpoint
    //                       tangents don't plateau — the linear blend
    //                       the floor provides has nothing to rescue.
    //                       Kept for exotic ease experimentation.
    private void AddParkedExpander()
    {
        var stack = NewExpander("Variant transitions — parked");
        // Expander stays collapsed by default — reach the last-added
        // Expander in TuningStack and flip its IsExpanded.
        if (TuningStack.Children[^1] is Expander exp) exp.IsExpanded = false;

        AddFloatSlider(stack, "HuePhaseTurns", 0, 1, _tuning.HuePhaseTurns,
            v => _tuning.HuePhaseTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "ArcPhaseTurns", 0, 1, _tuning.ArcPhaseTurns,
            v => _tuning.ArcPhaseTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingArcPhaseTurns", 0, 1, _tuning.RecordingArcPhaseTurns,
            v => _tuning.RecordingArcPhaseTurns = (float)v, rebuild: true);
        AddFloatSlider(stack, "HueMinSpeedFraction", 0, 1, _tuning.HueMinSpeedFraction,
            v => _tuning.HueMinSpeedFraction = (float)v, rebuild: true);
        AddFloatSlider(stack, "ArcMinSpeedFraction", 0, 1, _tuning.ArcMinSpeedFraction,
            v => _tuning.ArcMinSpeedFraction = (float)v, rebuild: true);
        AddFloatSlider(stack, "RecordingBlendSeconds", 0, 5, _tuning.RecordingBlendSeconds,
            v => _tuning.RecordingBlendSeconds = v, rebuild: true);
        AddFloatSlider(stack, "RecordingHuePeriodSeconds", 0, 60, _tuning.RecordingHuePeriodSeconds,
            v => _tuning.RecordingHuePeriodSeconds = v, rebuild: true);
        AddFloatSlider(stack, "TranscribingOpacity", 0, 1, _tuning.TranscribingOpacity,
            v => _tuning.TranscribingOpacity = (float)v, rebuild: true);
        AddFloatSlider(stack, "TranscribingBlendSeconds", 0, 5, _tuning.TranscribingBlendSeconds,
            v => _tuning.TranscribingBlendSeconds = v, rebuild: true);
        AddFloatSlider(stack, "RewritingOpacity", 0, 1, _tuning.RewritingOpacity,
            v => _tuning.RewritingOpacity = (float)v, rebuild: true);
        AddFloatSlider(stack, "RewritingBlendSeconds", 0, 5, _tuning.RewritingBlendSeconds,
            v => _tuning.RewritingBlendSeconds = v, rebuild: true);
    }

    // ── Control factories ───────────────────────────────────────────
    //
    // Each row sits in a 3-column Grid (label / slider / value) so the
    // column alignment stays consistent across every expander.

    private StackPanel NewExpander(string title)
    {
        var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
        var expander = new Expander
        {
            Header = title,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            IsExpanded = true,
        };
        TuningStack.Children.Add(expander);
        return content;
    }

    private Slider AddFloatSlider(
        StackPanel parent, string label,
        double min, double max, double value,
        Action<double> setter,
        bool rebuild = false)
    {
        var valueTb = new TextBlock
        {
            Text = value.ToString("F3"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = (max - min) / 1000.0,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        slider.ValueChanged += (_, e) =>
        {
            setter(e.NewValue);
            valueTb.Text = e.NewValue.ToString("F3");
            if (rebuild) RequestRebuild();
        };
        parent.Children.Add(WrapSliderRow(label, slider, valueTb));
        return slider;
    }

    private Slider AddIntSlider(
        StackPanel parent, string label,
        int min, int max, int value,
        Action<int> setter,
        bool rebuild = false)
    {
        var valueTb = new TextBlock
        {
            Text = value.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            StepFrequency = 1,
            SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        slider.ValueChanged += (_, e) =>
        {
            int iv = (int)Math.Round(e.NewValue);
            setter(iv);
            valueTb.Text = iv.ToString();
            if (rebuild) RequestRebuild();
        };
        parent.Children.Add(WrapSliderRow(label, slider, valueTb));
        return slider;
    }

    private void AddToggle(
        StackPanel parent, string label,
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

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
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
        parent.Children.Add(grid);
    }

    // Dedicated toggle for the rotation direction: the underlying field
    // is a `float` that the StartRotation math multiplies through, but
    // semantically only -1 (CCW) and +1 (CW) are meaningful values.
    // Fractional values smuggle a hidden speed multiplier into the
    // rotation, which is a trap. The ToggleSwitch snaps strictly to
    // ±1 so the slider path can't introduce ambiguity.
    private void AddDirectionToggle(
        StackPanel parent, string label,
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

        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
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
        parent.Children.Add(grid);
    }

    private static Grid WrapSliderRow(string label, Slider slider, TextBlock valueTb)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueTb, 2);
        grid.Children.Add(labelTb);
        grid.Children.Add(slider);
        grid.Children.Add(valueTb);
        return grid;
    }
}
