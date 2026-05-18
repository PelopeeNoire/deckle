using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Deckle.Lighting;
using Deckle.Lighting.Ambient;
using Deckle.Vision;

namespace Deckle.Playground;

// ─── AmbientPage — preview grid + swatches ──────────────────────────────────
//
// Sampled-pixel grid mirroring the screen colour distribution, the
// emitted-colour swatch strip on the toolbar, and the four border-zone
// overlay rectangles. Driven by a 200 ms DispatcherTimer that prefers
// the canonical AmbientEngine.LatestSample, falling back to the local
// FrameSampler.LatestSample when the user is running an isolated
// capture session without the full pipeline.
//
// Idle behaviour (no live source) blanks the cells and switches the
// zone overlay to a neutral grey — the user sees the substrate is
// there, no captured frame is frozen mid-tone. The zone overlay
// rectangles' Visibility is still gated by the per-zone assignment
// check in UpdateZoneOverlayHighlight (LightZones partial) and by
// the "Zones" toggle on the toolbar.

public sealed partial class AmbientPage
{
    private void OnSamplerFrameArrived(CapturedFrame frame)
    {
        _frameSampler?.Process(frame);
    }

    private void BuildPreviewGrid(int cols, int rows)
    {
        AmbientPreviewGrid.RowDefinitions.Clear();
        AmbientPreviewGrid.ColumnDefinitions.Clear();
        AmbientPreviewGrid.Children.Clear();

        for (int c = 0; c < cols; c++)
            AmbientPreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewCellSize) });
        for (int r = 0; r < rows; r++)
            AmbientPreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PreviewCellSize) });

        _previewCells = new Microsoft.UI.Xaml.Shapes.Rectangle[cols * rows];

        // One SolidColorBrush per cell — MANDATORY. Sharing a single
        // brush across cells and mutating its Color in the preview
        // tick made every cell snap to the last pixel of the grid
        // (typically the bottom-right taskbar — dark), which is what
        // produced the "everything is dark while the screen is white"
        // symptom even though the FrameSampler average (and the lamp
        // push) were correct.
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                };
                Grid.SetRow(rect, r);
                Grid.SetColumn(rect, c);
                AmbientPreviewGrid.Children.Add(rect);
                _previewCells[r * cols + c] = rect;
            }
        }

        _previewGridCols = cols;
        _previewGridRows = rows;
        LayoutLightZonesOverlayFromViewbox();

        UpdatePreviewViewboxVisibility();
    }

    private void StartPreviewTimer()
    {
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _previewTimer.Tick += OnPreviewTimerTick;
        }
        _previewTimer.Start();
    }

    // Starts the preview timer iff there's a live source pushing
    // samples (canonical AmbientEngine or local Screen capture).
    // Otherwise stops the timer entirely and runs a one-shot blanking
    // pass so the UI reflects the idle state — keeping the timer
    // ticking at 5 Hz on an idle Playground page noticeably loaded
    // the dispatcher and produced visible lag while dragging the
    // window. Called from page-nav hooks, AmbientEngine.StateChanged,
    // and the Start/Stop paths of the local Screen capture toggle ;
    // safe to call repeatedly, idempotent.
    private void EvaluatePreviewTimerState()
    {
        if (HasLiveSource())
        {
            StartPreviewTimer();
        }
        else
        {
            _previewTimer?.Stop();
            BlankIdlePreviewState();
        }
    }

    private bool HasLiveSource()
    {
        var engine = AmbientEngine.Current;
        bool engineActive       = engine is { IsRunning: true };
        bool localCaptureActive = _screenCapture is { IsRunning: true };
        return engineActive || localCaptureActive;
    }

    // One-shot cleanup mirrored on the idle-branch of OnPreviewTimerTick.
    // Invoked when EvaluatePreviewTimerState transitions the timer
    // OFF so the UI doesn't keep showing whatever the last frame
    // painted.
    private void BlankIdlePreviewState()
    {
        ClearPreviewCells();
        ApplyNeutralZoneOverlay();
        if (_swatchByLight.Count > 0)
        {
            EmittedSwatches.Children.Clear();
            _swatchByLight.Clear();
        }
    }

    private void UpdatePreviewViewboxVisibility()
    {
        // Visibility contract (per Louis 2026-05-19) :
        //   Ambient ON                          → cells visible, no placeholder.
        //   Ambient OFF + Zones toggle ON       → zone overlay takes the stage,
        //                                          no placeholder.
        //   Ambient OFF + Zones toggle OFF      → placeholder.
        //   No group selected (no zones at all) → placeholder.
        //
        // The Zones overlay Canvas Visibility is governed by
        // OnShowZoneOverlaysToggled (toggling hides / shows the whole
        // sibling Canvas) ; here we decide whether the Viewbox host or
        // the placeholder owns the surface. The empty-state showing
        // when zones are resolved but the toggle is off lets the user
        // dismiss the preview area entirely when they don't want
        // anything on screen — matches the requested "if you uncheck
        // zones, there's nothing".
        bool hasCells = _previewCells is not null;
        bool hasZones = _placementLights is { Count: > 0 };
        bool zonesToggleOn = ShowZoneOverlaysToggle?.IsChecked == true;
        bool show = hasCells || (hasZones && zonesToggleOn);

        AmbientPreviewViewbox.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        AmbientPreviewEmptyState.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

        // Defer a layout refresh past the next layout pass so the
        // overlay Canvas picks up the Viewbox host's freshly-measured
        // size in the zones-only mode (where the Viewbox itself has
        // 0 dip content — see LayoutLightZonesOverlayFromViewbox for
        // the parent-fallback that lands the rects in the right place).
        if (show)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                LayoutLightZonesOverlayFromViewbox);
        }
    }

    private void OnPreviewTimerTick(object? sender, object e)
    {
        // "Live source" = either the canonical AmbientEngine in the host
        // App is pushing samples, or the local Screen capture toggle is
        // running for sampler-isolated testing. Without either, we used
        // to leave the cells frozen on the last captured frame — looked
        // like the pipeline was still alive when it wasn't. Now we
        // blank the cells and switch the zone overlay to a neutral
        // grey so the user reads "the substrate is here, nothing is
        // being captured".
        var engine = AmbientEngine.Current;
        bool engineActive       = engine is { IsRunning: true };
        bool localCaptureActive = _screenCapture is { IsRunning: true };
        bool hasLiveSource      = engineActive || localCaptureActive;

        if (!hasLiveSource)
        {
            ClearPreviewCells();
            ApplyNeutralZoneOverlay();
            UpdateEmittedSwatches();   // self-clears when engine isn't running
            return;
        }

        var sample = engine?.LatestSample ?? _frameSampler?.LatestSample;

        if (sample is not null && _previewCells is null)
        {
            BuildPreviewGrid(sample.Cols, sample.Rows);
            UpdatePreviewViewboxVisibility();
        }

        if (sample is not null && _previewCells is not null)
        {
            int total = Math.Min(sample.Grid.Length, _previewCells.Length);
            for (int i = 0; i < total; i++)
            {
                var c = sample.Grid[i];
                var brush = _previewCells[i].Fill as SolidColorBrush;
                if (brush is null)
                {
                    _previewCells[i].Fill = new SolidColorBrush(c);
                }
                else
                {
                    brush.Color = c;
                }
            }
        }

        UpdateEmittedSwatches();
        UpdateZoneOverlayColors();
    }

    // Idle helpers : called from OnPreviewTimerTick when no live source
    // is pushing samples. The cells go transparent (preview substrate
    // becomes a clean Layer-fill rectangle), the zone overlay
    // rectangles switch to a neutral semi-transparent grey so the user
    // can still see which bands of the screen the pipeline would
    // sample if it were on. UpdateZoneOverlayHighlight still gates rect
    // visibility on whether a light is assigned to each zone — empty
    // zones stay collapsed even in neutral mode.

    private void ClearPreviewCells()
    {
        if (_previewCells is null) return;
        foreach (var rect in _previewCells)
        {
            if (rect.Fill is SolidColorBrush b)
                b.Color = Microsoft.UI.Colors.Transparent;
        }
    }

    private void ApplyNeutralZoneOverlay()
    {
        ApplyNeutralZoneFill(LightZone.Top,    ZoneTopRect);
        ApplyNeutralZoneFill(LightZone.Bottom, ZoneBottomRect);
        ApplyNeutralZoneFill(LightZone.Left,   ZoneLeftRect);
        ApplyNeutralZoneFill(LightZone.Right,  ZoneRightRect);
    }

    private void ApplyNeutralZoneFill(LightZone zone, Microsoft.UI.Xaml.Shapes.Rectangle rect)
    {
        if (!_zoneFillBrushes.TryGetValue(zone, out var brush))
        {
            brush = new SolidColorBrush { Opacity = 0.4 };
            _zoneFillBrushes[zone] = brush;
            rect.Fill = brush;
        }
        // Mid-grey 0x80 with the 0.4 brush opacity reads as a neutral
        // band on both Mica light and Mica dark — neutral enough to
        // say "this zone exists" without competing for attention with
        // the preview substrate.
        brush.Color = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    }

    private void UpdateZoneOverlayColors()
    {
        var engine = AmbientEngine.Current;
        if (engine is null || !engine.IsRunning) return;

        var snapshot = engine.SnapshotEmittedColors();
        if (snapshot.Count == 0) return;

        var settings = AmbientSettingsService.Instance.Current;

        ApplyZoneOverlayColor(LightZone.Top,    ZoneTopRect,    snapshot, settings);
        ApplyZoneOverlayColor(LightZone.Bottom, ZoneBottomRect, snapshot, settings);
        ApplyZoneOverlayColor(LightZone.Left,   ZoneLeftRect,   snapshot, settings);
        ApplyZoneOverlayColor(LightZone.Right,  ZoneRightRect,  snapshot, settings);
    }

    private void ApplyZoneOverlayColor(
        LightZone zone,
        Microsoft.UI.Xaml.Shapes.Rectangle rect,
        IReadOnlyDictionary<string, LightColor> emitted,
        AmbientSettings settings)
    {
        var color = ResolveZoneEmittedColor(emitted, settings, zone);
        if (color is null) return;

        if (!_zoneFillBrushes.TryGetValue(zone, out var brush))
        {
            // Soft fill so the underlying preview cells remain
            // readable through the overlay. Opacity on the Brush
            // (not on the Rectangle) so the dashed stroke stays
            // full-opacity for legibility.
            brush = new SolidColorBrush
            {
                Opacity = 0.4,
            };
            _zoneFillBrushes[zone] = brush;
            rect.Fill = brush;
        }
        brush.Color = Windows.UI.Color.FromArgb(0xFF, color.Value.R, color.Value.G, color.Value.B);
    }

    private static LightColor? ResolveZoneEmittedColor(
        IReadOnlyDictionary<string, LightColor> emitted,
        AmbientSettings settings,
        LightZone zone)
    {
        if (!settings.UseMultiLight)
        {
            return emitted.TryGetValue("group", out var c) ? c : null;
        }
        foreach (var (id, assignedZone) in settings.LightZones)
        {
            if (assignedZone == zone && emitted.TryGetValue(id, out var c))
                return c;
        }
        return null;
    }

    private void UpdateEmittedSwatches()
    {
        var engine = AmbientEngine.Current;
        if (engine is null || !engine.IsRunning)
        {
            if (_swatchByLight.Count > 0)
            {
                EmittedSwatches.Children.Clear();
                _swatchByLight.Clear();
            }
            return;
        }

        var snapshot = engine.SnapshotEmittedColors();
        if (snapshot.Count == 0) return;

        bool setMatches = snapshot.Count == _swatchByLight.Count;
        if (setMatches)
        {
            foreach (var key in snapshot.Keys)
            {
                if (!_swatchByLight.ContainsKey(key)) { setMatches = false; break; }
            }
        }

        if (!setMatches)
        {
            EmittedSwatches.Children.Clear();
            _swatchByLight.Clear();
            foreach (var (id, color) in snapshot)
            {
                var displayName = ResolveLightDisplayName(engine, id);
                var swatch = BuildSwatch(displayName, color);
                _swatchByLight[id] = swatch;
                EmittedSwatches.Children.Add(swatch.Container);
            }
        }
        else
        {
            foreach (var (id, color) in snapshot)
            {
                var entry = _swatchByLight[id];
                entry.Fill.Color = Windows.UI.Color.FromArgb(0xFF, color.R, color.G, color.B);
            }
        }
    }

    private (Microsoft.UI.Xaml.Shapes.Rectangle Container, SolidColorBrush Fill, string DisplayName) BuildSwatch(string displayName, LightColor color)
    {
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, color.R, color.G, color.B));
        var swatchRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 32,
            Height = 32,
            RadiusX = 4,
            RadiusY = 4,
            Fill = fill,
            Stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            StrokeThickness = 1,
        };
        ToolTipService.SetToolTip(swatchRect, displayName);
        return (swatchRect, fill, displayName);
    }

    private static string ResolveLightDisplayName(AmbientEngine engine, string lightKey)
    {
        if (lightKey == "group") return "Group";

        var multi = engine.MultiLights;
        if (multi is not null)
        {
            foreach (var descriptor in multi)
            {
                if (descriptor.Id == lightKey)
                {
                    return string.IsNullOrWhiteSpace(descriptor.Name) ? lightKey : descriptor.Name;
                }
            }
        }
        return lightKey;
    }
}
