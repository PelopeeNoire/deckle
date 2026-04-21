using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
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

            // Transcribing exposure is theme-aware (Dark vs Light split),
            // and Recording reuses those same baselines for its greyscale
            // palette — re-apply the variant on live theme change so the
            // stroke brightness matches the new substrate immediately.
            // Rewriting is palette-neutral and doesn't need this pass.
            if (_processingStroke != null && _currentVariant is { } v
                && v != ProcessingVariant.Rewriting)
            {
                _processingStroke.ApplyVariant(
                    v, ChronoRoot.ActualTheme == ElementTheme.Dark);
            }
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
    }

    private void ApplyRecording()
    {
        _stopwatch.Restart();

        AttachProcessingVisual(ProcessingVariant.Recording);

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

        AttachProcessingVisual(ProcessingVariant.Transcribing);
    }

    private void ApplyRewriting()
    {
        _stopwatch.Stop();
        UnhookRendering();

        UpdateClock();
        ResetDigitAccent();

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
    }

    // ── Composition stroke attach ─────────────────────────────────────────────
    //
    // ProcessingSurfaceHost (XAML Border) is the attach point for the
    // Composition visual produced by HudComposition. The visual sits above
    // ChronoCard and below the ClockText in the Grid z-order, so its stroke
    // paints on the card surface but the clock text reads on top.
    //
    // Fallback dims (272, 78) catch the pre-layout attach (ActualWidth/Height
    // are 0 before the first measure pass). The visual is not auto-resized
    // on subsequent layout passes — acceptable here because Charging/Recording
    // always resets the surface, and Transcribing/Rewriting only fire after
    // at least one full chrono measure.
    //
    // Single-pipeline, live-modulated variants where possible. The stroke is
    // created on first enter into Recording / Transcribing / Rewriting. For
    // Transcribing ↔ Rewriting transitions, ApplyVariant blends effect
    // properties on the SAME visual — no surface rebuild. The Recording ↔
    // (Transcribing / Rewriting) boundary crosses between a rotation-frozen
    // stroke and a spinning one: the two rotation modes are baked at
    // creation (static TransformMatrix vs KeyFrameAnimation on
    // CompositionSurfaceBrush.TransformMatrix, settled once and impossible
    // to unbind cleanly mid-life), so we dispose and rebuild.

    private HudComposition.ProcessingStroke? _processingStroke;
    private ProcessingVariant? _currentVariant;

    // EMA smoothing after the dBFS remap below. EmaAlpha 0.72 at 20 Hz
    // source → τ = -T / ln(alpha) ≈ 0.05 / 0.328 ≈ 0.15 s — fast enough
    // to track intonations at the word scale (typical word = 200–500 ms)
    // while still ironing out the sample grid into a continuous ramp.
    // The 50 ms Composition-side keyframes interpolate between samples at
    // the monitor refresh rate, so perceived motion is smooth regardless.
    private float _smoothedLevel;
    private const float EmaAlpha = 0.72f;

    // Linear RMS mapped through a dBFS window before EMA smoothing. The
    // window [MinDbfs, MaxDbfs] compresses real-world voice dynamics into
    // [0, 1]: anything ≤ MinDbfs is silence (0), anything ≥ MaxDbfs is
    // full opacity (1).
    //
    // Reference table with MinDbfs = -50, MaxDbfs = -32 (18 dB window):
    //   rms ≤ 0.003 (-50 dBFS)  → 0.00   silence (below engine gate anyway)
    //   rms = 0.010 (-40 dBFS)  → 0.56   soft voice / quiet talk
    //   rms = 0.018 (-35 dBFS)  → 0.83
    //   rms = 0.022 (-33 dBFS)  → 0.94
    //   rms = 0.025 (-32 dBFS)  → 1.00   clipping ceiling
    //   rms ≥ 0.025             → 1.00   clamped (anything above clips)
    //
    // MaxDbfs stays at -32: the WhispEngine RMS is a true 50 ms window
    // average, typically 10-15 dB below peak/VU-meter readings. Louis's
    // DSP shows peaks at -18 / -12 dBFS on normal speech, so the RMS of
    // that same speech lands around -28 to -32 — clipping at -32 matches
    // the RMS distribution of conversational voice.
    //
    // MinDbfs lowered to -50 (from -40) widens the usable window to 18 dB.
    // The engine's internal gate already cuts anything below ~-40 dBFS to
    // zero, so dropping the visual floor to -50 does NOT introduce noise-
    // level flicker; it only changes where the "gate opens" visual lands
    // on the opacity ramp. Before: gate opens at opacity 0, then climbs.
    // After: gate opens at opacity ~0.56 (midway), then climbs — so soft
    // voice is visible immediately when speech starts, instead of
    // crawling up from zero.
    private const float MinDbfs = -50f;
    private const float MaxDbfs = -32f;

    private static float RmsToPerceptualLevel(float rms)
    {
        if (rms <= 0f) return 0f;
        float dbfs = 20f * MathF.Log10(rms);
        float t = (dbfs - MinDbfs) / (MaxDbfs - MinDbfs);
        return Math.Clamp(t, 0f, 1f);
    }

    // Forwarded from HudWindow.OnAudioLevel. Called from the recording
    // audio thread. Gated on _currentVariant == Recording so the engine
    // event can stay subscribed permanently — Transcribing / Rewriting
    // strokes have ApplyVariant-driven opacity and must not be pushed
    // from the RMS pump. CompositionPropertySet + StartAnimation are
    // thread-safe per Composition's contract — no DispatcherQueue.
    internal void UpdateAudioLevel(float rms)
    {
        if (_processingStroke is null) return;
        if (_currentVariant != ProcessingVariant.Recording) return;

        float perceptual = RmsToPerceptualLevel(rms);
        _smoothedLevel = _smoothedLevel * EmaAlpha + perceptual * (1f - EmaAlpha);
        _processingStroke.UpdateLevel(_smoothedLevel);
    }

    private void AttachProcessingVisual(ProcessingVariant variant)
    {
        bool isDark = ChronoRoot.ActualTheme == ElementTheme.Dark;

        // Rotation-frozen vs spinning strokes cannot share a SpriteVisual —
        // the TransformMatrix is set once at creation (static matrix or
        // keyframe animation) and swapping modes live isn't supported by
        // Composition. Tear the existing stroke down when crossing that
        // boundary; in-kind transitions (Transcribing ↔ Rewriting) keep
        // the same visual and only blend effect properties.
        bool crossingBoundary =
            _processingStroke != null &&
            IsRecording(variant) != IsRecording(_currentVariant);

        if (crossingBoundary)
        {
            ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
            _processingStroke!.Dispose();
            _processingStroke = null;
        }

        if (_processingStroke == null)
        {
            var compositor = ElementCompositionPreview
                .GetElementVisual(ProcessingSurfaceHost).Compositor;

            float w = (float)ProcessingSurfaceHost.ActualWidth;
            float h = (float)ProcessingSurfaceHost.ActualHeight;
            if (w == 0f || h == 0f) { w = 272f; h = 78f; }
            var size = new Vector2(w, h);

            _processingStroke = variant == ProcessingVariant.Recording
                ? HudComposition.CreateRecordingStroke(compositor, size)
                : HudComposition.CreateProcessingStroke(compositor, size);
            ElementCompositionPreview.SetElementChildVisual(
                ProcessingSurfaceHost, _processingStroke.Visual);
        }

        // Reset the EMA accumulator on every Recording entry so leftover
        // energy from a previous recording session doesn't seed the new
        // outline with a non-zero opacity floor. Safe to reset here even
        // on a same-kind re-attach (ApplyRecording → ApplyRecording) — the
        // Recording path always starts from silence.
        if (variant == ProcessingVariant.Recording)
            _smoothedLevel = 0f;

        _currentVariant = variant;

        // Cold start or in-kind transition: blend the effect properties to
        // the new variant's targets. ApplyVariant skips Opacity for
        // Recording (UpdateLevel owns that channel).
        _processingStroke.ApplyVariant(variant, isDark);
    }

    private void DetachProcessingVisual()
    {
        if (_processingStroke == null) return;

        ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
        _processingStroke.Dispose();
        _processingStroke = null;
        _currentVariant   = null;
    }

    private static bool IsRecording(ProcessingVariant? v)
        => v == ProcessingVariant.Recording;

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
