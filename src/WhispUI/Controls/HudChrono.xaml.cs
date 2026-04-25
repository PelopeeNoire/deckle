using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    // Per-digit "was modified during Recording" flag. Preserved across the
    // Recording → Transcribing / Rewriting transition so the swipe can tell
    // which digits are eligible for the accent flash (dots and unchanged
    // digits stay at Opacity 0 on their accent overlay forever).
    // Index order matches `_digitPrimary` / `_digitAccent` / `_digitHeat`:
    //   0 Min1, 1 Min2, 2 Sec1, 3 Sec2, 4 Cs1, 5 Cs2.
    private readonly bool[] _digitChanged = new bool[6];

    // Per-digit heat 0..1 driving the accent overlay's Opacity. Rises fast
    // when the swipe head is on a changed digit, decays slowly afterwards.
    // The asymmetric rise/decay (see SwipeRiseAlpha / SwipeDecayAlpha below)
    // gives the wave effect Louis described: a digit keeps glowing for a
    // moment after the head has moved on, so several digits are partially
    // lit at once — a trailing comet instead of a single moving pixel.
    private readonly float[] _digitHeat = new float[6];

    // Cached references assembled in EnsureSwipeInfra. Parallel arrays so
    // the per-frame loop is a tight zip over three indices. Accent elements
    // are TextBlocks (NOT UIElements in general) so we can assign .Opacity
    // directly without reaching for Composition.
    private TextBlock[]? _digitPrimary;
    private TextBlock[]? _digitAccent;

    private HudState _state = HudState.Hidden;

    public HudChrono()
    {
        InitializeComponent();

        ChronoRoot.ActualThemeChanged += (_, _) =>
        {
            // Accent TextBlocks bind Foreground via {ThemeResource …} in
            // XAML, so they re-resolve on theme change automatically. The
            // primary TextBlocks inherit from the shared style's
            // ThemeResource Foreground — same story. No per-TextBlock
            // re-assignment needed here (unlike the old design which
            // mutated Foreground in code, which breaks the ThemeResource
            // binding and requires a manual re-push on theme change).

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

    // Resolved at runtime from Application.Resources so theme switches
    // still update the brush (System resource keys flip color across
    // light/dark). Only ResolveNeutralBrush is used from code — the
    // critical / primary brushes now live purely in XAML via
    // {ThemeResource …} bindings on the accent overlay TextBlocks and
    // the shared ChronoDigitStyle, respectively, and theme changes are
    // handled by WinUI's resource-tracking machinery. The neutral brush
    // is the one exception because Charging overrides the primary
    // Foreground with the tertiary tone on every digit — too
    // state-specific to express as a ThemeResource default.
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
        StopSwipe();

        _lastMin = _lastSec = _lastCs = -1;
        ClearDigitChanged();
        ClearDigitHeat();
        ResetDigitTexts();

        // Charging is the one state where the primary text is painted in
        // the neutral / tertiary colour — a "parked" clock waiting for the
        // first recording. Override the Style default Foreground for the
        // 6 digits; dots stay primary (they're structural punctuation,
        // not data, so they read at full contrast regardless of state).
        var neutral = ResolveNeutralBrush();
        Min1.Foreground = neutral; Min2.Foreground = neutral;
        Sec1.Foreground = neutral; Sec2.Foreground = neutral;
        Cs1.Foreground  = neutral; Cs2.Foreground  = neutral;
        DotA.Foreground = neutral; DotB.Foreground = neutral;

        DetachProcessingVisual();
    }

    private void ApplyRecording()
    {
        _stopwatch.Restart();
        StopSwipe();

        AttachProcessingVisual(ProcessingVariant.Recording);

        _lastMin = _lastSec = _lastCs = -1;
        ClearDigitChanged();
        ClearDigitHeat();
        ResetDigitTexts();

        // Clear local Foreground so each primary TextBlock inherits its
        // Style default (TextFillColorPrimaryBrush, theme-resource-bound).
        // UpdateClock will then bump _digitHeat[i] = 1 on any digit that
        // changes — the accent overlay cross-fades in immediately (no
        // latency during Recording, because the head isn't "sweeping"
        // here — each change is an independent event).
        ClearDigitForegrounds();

        UpdateClock();
        HookRendering();
    }

    private void ApplyTranscribing()
    {
        // Freeze the clock at its last value.
        _stopwatch.Stop();

        // Order matters: UpdateClock first to write the final elapsed value
        // (which may mark the last-changed digit because the stopwatch
        // advanced between the last vsync tick and the Stop), then the
        // swipe starts. We KEEP the _digitChanged flags so the swipe knows
        // which digits are eligible for the accent flash.
        UpdateClock();
        ClearDigitForegrounds();

        AttachProcessingVisual(ProcessingVariant.Transcribing);
        StartSwipe();
        // HookRendering drives OnRendering → UpdateSwipe (stopwatch is
        // stopped so UpdateClock is a no-op on the digit values).
        HookRendering();
    }

    private void ApplyRewriting()
    {
        _stopwatch.Stop();

        UpdateClock();
        ClearDigitForegrounds();

        AttachProcessingVisual(ProcessingVariant.Rewriting);
        StartSwipe();
        HookRendering();
    }

    // Drops the per-digit "ever-changed" flags. Not called on
    // Transcribing / Rewriting entry — those preserve the flags so the
    // swipe reveal can target only the digits that actually moved.
    private void ClearDigitChanged()
    {
        for (int i = 0; i < _digitChanged.Length; i++) _digitChanged[i] = false;
    }

    // Drops all heat to zero and pushes Opacity=0 onto the accent
    // overlays. Used on state entries that need a clean slate (Charging,
    // Recording start). Transcribing / Rewriting inherit heat from the
    // last Recording frame so the previously-lit digits decay naturally
    // as the swipe picks them up.
    private void ClearDigitHeat()
    {
        for (int i = 0; i < _digitHeat.Length; i++) _digitHeat[i] = 0f;
        if (_digitAccent is null) return;
        foreach (var t in _digitAccent) t.Opacity = 0;
        // Restore the primary-glyph invariant: accent = 0 ⇒ primary = 1.
        // Without this, primaries knocked to 0 by a previous Recording
        // flash would stay hidden after the state transition clears the
        // accents.
        if (_digitPrimary is null) return;
        foreach (var t in _digitPrimary) t.Opacity = 1;
    }

    private void ResetDigitTexts()
    {
        Min1.Text = Min2.Text = "0";
        Sec1.Text = Sec2.Text = "0";
        Cs1.Text  = Cs2.Text  = "0";
        DotA.Text = DotB.Text = ".";
        // Keep the accent overlays' Text in sync even when they're hidden
        // — otherwise the next heat-up would show a stale digit briefly
        // before UpdateClock rewrites it.
        Min1Accent.Text = Min2Accent.Text = "0";
        Sec1Accent.Text = Sec2Accent.Text = "0";
        Cs1Accent.Text  = Cs2Accent.Text  = "0";
    }

    // Clears the local Foreground on all 8 primary TextBlocks. Each falls
    // back to its Style default (TextFillColorPrimaryBrush). The accent
    // overlays always carry SystemFillColorCriticalBrush from XAML, so
    // they don't need clearing here — only the Opacity is state-driven.
    private void ClearDigitForegrounds()
    {
        Min1.ClearValue(TextBlock.ForegroundProperty);
        Min2.ClearValue(TextBlock.ForegroundProperty);
        DotA.ClearValue(TextBlock.ForegroundProperty);
        Sec1.ClearValue(TextBlock.ForegroundProperty);
        Sec2.ClearValue(TextBlock.ForegroundProperty);
        DotB.ClearValue(TextBlock.ForegroundProperty);
        Cs1.ClearValue(TextBlock.ForegroundProperty);
        Cs2.ClearValue(TextBlock.ForegroundProperty);
    }

    private void ApplyHidden()
    {
        _stopwatch.Stop();
        UnhookRendering();
        StopSwipe();

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

    // HudPlayground-only: config override consumed (and cleared) by the
    // next stroke creation inside AttachProcessingVisual. Lets the
    // playground bring up a state with a caller-supplied config in a
    // single stroke creation — without this slot, the playground had to
    // call ApplyState (creates a stroke with shipping defaults) then
    // RebuildStroke (dispose + recreate with tuning config), which
    // doubled the stroke churn on every target change and inflated
    // live_stroke_count artefacts in the instrumentation log.
    //
    // Null in shipping WhispUI — OnLaunched and HudWindow never set it,
    // so AttachProcessingVisual falls back to the factories' default
    // configs. Shipping behaviour is byte-identical.
    private HudComposition.ConicArcStrokeConfig? _nextStrokeConfig;

    // EMA smoothing after the dBFS remap below. EmaAlpha 0.72 at 20 Hz
    // source → τ = -T / ln(alpha) ≈ 0.05 / 0.328 ≈ 0.15 s — fast enough
    // to track intonations at the word scale (typical word = 200–500 ms)
    // while still ironing out the sample grid into a continuous ramp.
    // The 50 ms Composition-side keyframes interpolate between samples at
    // the monitor refresh rate, so perceived motion is smooth regardless.
    private float _smoothedLevel;

    // `public static` (not const / readonly) so HudPlayground can override
    // the audio-mapping tunables live. Shipping code still resolves them
    // as if they were constants — the field reads are inlined by the JIT
    // when nothing mutates them in a given process. Defaults preserved.
    public static float EmaAlpha = 0.25f;

    // Linear RMS mapped through a dBFS window, then through a power
    // curve, before EMA smoothing. The window [MinDbfs, MaxDbfs] folds
    // the dBFS range into a linear [0, 1] parameter t; the power curve
    // t^p then reshapes the response so the visual reacts softly in
    // the lower half and aggressively in the upper half of the window.
    //
    // Reference table with MinDbfs = -40, MaxDbfs = -22 (18 dB window)
    // and DbfsCurveExponent = 2.0 (quadratic):
    //   rms ≤ 0.010 (-40 dBFS)  → t=0.00  → y=0.00   silence / gate
    //   rms = 0.018 (-35 dBFS)  → t=0.28  → y=0.08   breath / ambient
    //   rms = 0.032 (-30 dBFS)  → t=0.56  → y=0.31   soft onset
    //   rms = 0.040 (-28 dBFS)  → t=0.67  → y=0.44   conversational
    //   rms = 0.050 (-26 dBFS)  → t=0.78  → y=0.61   louder
    //   rms = 0.063 (-24 dBFS)  → t=0.89  → y=0.79   assertive speech
    //   rms = 0.079 (-22 dBFS)  → t=1.00  → y=1.00   emphatic ceiling
    //
    // Calibration — Louis's voice peaks around -18 dBFS but the 50 ms
    // RMS average sits 6-10 dB below peak, landing in -28..-24 dBFS
    // for normal speech and brushing -22 dBFS only on emphatic
    // stress. Previous ceiling at -18 dBFS was unreachable in
    // practice: conversational RMS reached y ≈ 0.30 and even loud
    // speech stayed below y=0.55, so the stroke barely lit up during
    // real recordings (the playground's sim pump masked this because
    // its peak value saturated the upper range). The -22 dBFS
    // ceiling puts conversational RMS at y=0.44-0.79 — clearly
    // visible, with real dynamics — and the quadratic curve keeps
    // the low-end soft so ambient noise still fades to zero.
    //
    // MinDbfs -40: matches the engine's noise-gate threshold, so the
    // visual floor coincides with the audible floor.
    //
    // DbfsCurveExponent 1.0 restores the old linear mapping; values
    // above 1 push the response to the upper end of the window; below
    // 1 pushes it to the low end (only useful for debugging).
    //
    // `public static` (not const) — HudPlayground mutates these live to
    // explore the window and the curve shape. Defaults preserved.
    public static float MinDbfs           = -40f;
    public static float MaxDbfs           = -32f;
    public static float DbfsCurveExponent = 2.0f;

    private static float RmsToPerceptualLevel(float rms)
    {
        if (rms <= 0f) return 0f;
        float dbfs = 20f * MathF.Log10(rms);
        float t = (dbfs - MinDbfs) / (MaxDbfs - MinDbfs);
        t = Math.Clamp(t, 0f, 1f);
        // Power-curve response. p = 1 is linear; p > 1 compresses the
        // low end and expands the high end. Guarded against p ≤ 0 so
        // the playground can't nuke the mapping by dragging to 0.
        float p = DbfsCurveExponent;
        if (p <= 0f) return t;
        return MathF.Pow(t, p);
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

            // Consume the one-shot config override if the playground armed
            // one before this ApplyState call. Null in shipping WhispUI.
            var cfg = _nextStrokeConfig;
            _nextStrokeConfig = null;

            _processingStroke = variant == ProcessingVariant.Recording
                ? HudComposition.CreateRecordingStroke(compositor, size, cfg)
                : HudComposition.CreateProcessingStroke(compositor, size, cfg);
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

    // HudPlayground-only: rebuild the stroke with a caller-supplied config
    // so baked-geometry knobs (StrokeThickness, WedgeCount, ConicSpan*,
    // ArcMirror, ArcPhaseTurns, etc.) can be explored interactively. The
    // stroke is rebuilt, not mutated — paint-time fields are baked into
    // Win2D surfaces and cannot be animated live.
    //
    // No-op when no variant is active (Hidden / Charging have no stroke).
    // The current variant determines which factory is called; the caller
    // must pass a config that matches (Recording* fields honoured when
    // variant == Recording, generic fields otherwise).
    // `log` is an optional diagnostic sink used exclusively by the
    // HudPlayground — shipping WhispUI never passes one, so the null-
    // conditional invocations collapse to zero cost. Each anchor lets
    // the playground log panel show the exact lifecycle order when
    // Louis observes a stroke freezing mid-run; the try/catch wraps
    // the whole teardown + rebuild + apply sequence so a Composition
    // exception thrown deep inside any of the factories surfaces as a
    // visible ERROR line instead of freezing silently.
    // HudPlayground-only: force the "digit was modified during Recording"
    // flags so the swipe reveal (only visible on flagged digits) can be
    // observed without first running a full Recording cycle. Shipping
    // WhispUI never calls this — the flags flip naturally inside
    // UpdateClock as the chrono advances.
    //
    // The four CS digits and both seconds digits are the usual candidates
    // for "was modified" because a typical recording spans at least a few
    // tenths of a second. Minutes stay unflagged unless a call supplies
    // true for them explicitly — mirrors the shipping pattern where
    // minutes only flip on recordings longer than 60s.
    internal void SimulateChangedDigits(
        bool min1, bool min2,
        bool sec1, bool sec2,
        bool cs1,  bool cs2)
    {
        // Index order matches _digitChanged: 0 Min1, 1 Min2,
        // 2 Sec1, 3 Sec2, 4 Cs1, 5 Cs2.
        _digitChanged[0] = min1;
        _digitChanged[1] = min2;
        _digitChanged[2] = sec1;
        _digitChanged[3] = sec2;
        _digitChanged[4] = cs1;
        _digitChanged[5] = cs2;
    }

    // HudPlayground-only: arms a config override to be consumed by the
    // very next stroke creation inside AttachProcessingVisual (which
    // happens during ApplyState for Recording / Transcribing / Rewriting).
    // The override is one-shot — it's cleared as soon as it's used, so
    // subsequent ApplyState calls without a fresh Set* would fall back to
    // the factories' defaults. Use RebuildStroke after the state is live
    // to apply a new config without changing state.
    internal void SetNextStrokeConfig(HudComposition.ConicArcStrokeConfig config)
    {
        _nextStrokeConfig = config;
    }

    internal void RebuildStroke(
        HudComposition.ConicArcStrokeConfig config,
        System.Action<string, string>? log = null)
    {
        if (_currentVariant is not { } variant)
        {
            log?.Invoke("REBUILD", "skip — no active variant");
            return;
        }

        try
        {
            log?.Invoke("REBUILD", $"begin variant={variant}");

            ElementCompositionPreview.SetElementChildVisual(ProcessingSurfaceHost, null);
            log?.Invoke("REBUILD", "detached old visual from host");

            _processingStroke?.Dispose();
            _processingStroke = null;
            log?.Invoke("REBUILD", "disposed old ProcessingStroke");

            var compositor = ElementCompositionPreview
                .GetElementVisual(ProcessingSurfaceHost).Compositor;

            float w = (float)ProcessingSurfaceHost.ActualWidth;
            float h = (float)ProcessingSurfaceHost.ActualHeight;
            bool fallback = (w == 0f || h == 0f);
            if (fallback) { w = 272f; h = 78f; }
            var size = new Vector2(w, h);
            log?.Invoke("REBUILD", $"size={w:F1}×{h:F1}{(fallback ? " (fallback)" : "")}");

            _processingStroke = variant == ProcessingVariant.Recording
                ? HudComposition.CreateRecordingStroke(compositor, size, config)
                : HudComposition.CreateProcessingStroke(compositor, size, config);
            // Log the unique CreationId + live-count alongside the variant
            // so the playground log reads chronologically as "created #N
            // (live=K)" — when K starts climbing beyond 1, the Dispose
            // path is failing somewhere and that's the freeze signal.
            log?.Invoke("REBUILD",
                $"created {(variant == ProcessingVariant.Recording ? "Recording" : "Processing")}Stroke " +
                $"#{_processingStroke.CreationId} (live={HudComposition.LiveStrokeCount})");

            ElementCompositionPreview.SetElementChildVisual(
                ProcessingSurfaceHost, _processingStroke.Visual);
            log?.Invoke("REBUILD", "attached new visual to host");

            bool isDark = ChronoRoot.ActualTheme == ElementTheme.Dark;
            _processingStroke.ApplyVariant(variant, isDark);
            log?.Invoke("REBUILD", $"ApplyVariant {variant} isDark={isDark} — done");
        }
        catch (System.Exception ex)
        {
            // Composition can throw (e.g. DirectX device lost, surface
            // creation failure, expression parse error in StartRotation).
            // Without this catch the exception bubbles to the UI thread
            // and either crashes the playground or — worse — silently
            // kills the visual's animations, which is exactly the
            // "freeze mid-run" symptom Louis is chasing.
            log?.Invoke("ERROR", $"RebuildStroke threw {ex.GetType().Name}: {ex.Message}");
            throw;
        }
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

    // Single vsync dispatcher for both the clock ticker (Recording) and the
    // swipe reveal (Transcribing / Rewriting). UpdateClock early-outs via
    // the stopwatch state when not Recording, so calling both is cheap.
    private void OnRendering(object? sender, object e)
    {
        UpdateClock();
        UpdateSwipe();
    }

    // Writes `newText` onto both the primary and accent TextBlocks at
    // `index`, flags the digit as "changed" for the downstream swipe,
    // and bumps its heat to 1 so the accent overlay is visible
    // immediately — the Recording-time "each change flashes red" UX
    // Louis has been iterating on. Returns early if the text didn't
    // actually change (no-op on every vsync for stationary digits).
    // Index order matches the `_digitChanged` / `_digitHeat` arrays:
    // 0 Min1, 1 Min2, 2 Sec1, 3 Sec2, 4 Cs1, 5 Cs2.
    private void WriteDigit(int index, string newText, TextBlock primary, TextBlock accent)
    {
        if (primary.Text == newText) return;
        primary.Text = newText;
        accent.Text  = newText;
        _digitChanged[index] = true;
        _digitHeat[index]    = 1f;
        // Push the flash directly on the overlay. During Recording the
        // vsync loop (UpdateClock) runs but UpdateSwipe is dormant
        // (_swipeRunning=false), so without this line heat=1 never
        // reaches the overlay and the digit stays primary. During
        // Transcribing/Rewriting the swipe rewrites Opacity every
        // vsync — this write is immediately superseded by UpdateSwipe,
        // so no interference with the wave animation.
        //
        // Invariant primary.Opacity + accent.Opacity = 1 so only one
        // glyph ever contributes ink. Without this, both TextBlocks
        // render at full alpha simultaneously and the accent glyph
        // appears visibly thicker / bolder than an unchanged digit,
        // because two ClearType-hinted copies of the same glyph at
        // the same position double up on subpixel coverage.
        accent.Opacity  = 1;
        primary.Opacity = 0;
    }

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
            WriteDigit(0, d1.ToString(), Min1, Min1Accent);
            WriteDigit(1, d2.ToString(), Min2, Min2Accent);
            _lastMin = min;
        }
        if (sec != _lastSec)
        {
            int d1 = sec / 10, d2 = sec % 10;
            WriteDigit(2, d1.ToString(), Sec1, Sec1Accent);
            WriteDigit(3, d2.ToString(), Sec2, Sec2Accent);
            _lastSec = sec;
        }
        if (cs != _lastCs)
        {
            int d1 = cs / 10, d2 = cs % 10;
            WriteDigit(4, d1.ToString(), Cs1, Cs1Accent);
            WriteDigit(5, d2.ToString(), Cs2, Cs2Accent);
            _lastCs = cs;
        }
    }

    // ── Swipe reveal animation ───────────────────────────────────────────
    //
    // During Transcribing and Rewriting, a wave travels left→right across
    // the 6 digits. Each digit carries its own *heat* scalar in [0, 1]
    // driving the Opacity of its accent overlay TextBlock — when heat
    // rises, the overlay (SystemFillColorCriticalBrush) cross-fades in
    // over the primary; at heat=1 the digit reads as pure red.
    //
    // The key property (what Louis calls "l'effet de vague") is
    // *asymmetric rise and decay*. When the wave's head lands on a
    // changed digit, its heat target becomes 1 and heat rises at
    // SwipeRiseAlpha per frame. When the head moves on, the target drops
    // back to 0 but heat decays at the much slower SwipeDecayAlpha per
    // frame. So a digit takes a few frames to light up, and many frames
    // to dim out — which means by the time digit N is near full heat,
    // the head is already on digit N+1, which itself has started rising.
    // The trail of partially-lit digits behind the head reads as a comet
    // trailing left→right.
    //
    // Dots (DotA / DotB) have no accent overlay and no heat tracking —
    // they stay primary the whole cycle. Unchanged digits (those that
    // did not flip during Recording, per _digitChanged[]) have their
    // target pinned at 0 so their heat only decays; if they inherit any
    // heat from the Recording hand-off, it fades and then they stay
    // dark.
    //
    // Why managed driving instead of CompositionPropertySet + animation:
    // the heat state depends on the head index *and* the per-digit
    // changed flag, neither of which is cleanly expressible as a
    // Composition Expression. At 6 elements × vsync the per-frame cost
    // is trivial in managed code.

    // Swipe reveal animation — tunables.
    // SwipeCycleSeconds   full head-sweep duration (seconds).
    //                     Lower = faster wave, higher = contemplative.
    // SwipeEaseP1/P2      cubic-bezier control points for the head's
    //                     progress curve. Defaults mirror HudComposition's
    //                     ArcEase (0.5, 0) → (0.2, 1) for a sharp ease-out.
    // SwipeRiseAlpha      per-frame lerp factor toward the active target
    //                     (head on digit = 1). 0.22 ≈ ~9 frames to reach
    //                     90 % at 60 Hz → ~150 ms ramp-up; slow enough
    //                     to read as a fade rather than a snap, fast
    //                     enough that the digit is still "catching up"
    //                     when the head has already moved on.
    // SwipeDecayAlpha     per-frame lerp factor back to 0 when the head
    //                     is elsewhere. 0.06 ≈ ~37 frames to drop below
    //                     10 % at 60 Hz → ~620 ms trail — longer than
    //                     rise so heat accumulates into a trailing
    //                     comet. Keep DecayAlpha < RiseAlpha / 3 for
    //                     the wave character; otherwise the trail reads
    //                     as "tremblement" rather than "vague".
    // `public static` (not const / readonly) so HudPlayground can tune
    // the cadence, easing, and rise/decay alphas live.
    public static float   SwipeCycleSeconds = 1.6f;
    public static Vector2 SwipeEaseP1       = new(0.5f, 0f);
    public static Vector2 SwipeEaseP2       = new(0.2f, 1f);
    public static float   SwipeRiseAlpha    = 0.1f;
    public static float   SwipeDecayAlpha   = 0.05f;

    // Digit count — structural, mirrors _digitHeat.Length and the 6 accent
    // overlays declared in HudChrono.xaml. Not a tunable.
    private const int DigitCount = 6;

    private readonly System.Diagnostics.Stopwatch _swipeStopwatch = new();
    private bool _swipeRunning;

    private void EnsureSwipeInfra()
    {
        if (_digitPrimary is null)
        {
            _digitPrimary = new[] { Min1, Min2, Sec1, Sec2, Cs1, Cs2 };
            _digitAccent  = new[] { Min1Accent, Min2Accent, Sec1Accent, Sec2Accent, Cs1Accent, Cs2Accent };
        }
    }

    private void StartSwipe()
    {
        EnsureSwipeInfra();
        if (_swipeRunning) return;
        _swipeStopwatch.Restart();
        _swipeRunning = true;
    }

    private void StopSwipe()
    {
        if (!_swipeRunning) return;
        _swipeStopwatch.Stop();
        _swipeRunning = false;
        // Drop heat to zero and hide the accent overlays on the way out.
        // The next state entry (ApplyRecording / ApplyCharging) takes
        // over from a clean slate.
        ClearDigitHeat();
    }

    private void UpdateSwipe()
    {
        if (!_swipeRunning || _digitPrimary is null || _digitAccent is null) return;

        // Fraction of the cycle that has elapsed, wrapped into [0, 1). The
        // modulo gives us the looping behaviour for free — no keyframe
        // roll-over to worry about.
        double elapsed = _swipeStopwatch.Elapsed.TotalSeconds;
        float t = (float)((elapsed / SwipeCycleSeconds) % 1.0);
        float progress = CubicBezierEase(t, SwipeEaseP1, SwipeEaseP2);

        // Head index in [0, DigitCount-1]. The head sweeps only the 6
        // digits — dots are not swept (they have no heat, no overlay).
        // progress is eased so the head lingers near the extremes
        // (cubic-bezier ease-out) — that's the "accelerate then slow
        // down" cadence at the seam between cycles.
        int headIndex = (int)MathF.Floor(progress * DigitCount);
        if (headIndex >= DigitCount) headIndex = DigitCount - 1;
        if (headIndex < 0)           headIndex = 0;

        for (int i = 0; i < DigitCount; i++)
        {
            // Target heat: 1 when the head is on this digit AND the digit
            // was modified during Recording. Everywhere else, 0. Unchanged
            // digits never receive target = 1, so their heat only ever
            // decays and stays dark after the initial fade-out.
            float target = (i == headIndex && _digitChanged[i]) ? 1f : 0f;

            // Asymmetric lerp: fast rise, slow decay. The asymmetry is
            // what creates the comet trail.
            float alpha = target > _digitHeat[i] ? SwipeRiseAlpha : SwipeDecayAlpha;
            _digitHeat[i] += (target - _digitHeat[i]) * alpha;

            // Push the new heat onto the overlay's Opacity. TextBlock
            // Opacity is a DP so the set is a no-op when unchanged; no
            // need to guard manually — but we round to 3 decimals first
            // so a heat of 0.9999997 (floating noise) doesn't repeatedly
            // invalidate the render pass.
            //
            // The primary glyph's Opacity is pushed to (1 - accent) so
            // the two layers never double up on subpixel coverage (see
            // WriteDigit comment). Skipping this invariant makes
            // Recording-time flashes look bolder than unchanged digits.
            double rounded = System.Math.Round(_digitHeat[i], 3);
            if (_digitAccent[i].Opacity != rounded)
                _digitAccent[i].Opacity = rounded;
            double primaryOpacity = 1.0 - rounded;
            if (_digitPrimary[i].Opacity != primaryOpacity)
                _digitPrimary[i].Opacity = primaryOpacity;
        }
    }

    // Cubic-bezier ease with anchor points P0=(0,0), P3=(1,1) and free
    // control points p1, p2. Given input x on [0, 1], solves Bx(u) = x via
    // Newton-Raphson, then returns By(u). WebKit's UnitBezier formulation —
    // the polynomial coefficients collapse to 3 fused-multiply-adds per
    // sample, and 8 Newton iterations get us well below sub-pixel accuracy
    // for any reasonable control-point layout.
    private static float CubicBezierEase(float x, Vector2 p1, Vector2 p2)
    {
        float cx = 3f * p1.X;
        float bx = 3f * (p2.X - p1.X) - cx;
        float ax = 1f - cx - bx;
        float cy = 3f * p1.Y;
        float by = 3f * (p2.Y - p1.Y) - cy;
        float ay = 1f - cy - by;

        float u = x;
        for (int i = 0; i < 8; i++)
        {
            float sampleX = ((ax * u + bx) * u + cx) * u - x;
            if (MathF.Abs(sampleX) < 1e-4f) break;
            float dx = (3f * ax * u + 2f * bx) * u + cx;
            if (MathF.Abs(dx) < 1e-6f) break;
            u -= sampleX / dx;
        }
        u = Math.Clamp(u, 0f, 1f);
        return ((ay * u + by) * u + cy) * u;
    }
}
