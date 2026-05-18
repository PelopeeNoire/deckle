using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Lighting;
using Deckle.Lighting.Ambient;
using Deckle.Lighting.Hue;
using Deckle.Logging;
using Deckle.Playground.ViewModels;
using Deckle.Vision;

namespace Deckle.Playground;

// ─── Ambient lighting tuning page ────────────────────────────────────────────
//
// Owns the screen-capture preview, the Hue pairing surface, the per-light
// zone assignments, and the live HDR tuning sandbox. Extracted from the
// monolithic PlaygroundWindow.xaml.cs in 2026-05-19 and split across
// partial files in the same pass to keep each concern readable :
//
//   • AmbientPage.xaml.cs       — fields, lifecycle, observers, VM sync,
//                                 OnShowZoneOverlaysToggled, theme brush.
//   • AmbientPage.ScreenCapture.cs — local Screen capture toggle + FPS counter.
//   • AmbientPage.Hue.cs        — discover / pair / list groups / test colours.
//   • AmbientPage.LightZones.cs — per-light zone assignment UI + Identify.
//   • AmbientPage.Preview.cs    — sampled-pixel grid + swatch strip + zone
//                                 overlay rendering + idle blanking.
//   • AmbientPage.HdrTuning.cs  — pipeline toggle + Mode / Brightness curve /
//                                 Smoothing / Change threshold / Exposure /
//                                 Saturation / Min brightness sliders.
//
// Lifetime contract :
//   • Page instance is cached (NavigationCacheMode.Required).
//   • Runtime resources (capture service, frame sampler, Hue light
//     output, preview cells, light-zone rects) outlive a single page
//     navigation — they survive in the cached instance until
//     PlaygroundWindow disposes them on Closed via DisposeResources.
//   • Preview timer starts in OnNavigatedTo and stops in OnNavigatingFrom
//     so off-screen pages don't pump unused UI updates.
//   • The AmbientSettingsService.Changed and HuePairingService.BridgeChanged
//     observers are wired once in the constructor and torn down in
//     DisposeResources — cross-window edits (Settings AmbientPage)
//     reflect here without re-subscribing on every navigate.
public sealed partial class AmbientPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public AmbientViewModel ViewModel { get; } = new();

    // Guards programmatic Slider / ComboBox writes during ViewModel sync
    // so the *Changed handlers don't re-fire PushToSettings — same role
    // as `_isSyncing` in GeneralViewModel, mirrored at the page level
    // because the sliders use code-behind event handlers (not x:Bind)
    // due to their custom rescaling (saturation /100, min-brightness
    // int round).
    private bool _initializing;

    // ── Screen capture (J1) — see AmbientPage.ScreenCapture.cs ──────────────
    private ScreenCaptureService? _screenCapture;
    private readonly DispatcherTimer _screenCaptureFpsTimer = new()
        { Interval = TimeSpan.FromMilliseconds(1000) };
    private long _screenCaptureLastSampledFrames;
    private long _screenCaptureLastSampleTimestamp;

    // Glyphs swapped on the screen-capture and pipeline toggle buttons.
    // E768 (Play) when stopped — the action triggered by clicking.
    // E71A (Stop, solid square) when running. \uXXXX escape rather than
    // a literal char so editors that strip private-use codepoints on
    // save don't quietly blank them out.
    private const string ScreenCaptureGlyphStart = "";
    private const string ScreenCaptureGlyphStop  = "";

    // ── Hue REST driver (J2) — see AmbientPage.Hue.cs ───────────────────────
    private CancellationTokenSource? _huePairCts;
    private bool _hueIsPairing;
    private IReadOnlyList<HueGroup> _hueGroups = [];
    private HueRestLightOutput? _hueLightOutput;
    private CancellationTokenSource? _hueRotationCts;
    private bool _hueGroupComboSuppress;
    private bool _hueGroupsFetchInFlight;

    // ── Preview grid + sampler (J3) — see AmbientPage.Preview.cs ────────────
    private FrameSampler? _frameSampler;
    private bool _pipelineStartedCapture;
    private DispatcherTimer? _previewTimer;
    private Microsoft.UI.Xaml.Shapes.Rectangle[]? _previewCells;
    private int _previewGridCols;
    private int _previewGridRows;

    // Native dip size of one downsampled cell in the preview grid. The
    // whole stage lives in a coordinate space of GridCols × CellSize by
    // GridRows × CellSize ; the Viewbox above it scales uniformly.
    private const double PreviewCellSize = 16;

    // Cached swatch chip per light id (or "group" sentinel in single-
    // colour mode). The chip is just a coloured Rectangle — no labels
    // under it (the Lights dropdown carries the names). The brush is
    // mutated in place each tick to avoid rebuilding visuals every
    // frame.
    private readonly Dictionary<string, (Microsoft.UI.Xaml.Shapes.Rectangle Container, SolidColorBrush Fill, string DisplayName)> _swatchByLight = new();

    // Per-zone fill brush for the overlay rectangles on the preview.
    // Brushes carry the live emitted colour the engine sends to
    // whichever light is assigned to that zone, at reduced opacity so
    // the underlying preview cells stay visible.
    private readonly Dictionary<LightZone, SolidColorBrush> _zoneFillBrushes = new();

    // ── Light zones (J4) — see AmbientPage.LightZones.cs ────────────────────
    private List<LightDescriptor>? _placementLights;
    private Dictionary<string, LightZone>? _suggestedZones;

    public AmbientPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        _screenCaptureFpsTimer.Tick += OnScreenCaptureFpsTick;

        // Re-align the LightZonesOverlay Canvas (sibling of the
        // Viewbox) with the displayed footprint of the scaled preview
        // grid whenever the Viewbox resizes — keeps the overlay
        // rectangles tracking the visible pixels at any window size,
        // and keeps the stroke thickness in real DIP space (no scaling
        // artefacts on wide / narrow windows).
        AmbientPreviewViewbox.SizeChanged += (_, _) => LayoutLightZonesOverlayFromViewbox();

        // Cross-window observers wired once for the lifetime of the
        // page instance. DisposeResources tears them down on Window
        // close.
        HuePairingService.Instance.BridgeChanged += OnHueBridgeChanged;
        AmbientSettingsService.Instance.Changed  += OnAmbientSettingsChanged;
        if (AmbientEngine.Current is { } engine)
        {
            engine.StateChanged += OnAmbientEngineStateChanged;
        }

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += OnPageLoaded;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // First-load initialisation. Loaded fires once for the cached
        // Page instance ; subsequent OnNavigatedTo calls do a lighter
        // re-sync (ViewModel.Load + push to sliders) without re-running
        // the wiring below.
        _initializing = true;
        try
        {
            ViewModel.Load();
            SyncHueUiFromService();
            SyncPipelineUiFromViewModel();
            PushViewModelToControls();
            ApplyPipelineReadiness();
        }
        finally
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => _initializing = false);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Re-load the VM in case settings changed from another surface
        // (Settings AmbientPage) while we were on another Playground
        // page. The Loaded handler above does a similar pass on first
        // navigation ; here we just refresh, no wiring.
        bool prev = _initializing;
        _initializing = true;
        try
        {
            ViewModel.Load();
            PushViewModelToControls();
            SyncPipelineUiFromViewModel();
            ApplyPipelineReadiness();
        }
        finally
        {
            _initializing = prev;
        }

        // Only start the preview timer if there's actually something
        // to display — otherwise the page sits idle without burning
        // a 5 Hz tick on the dispatcher (a noticeable cause of window-
        // drag lag on the Playground when the user wasn't running the
        // pipeline anyway).
        EvaluatePreviewTimerState();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        // Off-screen page : stop pumping UI updates for cells that
        // aren't visible. The canonical AmbientEngine keeps pushing
        // to Hue.
        _previewTimer?.Stop();
    }

    // Called by PlaygroundWindow on Closed — tears down runtime
    // resources that outlive the page instance under
    // NavigationCacheMode.Required.
    public void DisposeResources()
    {
        _previewTimer?.Stop();
        _screenCaptureFpsTimer.Stop();

        StopScreenCaptureIfRunning();
        TeardownHueIfActive();

        HuePairingService.Instance.BridgeChanged -= OnHueBridgeChanged;
        AmbientSettingsService.Instance.Changed  -= OnAmbientSettingsChanged;
        if (AmbientEngine.Current is { } engine)
        {
            engine.StateChanged -= OnAmbientEngineStateChanged;
        }
    }

    // ── Service observers ────────────────────────────────────────────────────

    private void OnHueBridgeChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            SyncHueUiFromService();
            ApplyPipelineReadiness();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncHueUiFromService();
                ApplyPipelineReadiness();
            });
        }
    }

    private void OnAmbientSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            bool prev = _initializing;
            _initializing = true;
            try
            {
                ViewModel.Load();
                PushViewModelToControls();
                SyncPipelineUiFromViewModel();
                ApplyPipelineReadiness();
            }
            finally
            {
                _initializing = prev;
            }
        });
    }

    private void OnAmbientEngineStateChanged(AmbientEngineState state)
    {
        // Re-evaluate the preview timer when the engine starts or
        // stops so an idle Playground page doesn't keep pumping the
        // dispatcher at 5 Hz. AmbientEngine.StateChanged can fire from
        // any thread — marshal to the UI thread before touching the
        // timer / blanking the cells.
        if (DispatcherQueue.HasThreadAccess)
        {
            EvaluatePreviewTimerState();
        }
        else
        {
            DispatcherQueue.TryEnqueue(EvaluatePreviewTimerState);
        }
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────

    // "Zones" ToggleButton on the preview options row. Hides / shows
    // the LightZonesOverlay Canvas (the rectangles drawn over the
    // sampled grid). Also re-evaluates the preview / placeholder
    // visibility — when the user unchecks zones while the engine is
    // off, the empty-state takes the stage (visibility contract in
    // UpdatePreviewViewboxVisibility).
    private void OnShowZoneOverlaysToggled(object sender, RoutedEventArgs e)
    {
        if (LightZonesOverlay is null) return;
        LightZonesOverlay.Visibility = (ShowZoneOverlaysToggle.IsChecked == true)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePreviewViewboxVisibility();
    }

    // ── Theme resource helper ───────────────────────────────────────────────
    //
    // Looking up a system fill colour brush from code-behind requires
    // going through Application.Current.Resources (the ThemeResource
    // markup extension only resolves in XAML). Returns the resolved
    // Brush ; the cast is safe because the SystemFillColor* entries in
    // the WinUI theme dictionary are SolidColorBrush.
    private static Brush GetThemeBrush(string key)
        => (Brush)Application.Current.Resources[key];
}
