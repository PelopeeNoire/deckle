using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Controls;

namespace HudPlayground;

// Hosts the four HudChrono state previews on the left rail and a
// scrollable expander column of sliders on the right, one slider per
// tunable. Every slider rebuilds the subset of HudChrono strokes it
// affects via HudChrono.RebuildStroke so Louis can eyeball a change
// immediately — no WhispUI.exe relaunch, no hotkey cycle.
//
// Rebuild scope per slider category:
//   - Static mutables (HudChrono.SwipeCycleSeconds, SwipeEaseP*, EmaAlpha,
//     MinDbfs, MaxDbfs) mutate field value; no stroke rebuild needed.
//     Swipe tunables affect Transcribing + Rewriting; audio mapping
//     affects Recording only (via UpdateAudioLevel path).
//   - Shared paint-time (HsvSaturation, HueStart, WedgeCount, ConicSpan,
//     Hue*, Arc*, ConicFade*, ArcMirror) rebuilds Transcribing + Rewriting.
//     Recording has its own Recording* paint-time slots so it's untouched.
//   - Rewriting runtime rebuilds Rewriting only.
//   - Transcribing runtime rebuilds Transcribing only.
//   - Recording runtime + Recording paint-time rebuild Recording only.
//
// The simulated RMS pump is a 20 Hz DispatcherTimer (matching the
// shipping audio engine cadence) that feeds HudChrono.UpdateAudioLevel
// on the Recording preview. By default it sweeps a sine between
// SimRmsMin and SimRmsMax; a Manual override toggle replaces the sine
// with a directly-drivable slider.
public sealed partial class MainWindow : Window
{
    private readonly TuningModel   _tuning    = new();
    private readonly DispatcherTimer _rmsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch      _rmsClock  = new();

    // Simulated RMS tunables — live, no rebuild needed (the pump reads
    // these fields directly on each 50 ms tick).
    private float _simRmsMin            = 0f;
    private float _simRmsMax            = 0.025f;
    private float _simRmsPeriodSeconds  = 2.0f;
    private bool  _simManualOverride    = false;
    private float _simManualValue       = 0.012f;

