using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WhispUI.Composition;

namespace WhispUI.Controls;

// Chrono card — container + clock + processing stroke attach.
//
// Owns the Bitcount Single MM.SS.cc clock and the progressive digit accent
// (each digit that ever changed locks to SystemFillColorCriticalBrush until
// the next ApplyState(Recording) reset). Stroke sources:
//   - DWM frame (always on)     — 1-dip system accent stroke on the rounded
//                                  HWND silhouette (DWMWA_BORDER_COLOR =
//                                  DWMWA_COLOR_DEFAULT in HudWindow). Plays
//                                  the role of the permanent "Windows frame".
//   - Composition accent (state) — 1-dip stroke 1 dip inside the HWND, added
//                                  on top of DWM for Transcribing (diagonal
//                                  gradient) and Rewriting (8 colored arcs).
// The two layers are at different inset positions, so they never overlap
// pixel-wise — DWM at the outer edge, Composition 1 dip inside.
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

            // Transcribing exposure is theme-aware (Dark vs Light split).
            // Re-apply the variant on live theme change so the arc
            // brightness matches the new substrate immediately.
            if (_state == HudState.Transcribing && _processingStroke != null)
            {
                _processingStroke.ApplyVariant(
                    ProcessingVariant.Transcribing,
                    ChronoRoot.ActualTheme == ElementTheme.Dark);
            }

            // Recording outline is a single opaque theme-coloured stroke —
            // Retheme swaps the ColorBrush live so a dark↔light flip mid-
            // recording doesn't leave the outline stuck on the old palette.
            _recordingOutline?.Retheme(ResolvePrimaryTextColor());
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

    // Theme colour for the recording outline. Uses TextFillColorPrimary
    // (white on dark, black on light, with the brush's built-in contrast
    // alpha). Returned as a raw Windows.UI.Color because CompositionColorBrush
    // takes Color, not Brush.
    private static Color ResolvePrimaryTextColor() =>
        (Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush)?.Color
        ?? Microsoft.UI.Colors.White;

    // Single state-driven entry point. Called by HudWindow.SetState.
    internal void ApplyState(HudState next)
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

        _lastMin = _lastSec = _lastCs = -1;
        _tMin1 = _tMin2 = _tSec1 = _tSec2 = _tCs1 = _tCs2 = false;
        Min1.Text = Min2.Text = "0";
        Sec1.Text = Sec2.Text = "0";
        Cs1.Text  = Cs2.Text  = "0";

        var neutral = ResolveNeutralBrush();
        Min1.Foreground = neutral; Min2.Foreground = neutral;
        Sec1.Foreground = neutral; Sec2.Foreground = neutral;
        Cs1.Foreground  = neutral; Cs2.Foreground  = neutral;

        DetachProcessingVisual();
        DetachRecordingOutline();
    }

    private void ApplyRecording()
    {
        _stopwatch.Restart();

        // Both overlays are mutually exclusive on ProcessingSurfaceHost —
        // detach any lingering processing stroke before attaching the
        // recording outline.
        DetachProcessingVisual();
        AttachRecordingOutline();

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

        UpdateClock();
        HookRendering();
    }

    private void ApplyTranscribing()
    {
        // Freeze the clock at its last value.
        _stopwatch.Stop();
        UnhookRendering();

        // Order matters: UpdateClock first to write the final elapsed value
        // (which may relock the last-changed digit to the red accent because
        // the stopwatch advanced between the last vsync tick and the Stop),
        // then ResetDigitAccent to clear that accent. Reversed, we'd reset
        // first and UpdateClock would immediately repaint the freshly-changed
        // centisecond digit in red — exactly the "stuck red digit" symptom.
        UpdateClock();
        ResetDigitAccent();

        DetachRecordingOutline();
        AttachProcessingVisual(ProcessingVariant.Transcribing);
    }

    private void ApplyRewriting()
    {
        _stopwatch.Stop();
        UnhookRendering();

        UpdateClock();
        ResetDigitAccent();

        DetachRecordingOutline();
        AttachProcessingVisual(ProcessingVariant.Rewriting);
    }

    // Drops the per-digit "ever-changed" accent flags and clears the local
    // Foreground on each Run so they fall back to ClockText.Foreground
    // (TextFillColorPrimaryBrush, theme-tracked). Called when the chrono
    // freezes (Transcribing/Rewriting) so the red accent accumulated during
    // Recording disappears on stop.
    private void ResetDigitAccent()
    {
        _tMin1 = _tMin2 = _tSec1 = _tSec2 = _tCs1 = _tCs2 = false;
        Min1.ClearValue(TextElement.ForegroundProperty);
        Min2.ClearValue(TextElement.ForegroundProperty);
        Sec1.ClearValue(TextElement.ForegroundProperty);
        Sec2.ClearValue(TextElement.ForegroundProperty);
        Cs1.ClearValue(TextElement.ForegroundProperty);
        Cs2.ClearValue(TextElement.ForegroundProperty);
    }

    private void ApplyHidden()
    {
        _stopwatch.Stop();
        UnhookRendering();

        DetachProcessingVisual();
        DetachRecordingOutline();
    }

    // ── Composition stroke attach ─────────────────────────────────────────────
    //
    // ProcessingSurfaceHost (XAML Border) is the attach point for the
    // Composition ShapeVisual produced by HudComposition. The visual sits
    // above ChronoCard and below the ClockText in the Grid z-order, so its
    // stroke paints on the card surface but the clock text reads on top.
    //
    // Fallback dims (272, 78) catch the pre-layout attach (ActualWidth/Height
    // are 0 before the first measure pass). The visual is not auto-resized
    // on subsequent layout passes — acceptable here because Charging/Recording
    // always resets the surface, and Transcribing/Rewriting only fire after
    // at least one full chrono measure.
    //
    // Single persistent stroke, live-modulated variants. The stroke is
    // created once on first enter into a processing state; subsequent
    // state changes (Transcribing ↔ Rewriting) call ApplyVariant on the
    // SAME visual, which blends SaturationEffect / HueRotationEffect /
    // ExposureEffect properties over a config-driven BlendSeconds — no
    // surface rebuild, no GC hit, no lag. See HudComposition for the
    // variant knob list and defaults.

    private HudComposition.ProcessingStroke? _processingStroke;

    private void AttachProcessingVisual(ProcessingVariant variant)
    {
        bool isDark = ChronoRoot.ActualTheme == ElementTheme.Dark;

        if (_processingStroke == null)
        {
            var compositor = ElementCompositionPreview
                .GetElementVisual(ProcessingSurfaceHost).Compositor;

            float w = (float)ProcessingSurfaceHost.ActualWidth;
            float h = (float)ProcessingSurfaceHost.ActualHeight;
            if (w == 0f || h == 0f) { w = 272f; h = 78f; }
            var size = new Vector2(w, h);

            _processingStroke = HudComposition.CreateProcessingStroke(compositor, size);
            ElementCompositionPreview.SetElementChildVisual(
                ProcessingSurfaceHost, _processingStroke.Visual);
        }

        // First-ever attach starts at the baked-in Transcribing (Dark)
        // baseline — greyscale, neutral exposure — so the stroke appears
        // already in Transcribing look, no rainbow flash on cold start.
        // The call below blends to the requested variant (snapping the
        // theme-split exposure if we're on Light). Subsequent attaches
        // (state change on the already-attached visual) blend from the
        // previous variant to the new one — same code path.
        _processingStroke.ApplyVariant(variant, isDark);
    }

    private void DetachProcessingVisual()
    {
        if (_processingStroke == null) return;

        ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
        _processingStroke.Dispose();
        _processingStroke = null;
    }

    // ── Recording outline attach + audio-level pump ───────────────────────────
    //
    // ProcessingSurfaceHost doubles as the attach point for the recording
    // outline during HudState.Recording — the two overlays are mutually
    // exclusive so there's no z-order conflict. The outline is a single
    // theme-coloured stroke with opacity animated off a PropertySet scalar;
    // HudWindow.OnAudioLevel forwards engine mic RMS to UpdateAudioLevel,
    // which EMAs the signal (τ ≈ 1 s at 20 Hz) and pushes the smoothed
    // value into the PropertySet. See HudComposition.RecordingOutline.
    //
    // EmaAlpha 0.95 at 20 Hz source → τ = -T / ln(alpha) ≈ 0.05 / 0.0513
    // ≈ 0.97 s. Dominates the Composition-side 50 ms micro-keyframes, so
    // the outline rise/fall is what the user perceives, not the sample
    // grid.
    private HudComposition.RecordingOutline? _recordingOutline;
    private float _smoothedLevel;
    private const float EmaAlpha = 0.95f;

    // Forwarded from HudWindow.OnAudioLevel. Called from the recording
    // audio thread. No-op if the outline is not attached (any non-Recording
    // state), so the engine event can stay subscribed permanently.
    // CompositionPropertySet updates are thread-safe per Composition's
    // contract — no DispatcherQueue marshalling.
    internal void UpdateAudioLevel(float rms)
    {
        if (_recordingOutline is null) return;
        _smoothedLevel = _smoothedLevel * EmaAlpha + rms * (1f - EmaAlpha);
        _recordingOutline.UpdateLevel(_smoothedLevel);
    }

    private void AttachRecordingOutline()
    {
        if (_recordingOutline != null) return;

        // Reset the EMA accumulator so leftover energy from the previous
        // recording session doesn't seed the new outline with a non-zero
        // opacity floor.
        _smoothedLevel = 0f;

        var compositor = ElementCompositionPreview
            .GetElementVisual(ProcessingSurfaceHost).Compositor;

        // Same fallback dims as AttachProcessingVisual — ActualWidth/Height
        // are 0 before the first measure pass, and the surface host is a
        // fixed-size Border so the fallback matches its layout rect.
        float w = (float)ProcessingSurfaceHost.ActualWidth;
        float h = (float)ProcessingSurfaceHost.ActualHeight;
        if (w == 0f || h == 0f) { w = 272f; h = 78f; }
        var size = new Vector2(w, h);

        _recordingOutline = HudComposition.CreateRecordingOutline(
            compositor, size, ResolvePrimaryTextColor());
        ElementCompositionPreview.SetElementChildVisual(
            ProcessingSurfaceHost, _recordingOutline.Visual);
    }

    private void DetachRecordingOutline()
    {
        if (_recordingOutline is null) return;

        ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
        _recordingOutline.Dispose();
        _recordingOutline = null;
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
        int capSec = Settings.SettingsService.Instance.Current.Recording.MaxRecordingDurationSeconds;
        if (capSec > 0 && elapsed.TotalSeconds > capSec)
            elapsed = TimeSpan.FromSeconds(capSec);
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
