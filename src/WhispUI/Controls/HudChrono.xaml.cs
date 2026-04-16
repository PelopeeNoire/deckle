using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WhispUI.Composition;

namespace WhispUI.Controls;

// Chrono + status dot + processing-surface overlay (stroke + shadows).
//
// Owns the Bitcount Single MM.SS.cc clock, the progressive digit accent
// (each digit that ever changed locks to SystemFillColorCriticalBrush until
// the next ApplyState(Recording) reset), and the Composition mount point
// for the Transcribing / Rewriting overlays.
//
// The vsync rendering hook (CompositionTarget.Rendering) drives the clock
// — no DispatcherTimer, no jitter when the UI thread is busy.
public sealed partial class HudChrono : UserControl
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private bool _renderingHooked;

    private int _lastMin = -1;
    private int _lastSec = -1;
    private int _lastCs  = -1;

    // Brushes resolved on the UI thread via Application.Resources so they
    // follow the live theme. Re-resolved on ActualThemeChanged (a Foreground
    // assigned in code does not track ThemeResource bindings).
    private Brush _digitAccentBrush = null!;
    private bool _tMin1, _tMin2, _tSec1, _tSec2, _tCs1, _tCs2;

    private HudState _state = HudState.Hidden;
    private Visual? _processingVisual;

    public HudChrono()
    {
        InitializeComponent();

        _digitAccentBrush = ResolveCriticalBrush();
        ChronoRoot.ActualThemeChanged += (_, _) =>
        {
            _digitAccentBrush = ResolveCriticalBrush();
            if (_tMin1) Min1.Foreground = _digitAccentBrush;
            if (_tMin2) Min2.Foreground = _digitAccentBrush;
            if (_tSec1) Sec1.Foreground = _digitAccentBrush;
            if (_tSec2) Sec2.Foreground = _digitAccentBrush;
            if (_tCs1)  Cs1.Foreground  = _digitAccentBrush;
            if (_tCs2)  Cs2.Foreground  = _digitAccentBrush;
        };
    }

    // Resolved at runtime from Application.Resources so theme switches still
    // update the brush (System resource keys flip color across light/dark).
    private static Brush ResolveCriticalBrush() =>
        (Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush)
        ?? new SolidColorBrush(Microsoft.UI.Colors.IndianRed);

    private static Brush ResolveNeutralBrush() =>
        (Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush)
        ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    // Single state-driven entry point. Called by HudWindow.SetState.
    public void ApplyState(HudState next)
    {
        _state = next;
        switch (next)
        {
            case HudState.Charging:
                ApplyCharging();
                break;
            case HudState.Recording:
                ApplyRecording();
                break;
            case HudState.Transcribing:
                ApplyTranscribing();
                break;
            case HudState.Rewriting:
                ApplyRewriting();
                break;
            case HudState.Hidden:
            case HudState.Message:
                ApplyHidden();
                break;
        }
    }

    private void ApplyCharging()
    {
        _stopwatch.Reset();
        UnhookRendering();
        DetachProcessingVisual();

        _lastMin = _lastSec = _lastCs = -1;
        _tMin1 = _tMin2 = _tSec1 = _tSec2 = _tCs1 = _tCs2 = false;
        Min1.Text = Min2.Text = "0";
        Sec1.Text = Sec2.Text = "0";
        Cs1.Text  = Cs2.Text  = "0";

        var neutral = ResolveNeutralBrush();
        Min1.Foreground = neutral; Min2.Foreground = neutral;
        Sec1.Foreground = neutral; Sec2.Foreground = neutral;
        Cs1.Foreground  = neutral; Cs2.Foreground  = neutral;
        StatusDot.Fill  = neutral;
    }

    private void ApplyRecording()
    {
        _stopwatch.Restart();
        DetachProcessingVisual();

        _lastMin = _lastSec = _lastCs = -1;
        _tMin1 = _tMin2 = _tSec1 = _tSec2 = _tCs1 = _tCs2 = false;
        Min1.Text = Min2.Text = "0";
        Sec1.Text = Sec2.Text = "0";
        Cs1.Text  = Cs2.Text  = "0";

        // Clear local Foreground so each Run inherits ClockText.Foreground
        // (theme-resource-bound) until UpdateClock relocks the changed
        // digits to the accent.
        Min1.ClearValue(TextElement.ForegroundProperty);
        Min2.ClearValue(TextElement.ForegroundProperty);
        Sec1.ClearValue(TextElement.ForegroundProperty);
        Sec2.ClearValue(TextElement.ForegroundProperty);
        Cs1.ClearValue(TextElement.ForegroundProperty);
        Cs2.ClearValue(TextElement.ForegroundProperty);

        StatusDot.Fill = ResolveCriticalBrush();

        UpdateClock();
        HookRendering();
    }

    private void ApplyTranscribing()
    {
        // Freeze the clock at its last value — the digits stay readable
        // while the transcribing stroke + shadow play around the surface.
        _stopwatch.Stop();
        UnhookRendering();
        UpdateClock();

        StatusDot.Fill = ResolveNeutralBrush();
        AttachProcessingVisual(HudComposition.CreateTranscribingStroke);
    }

    private void ApplyRewriting()
    {
        // Same freeze as Transcribing — only the overlay differs.
        _stopwatch.Stop();
        UnhookRendering();
        UpdateClock();

        StatusDot.Fill = ResolveNeutralBrush();
        AttachProcessingVisual(HudComposition.CreateRewritingStroke);
    }

    private void ApplyHidden()
    {
        _stopwatch.Stop();
        UnhookRendering();
        DetachProcessingVisual();
    }

    private void AttachProcessingVisual(Func<Compositor, Vector2, ContainerVisual> factory)
    {
        DetachProcessingVisual();

        var hostVisual = ElementCompositionPreview.GetElementVisual(ProcessingSurfaceHost);
        var compositor = hostVisual.Compositor;

        // ActualWidth/Height may be 0 on first attach if the layout pass has
        // not run yet. Fall back to the chrono nominal size so the visual is
        // valid; the next layout pass will not auto-resize it, but Charging/
        // Recording reset the surface anyway and the user reaches Transcribing
        // only after the HUD has been measured at least once.
        float w = (float)ProcessingSurfaceHost.ActualWidth;
        float h = (float)ProcessingSurfaceHost.ActualHeight;
        if (w <= 0f) w = 314f;
        if (h <= 0f) h = 78f;

        _processingVisual = factory(compositor, new Vector2(w, h));
        ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, _processingVisual);
    }

    private void DetachProcessingVisual()
    {
        if (_processingVisual is null) return;
        ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
        _processingVisual = null;
    }

    private void HookRendering()
    {
        if (_renderingHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private void OnRendering(object? sender, object e) => UpdateClock();

    private void UpdateClock()
    {
        var elapsed = _stopwatch.Elapsed;
        int totalMin = (int)elapsed.TotalMinutes;
        int min = totalMin % 100;
        int sec = elapsed.Seconds;
        int cs  = elapsed.Milliseconds / 10;

        if (min != _lastMin)
        {
            int d1 = min / 10, d2 = min % 10;
            if (Min1.Text != d1.ToString()) { Min1.Text = d1.ToString(); if (!_tMin1) { _tMin1 = true; Min1.Foreground = _digitAccentBrush; } }
            if (Min2.Text != d2.ToString()) { Min2.Text = d2.ToString(); if (!_tMin2) { _tMin2 = true; Min2.Foreground = _digitAccentBrush; } }
            _lastMin = min;
        }
        if (sec != _lastSec)
        {
            int d1 = sec / 10, d2 = sec % 10;
            if (Sec1.Text != d1.ToString()) { Sec1.Text = d1.ToString(); if (!_tSec1) { _tSec1 = true; Sec1.Foreground = _digitAccentBrush; } }
            if (Sec2.Text != d2.ToString()) { Sec2.Text = d2.ToString(); if (!_tSec2) { _tSec2 = true; Sec2.Foreground = _digitAccentBrush; } }
            _lastSec = sec;
        }
        if (cs != _lastCs)
        {
            int d1 = cs / 10, d2 = cs % 10;
            if (Cs1.Text != d1.ToString()) { Cs1.Text = d1.ToString(); if (!_tCs1) { _tCs1 = true; Cs1.Foreground = _digitAccentBrush; } }
            if (Cs2.Text != d2.ToString()) { Cs2.Text = d2.ToString(); if (!_tCs2) { _tCs2 = true; Cs2.Foreground = _digitAccentBrush; } }
            _lastCs = cs;
        }
    }
}