    [Flags]
    private enum StrokeTarget
    {
        None         = 0,
        Recording    = 1 << 0,
        Transcribing = 1 << 1,
        Rewriting    = 1 << 2,
        Shared       = Transcribing | Rewriting,
        All          = Recording | Transcribing | Rewriting,
    }

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 1020));

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                // Pin each preview to its state. Order is cosmetic —
                // each ApplyState is independent.
                ChargingPreview.ApplyState(HudState.Charging);
                RecordingPreview.ApplyState(HudState.Recording);
                TranscribingPreview.ApplyState(HudState.Transcribing);
                RewritingPreview.ApplyState(HudState.Rewriting);

                BuildTuningPanel();
                StartRmsPump();
            };
        }

        this.Closed += (_, _) =>
        {
            _rmsTimer.Stop();
            _rmsClock.Stop();
        };
    }

    // ── RMS pump ─────────────────────────────────────────────────────────
    //
    // Runs on the UI thread via DispatcherTimer (20 Hz mirrors the audio
    // engine's 50 ms sample rate). Feeds HudChrono.UpdateAudioLevel on
    // the Recording instance only — the other states ignore RMS.

    private void StartRmsPump()
    {
        _rmsClock.Restart();
        _rmsTimer.Tick += (_, _) =>
        {
            float rms;
            if (_simManualOverride)
            {
                rms = _simManualValue;
            }
            else
            {
                // Sine sweep centered at (min+max)/2 with amplitude
                // (max-min)/2. The 0.5+0.5*sin form keeps output in
                // [min, max] and the phase is purely time-driven so
                // the sweep is continuous across slider changes.
                double t = _rmsClock.Elapsed.TotalSeconds;
                float sweep = (float)(0.5 + 0.5 * Math.Sin(
                    2 * Math.PI * t / Math.Max(0.1, _simRmsPeriodSeconds)));
                rms = _simRmsMin + sweep * (_simRmsMax - _simRmsMin);
            }
            RecordingPreview.UpdateAudioLevel(rms);
        };
        _rmsTimer.Start();
    }

    // ── Stroke rebuild dispatch ──────────────────────────────────────────

    private void RebuildStrokes(StrokeTarget mask)
    {
        var cfg = _tuning.ToConfig();
        if (mask.HasFlag(StrokeTarget.Recording))
            RecordingPreview.RebuildStroke(cfg);
        if (mask.HasFlag(StrokeTarget.Transcribing))
            TranscribingPreview.RebuildStroke(cfg);
        if (mask.HasFlag(StrokeTarget.Rewriting))
            RewritingPreview.RebuildStroke(cfg);
    }

    // ── Tuning panel construction ────────────────────────────────────────
    //
    // Sliders are built programmatically in code because (a) there are
    // ~60 of them, (b) grouping them in XAML would hide the setter
    // lambda next to the slider declaration, (c) code keeps the
    // "description → min/max/setter → mask" triple visually close.

    private void BuildTuningPanel()
    {
        TuningStack.Children.Clear();
        TuningStack.Children.Add(new TextBlock
        {
            Text = "Tunables",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Margin = new Thickness(0, 0, 0, 8),
        });

        AddSwipeExpander();
        AddHueRotationExpander();
        AddArcRotationExpander();
        AddConicFadeExpander();
        AddPaletteExpander();
        AddRecordingExpander();
        AddTranscribingExpander();
        AddRewritingExpander();
        AddAudioMappingExpander();
        AddSimulatedRmsExpander();
    }

    private void AddSwipeExpander()
    {
        var stack = NewExpander("Swipe");
        // Swipe tunables are static fields on HudChrono; they read live
        // on the next vsync, so no stroke rebuild needed.
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
    }

    private void AddHueRotationExpander()
    {
        var stack = NewExpander("Hue rotation (Transcribing / Rewriting)");
        AddFloatSlider(stack, "HuePeriodSeconds", 0, 60, _tuning.HuePeriodSeconds,
            v => _tuning.HuePeriodSeconds = v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueDirection (±1)", -1, 1, _tuning.HueDirection,
            v => _tuning.HueDirection = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HuePhaseTurns", 0, 1, _tuning.HuePhaseTurns,
            v => _tuning.HuePhaseTurns = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueEaseP1.X", 0, 1, _tuning.HueEaseP1X,
            v => _tuning.HueEaseP1X = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueEaseP1.Y", -0.5, 1.5, _tuning.HueEaseP1Y,
            v => _tuning.HueEaseP1Y = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueEaseP2.X", 0, 1, _tuning.HueEaseP2X,
            v => _tuning.HueEaseP2X = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueEaseP2.Y", -0.5, 1.5, _tuning.HueEaseP2Y,
            v => _tuning.HueEaseP2Y = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueVelocityFloor", 0, 2, _tuning.HueVelocityFloor,
            v => _tuning.HueVelocityFloor = (float)v, StrokeTarget.Shared);
    }

    private void AddArcRotationExpander()
    {
        var stack = NewExpander("Arc rotation (Transcribing / Rewriting)");
        AddFloatSlider(stack, "ArcPeriodSeconds", 0.5, 30, _tuning.ArcPeriodSeconds,
            v => _tuning.ArcPeriodSeconds = v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcDirection (±1)", -1, 1, _tuning.ArcDirection,
            v => _tuning.ArcDirection = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcPhaseTurns", 0, 1, _tuning.ArcPhaseTurns,
            v => _tuning.ArcPhaseTurns = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcEaseP1.X", 0, 1, _tuning.ArcEaseP1X,
            v => _tuning.ArcEaseP1X = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcEaseP1.Y", -0.5, 1.5, _tuning.ArcEaseP1Y,
            v => _tuning.ArcEaseP1Y = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcEaseP2.X", 0, 1, _tuning.ArcEaseP2X,
            v => _tuning.ArcEaseP2X = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcEaseP2.Y", -0.5, 1.5, _tuning.ArcEaseP2Y,
            v => _tuning.ArcEaseP2Y = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ArcVelocityFloor", 0, 2, _tuning.ArcVelocityFloor,
            v => _tuning.ArcVelocityFloor = (float)v, StrokeTarget.Shared);
        AddToggle(stack, "ArcMirror", _tuning.ArcMirror,
            v => _tuning.ArcMirror = v, StrokeTarget.Shared);
    }

    private void AddConicFadeExpander()
    {
        var stack = NewExpander("Conic fade & span (Transcribing / Rewriting)");
        AddFloatSlider(stack, "ConicSpanTurns", 0.05, 1.0, _tuning.ConicSpanTurns,
            v => _tuning.ConicSpanTurns = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ConicLeadFadeTurns", 0, 1, _tuning.ConicLeadFadeTurns,
            v => _tuning.ConicLeadFadeTurns = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ConicTailFadeTurns", 0, 1, _tuning.ConicTailFadeTurns,
            v => _tuning.ConicTailFadeTurns = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "ConicFadeCurve", 0.5, 10, _tuning.ConicFadeCurve,
            v => _tuning.ConicFadeCurve = (float)v, StrokeTarget.Shared);
        AddIntSlider(stack, "WedgeCount", 16, 720, _tuning.WedgeCount,
            v => _tuning.WedgeCount = v, StrokeTarget.Shared);
    }

    private void AddPaletteExpander()
    {
        var stack = NewExpander("Palette (Transcribing / Rewriting baked)");
        AddFloatSlider(stack, "HsvSaturation", 0, 1, _tuning.HsvSaturation,
            v => _tuning.HsvSaturation = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HsvValue", 0, 1, _tuning.HsvValue,
            v => _tuning.HsvValue = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueStart", 0, 1, _tuning.HueStart,
            v => _tuning.HueStart = (float)v, StrokeTarget.Shared);
        AddFloatSlider(stack, "HueRange", 0, 1, _tuning.HueRange,
            v => _tuning.HueRange = (float)v, StrokeTarget.Shared);
    }

    private void AddRecordingExpander()
    {
        var stack = NewExpander("Recording");
        AddFloatSlider(stack, "RecordingConicSpanTurns", 0.05, 1, _tuning.RecordingConicSpanTurns,
            v => _tuning.RecordingConicSpanTurns = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingConicLeadFadeTurns", 0, 1, _tuning.RecordingConicLeadFadeTurns,
            v => _tuning.RecordingConicLeadFadeTurns = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingConicTailFadeTurns", 0, 1, _tuning.RecordingConicTailFadeTurns,
            v => _tuning.RecordingConicTailFadeTurns = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingConicFadeCurve", 0.5, 10, _tuning.RecordingConicFadeCurve,
            v => _tuning.RecordingConicFadeCurve = (float)v, StrokeTarget.Recording);
        AddToggle(stack, "RecordingArcMirror", _tuning.RecordingArcMirror,
            v => _tuning.RecordingArcMirror = v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingArcPhaseTurns", 0, 1, _tuning.RecordingArcPhaseTurns,
            v => _tuning.RecordingArcPhaseTurns = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingSaturationDark", 0, 1, _tuning.RecordingSaturationDark,
            v => _tuning.RecordingSaturationDark = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingSaturationLight", 0, 1, _tuning.RecordingSaturationLight,
            v => _tuning.RecordingSaturationLight = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingHueShiftTurns", 0, 1, _tuning.RecordingHueShiftTurns,
            v => _tuning.RecordingHueShiftTurns = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingExposureDark", -4, 4, _tuning.RecordingExposureDark,
            v => _tuning.RecordingExposureDark = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingExposureLight", -4, 4, _tuning.RecordingExposureLight,
            v => _tuning.RecordingExposureLight = (float)v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingBlendSeconds", 0, 5, _tuning.RecordingBlendSeconds,
            v => _tuning.RecordingBlendSeconds = v, StrokeTarget.Recording);
        AddFloatSlider(stack, "RecordingHuePeriodSeconds", 0, 60, _tuning.RecordingHuePeriodSeconds,
            v => _tuning.RecordingHuePeriodSeconds = v, StrokeTarget.Recording);
    }

    private void AddTranscribingExpander()
    {
        var stack = NewExpander("Transcribing");
        AddFloatSlider(stack, "TranscribingSaturationDark", 0, 1, _tuning.TranscribingSaturationDark,
            v => _tuning.TranscribingSaturationDark = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingSaturationLight", 0, 1, _tuning.TranscribingSaturationLight,
            v => _tuning.TranscribingSaturationLight = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingHueShiftTurns", 0, 1, _tuning.TranscribingHueShiftTurns,
            v => _tuning.TranscribingHueShiftTurns = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingExposureDark", -4, 4, _tuning.TranscribingExposureDark,
            v => _tuning.TranscribingExposureDark = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingExposureLight", -4, 4, _tuning.TranscribingExposureLight,
            v => _tuning.TranscribingExposureLight = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingOpacity", 0, 1, _tuning.TranscribingOpacity,
            v => _tuning.TranscribingOpacity = (float)v, StrokeTarget.Transcribing);
        AddFloatSlider(stack, "TranscribingBlendSeconds", 0, 5, _tuning.TranscribingBlendSeconds,
            v => _tuning.TranscribingBlendSeconds = v, StrokeTarget.Transcribing);
    }

    private void AddRewritingExpander()
    {
        var stack = NewExpander("Rewriting");
        AddFloatSlider(stack, "RewritingSaturation", 0, 1, _tuning.RewritingSaturation,
            v => _tuning.RewritingSaturation = (float)v, StrokeTarget.Rewriting);
        AddFloatSlider(stack, "RewritingHueShiftTurns", 0, 1, _tuning.RewritingHueShiftTurns,
            v => _tuning.RewritingHueShiftTurns = (float)v, StrokeTarget.Rewriting);
        AddFloatSlider(stack, "RewritingExposure", -4, 4, _tuning.RewritingExposure,
            v => _tuning.RewritingExposure = (float)v, StrokeTarget.Rewriting);
        AddFloatSlider(stack, "RewritingOpacity", 0, 1, _tuning.RewritingOpacity,
            v => _tuning.RewritingOpacity = (float)v, StrokeTarget.Rewriting);
        AddFloatSlider(stack, "RewritingBlendSeconds", 0, 5, _tuning.RewritingBlendSeconds,
            v => _tuning.RewritingBlendSeconds = v, StrokeTarget.Rewriting);
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
    }

    private void AddSimulatedRmsExpander()
    {
        var stack = NewExpander("Simulated RMS");
        AddFloatSlider(stack, "SimRmsMin", 0, 0.05, _simRmsMin,
            v => _simRmsMin = (float)v);
        AddFloatSlider(stack, "SimRmsMax", 0, 0.05, _simRmsMax,
            v => _simRmsMax = (float)v);
        AddFloatSlider(stack, "SimRmsPeriodSeconds", 0.2, 10, _simRmsPeriodSeconds,
            v => _simRmsPeriodSeconds = (float)v);
        AddToggle(stack, "Manual override", _simManualOverride,
            v => _simManualOverride = v);
        AddFloatSlider(stack, "SimRmsManualValue", 0, 0.05, _simManualValue,
            v => _simManualValue = (float)v);
    }

    // ── Control factories ────────────────────────────────────────────────
    //
    // Each slider sits in a 3-column Grid (label / slider / value-readout)
    // so the column alignment stays consistent across every expander.

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
        };
        TuningStack.Children.Add(expander);
        return content;
    }

    private Slider AddFloatSlider(
        StackPanel parent, string label,
        double min, double max, double value,
        Action<double> setter,
        StrokeTarget rebuild = StrokeTarget.None)
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
            if (rebuild != StrokeTarget.None) RebuildStrokes(rebuild);
        };
        parent.Children.Add(WrapSliderRow(label, slider, valueTb));
        return slider;
    }

    private Slider AddIntSlider(
        StackPanel parent, string label,
        int min, int max, int value,
        Action<int> setter,
        StrokeTarget rebuild = StrokeTarget.None)
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
            if (rebuild != StrokeTarget.None) RebuildStrokes(rebuild);
        };
        parent.Children.Add(WrapSliderRow(label, slider, valueTb));
        return slider;
    }

    private void AddToggle(
        StackPanel parent, string label,
        bool value, Action<bool> setter,
        StrokeTarget rebuild = StrokeTarget.None)
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
            if (rebuild != StrokeTarget.None) RebuildStrokes(rebuild);
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

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
