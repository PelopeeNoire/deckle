using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Deckle.Audio;
using Deckle.Chrono.Hud;
using Deckle.Composition;
using Deckle.Interop;
using Deckle.Lighting;
using Deckle.Lighting.Ambient;
using Deckle.Lighting.Hue;
using Deckle.Logging;
using Deckle.Shell;
using Deckle.Vision;

namespace Deckle.Playground;

// ─── HUD playground window ────────────────────────────────────────────────────
//
// Long-lived tuning surface for the HUD composition stroke. Ported from the
// standalone `dev/HudPlayground` tool into a first-party Deckle window.
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
// fixed, no concept-level tabs. A draggable sash between the two lets the
// developer trade preview footprint for tuning width when a slider block
// gets tight.
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

    // Not readonly: OnResetAllClick swaps the whole instance for a fresh
    // `new TuningModel()` to snap fields back to compiled defaults. Every
    // row-factory lambda captures `_tuning` by closure over `this`, so the
    // lambdas see the swapped instance on the next edit — no need to
    // rebuild the panel purely for the swap.
    private TuningModel _tuning = new();

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
    // the swipe wave is the main reason these targets are selected here.
    private bool _simulateChangedDigits = true;

    private Target _currentTarget = Target.Transcribing;
    // Pause par défaut : la fenêtre s'ouvre sans animation, l'utilisateur
    // appuie sur Play pour démarrer. Cohérent avec la consigne "au départ
    // il ne doit y avoir rien" et la mise en pause systématique forcée à
    // chaque ShowAndActivate (cf. ce constructeur de classe).
    private bool   _isPlaying     = false;

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
    // that was reported). See NakedPreview class comment in HudComposition.
    private HudComposition.NakedPreview? _nakedPreview;

    // ── Ambient lighting — screen capture (J1) ─────────────────────────
    //
    // Created lazily on the first Start click ; disposed on Stop, on
    // Closing→Hide (so a hidden Playground doesn't keep the capture
    // pipeline running silently), and on the terminal Closed event.
    // FrameArrived fires on the capture service's worker thread (the
    // DXGI Output Duplication poll loop) ; the UI timer samples
    // FrameCount on the UI thread once a second to compute FPS without
    // per-frame DispatcherQueue marshaling.
    private ScreenCaptureService? _screenCapture;
    private readonly DispatcherTimer _screenCaptureFpsTimer = new()
        { Interval = TimeSpan.FromMilliseconds(1000) };
    private long _screenCaptureLastSampledFrames;
    private long _screenCaptureLastSampleTimestamp;

    // ── Ambient lighting — Hue REST driver (J2) ────────────────────────
    //
    // The Hue bridge state (paired client, credentials, IP) is owned
    // by HuePairingService.Instance — the process-wide singleton that
    // both the Playground and the Settings AmbientPage consume. The
    // Playground tracks two transient pieces of state on its own :
    // the local CancellationTokenSource for an in-flight pair (so the
    // Closing handler can cancel it), and a guard flag against double-
    // clicks on the Pair button. Everything else (current bridge,
    // discover results, persist creds, fire BridgeChanged) lives in
    // the service.
    //
    // Closing→Hide intentionally does NOT forget the bridge. The
    // canonical AmbientEngine running in App also consumes the
    // service, so tearing down here would also kill ambient lighting
    // for the rest of the app — which is wrong now that the service
    // is shared. The user explicitly clicks "Forget" (Settings or a
    // future Playground action) to invalidate the credentials.
    private CancellationTokenSource? _huePairCts;
    private bool _hueIsPairing;
    private IReadOnlyList<HueGroup> _hueGroups = [];
    private HueRestLightOutput? _hueLightOutput;
    private CancellationTokenSource? _hueRotationCts;
    private bool _hueGroupComboSuppress;

    // ── Ambient lighting — end-to-end pipeline (J3) ────────────────────
    //
    // The engine is instantiated lazily on the first Start pipeline
    // click ; it borrows the existing _screenCapture and
    // _hueLightOutput so capture cadence and bridge auth are shared
    // with the standalone test cards. DisposeAsync is fire-and-forget
    // on Closing (best-effort cleanup, the loop cancels via its CTS).
    //
    // FrameSampler is built on the first Start pipeline click after
    // _screenCapture has been started — we need its Device and
    // ContentSize. The sampler lives for one pipeline session ; it's
    // disposed when the user clicks Stop or closes the window.
    //
    // _pipelineStartedCapture tracks who turned on the capture so we
    // can mirror the symmetry on Stop : if the pipeline started it
    // (Louis clicked Start pipeline while capture was idle), the
    // pipeline stops it too. If Louis had started capture manually
    // before, we leave it running.
    //
    // _previewTimer reads _frameSampler.LatestSample every 200 ms and
    // updates the preview grid Rectangles' Fill brushes. 200 ms is
    // a deliberate visual cadence — humans don't notice updates above
    // 5 Hz on a low-frequency colour grid, and the timer doesn't fight
    // the engine's 15 Hz push for the same data.
    // The canonical AmbientEngine instance lives in App.AmbientEngine ;
    // the Playground is an observer for status and never instantiates
    // its own engine.
    private FrameSampler? _frameSampler;
    private bool _pipelineStartedCapture;
    private DispatcherTimer? _previewTimer;
    private Microsoft.UI.Xaml.Shapes.Rectangle[]? _previewCells;

    // Cached swatch Border + brush per light id (or "group" sentinel
    // in single-colour mode). Lets the 200 ms tick mutate the brush
    // colour instead of rebuilding the visuals every frame, while
    // still rebuilding the panel when the set of light ids changes
    // (group ↔ multi switch, pairing change). Mirrors the rationale
    // behind the per-cell brush refactor that resolved the black-
    // preview-grid bug on 2026-05-15.
    private readonly System.Collections.Generic.Dictionary<string, (Microsoft.UI.Xaml.Controls.Border Container, SolidColorBrush Fill, TextBlock Label)> _swatchByLight = new();

    // ── Ambient lighting — light zones (J4) ─────────────────────────
    //
    // _placementLights is the cached list of fixtures returned by the
    // connected IMultiLightOutput, populated when a Hue group is
    // selected (after ConnectAsync) and reset when the group changes.
    //
    // The per-light zone picker is a DropDownButton + MenuFlyout in
    // the LightZonesPanel rows. We don't keep a back-reference to the
    // buttons : selections flow through the menu item Click handler
    // which carries everything it needs in its Tag (ZoneMenuTag), and
    // a teardown is just LightZonesPanel.Children.Clear() — the GC
    // collects the orphaned visual subtree, lambdas captured by menu
    // items go with it.
    //
    // PreviewCellSize is the native dip size of one downsampled cell
    // in the preview grid. The whole stage (preview grid + zone
    // overlay) lives in a coordinate space sized at GridCols × CellSize
    // by GridRows × CellSize ; the Viewbox above it scales the whole
    // composition uniformly to fill the available area.
    private const double PreviewCellSize = 16;
    private List<LightDescriptor>? _placementLights;

    // Zone suggestion derived from a Hue entertainment area's positions.
    // Computed once per group selection in ResolveLightsAndBuildPlacementAsync
    // and read by BuildLightZonesUi to pre-fill ComboBoxes for lights
    // the user hasn't explicitly mapped yet. Null = no matching
    // entertainment area was found (or fetching failed) and the
    // ComboBoxes start on "None".
    private Dictionary<string, LightZone>? _suggestedZones;

    // Total UX duration of an Identify flash. The bridge would
    // happily run alert=lselect for 15 s ; we cap it at 3 s so the
    // user spots the lamp without sitting through a strobe, then we
    // send alert=none to cut the flash short.
    private static readonly TimeSpan IdentifyFlashDuration = TimeSpan.FromSeconds(3);

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

        Title = "Deckle Playground";
        // Default 1800×900: NavView pane (320) + HUD page (1200 min) +
        // breathing room for the tuning expanders to sit comfortably wide
        // out of the box. Min 1520×600 is still a usable surface, just
        // tight ; first-launch size aims for the comfortable end.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1800, 1440));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        // Min width 1280: NavView is now LeftCompact (48 dip strip) with
        // the pane closed by default, so the playground content gets
        // ~1230 dip to work with at minimum. HUD page wants 1200 dip
        // (preview 720 + tuning 480), Ambient page wants 480 + 380 + 12
        // gap ≈ 875 dip — both comfortable below 1280. Previous 1520
        // cap was sized for the auto-expanded pane that we no longer
        // surface at startup. Windows DPI scaling carries the rest :
        // the dip-based layout adapts to the user's effective scale,
        // so a 13" laptop @ 1920×1080 @ 150 % effective renders this
        // window at 1280×600 dip without overflow.
        presenter.PreferredMinimumWidth  = 1280;
        presenter.PreferredMinimumHeight = 600;
        AppWindow.SetPresenter(presenter);

        // Close → hide, never destroy. Same contract as SettingsWindow /
        // LogWindow — App owns the single instance for its lifetime.
        // Side effect: stop the screen capture if it was running, so a
        // hidden Playground doesn't keep pulling frames from the GPU.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            StopScreenCaptureIfRunning();
            TeardownHueIfActive();
            AppWindow.Hide();
        };

        _screenCaptureFpsTimer.Tick += OnScreenCaptureFpsTick;

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
                // No persistence: row factories read the in-memory _tuning,
                // sim*, and HudChrono / HudComposition statics — those start
                // at the compiled defaults the running app is using. The
                // Playground tunes them in-process for the current session
                // and writes nothing to disk; the next app launch returns
                // to the built-in defaults.
                BuildTuningPanel();
                // Populate the target cards ItemsRepeater. Done in Loaded
                // (not in the constructor) because ItemsRepeater isn't
                // realised until the XAML tree finishes initialising —
                // setting ItemsSource earlier silently no-ops.
                TargetCardsRepeater.ItemsSource = _targetCardNames;
                ApplyTarget();
                // Project HuePairingService state into the row visuals.
                // The service auto-restored its bridge from settings on
                // first access, so paired sessions land here with the
                // row already showing "Paired" without further work.
                SyncHueUiFromService();

                // Stay in sync with re-pair / forget operations that
                // happen from another surface (Settings AmbientPage)
                // while the Playground is open.
                HuePairingService.Instance.BridgeChanged += OnHueBridgeChangedFromPlayground;

                // Mirror the persisted UseMultiLight value into the
                // RadioButtons. SelectedIndex 0 = group, 1 = per-zone.
                // The SelectionChanged handler guards against feedback
                // loops by comparing settings before writing back.
                PipelineModeRadios.SelectedIndex =
                    AmbientSettingsService.Instance.Current.UseMultiLight ? 1 : 0;

                // Reflect the master Enabled state on the Pipeline
                // toggle, and stay in sync with subsequent flips made
                // from the tray menu or the AmbientPage.
                SyncPipelineUiFromSettings();

                // Free the Turn-on button as soon as the persisted
                // pair state is enough to let the App-side engine
                // start (bridge paired + a group id saved). The
                // Playground used to gate the button on a local
                // ConnectAsync that only ran after the user clicked
                // through Hue → List groups → pick, leaving the
                // button greyed forever for someone who paired from
                // Settings and just expected to flip the switch here.
                ApplyPipelineReadiness();

                // Engine state observer — kept around so other surfaces
                // can react later, but the preview lifecycle no longer
                // hangs off it : we just start the timer here and let
                // OnPreviewTimerTick poll LatestSample. That way the
                // grid lights up whether the engine was already running
                // at open time, or starts later via the tray, Settings,
                // or the Playground toggle itself — no race on the
                // first StateChanged transition.
                if (AmbientEngine.Current is { } engine)
                {
                    engine.StateChanged += OnAmbientEngineStateChangedFromPlayground;
                }

                // Timer runs for the lifetime of the Playground window.
                // When AmbientEngine.LatestSample is null (engine off /
                // not yet warm) OnPreviewTimerTick early-returns ; when
                // it's non-null the grid lazy-builds and the cells
                // animate. Cheap enough to leave running all the time,
                // and immune to the StateChanged ordering bugs we hit
                // by gating it on Running.
                StartPreviewTimer();

                // Populate the HDR tuning sandbox sliders from the live
                // settings + flip the re-fire suppressor off so the
                // user's slider drags write back through.
                InitPlaygroundAmbientTuning();

                AmbientSettingsService.Instance.Changed += OnAmbientSettingsChangedFromPlayground;
            };
        }

        // Tooltip override : the previous attempt ran from root.Loaded
        // which fires before the NavigationView has materialised its
        // template parts, so FindVisualDescendantByName("TogglePaneButton")
        // returned null and the FR tooltip stayed. Hooking on the
        // NavView's own Loaded gets us closer, and the DispatcherQueue
        // Low-priority enqueue lets the template generator finish
        // before we walk the tree. Belt and braces : we also re-attempt
        // on PaneOpened in case the toggle button only appears after
        // the user expands the pane manually.
        PlaygroundNav.Loaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => OverrideNavPaneToggleTooltip(PlaygroundNav, "Open navigation"));
        };
        PlaygroundNav.PaneOpened += (_, _) =>
            OverrideNavPaneToggleTooltip(PlaygroundNav, "Open navigation");
        PlaygroundNav.PaneClosed += (_, _) =>
            OverrideNavPaneToggleTooltip(PlaygroundNav, "Open navigation");

        // Tap on empty background → move focus to RootGrid, which
        // dismisses the caret from a NumberBox the user just edited.
        // CRITICAL : filter on OriginalSource. Tapped is a routed event
        // and WinUI 3's ButtonBase derivatives (Button, DropDownButton,
        // ToggleButton, ComboBox, NumberBox spin buttons …) do NOT mark
        // it Handled when they fire Click. Without this guard, every
        // click on a button or dropdown bubbles here, RootGrid steals
        // focus mid-action, and the flyout / dropdown loses its anchor
        // before the click is fully processed. Symptom : "the
        // DropDownButton doesn't open on the first click", "the
        // ComboBox needs 3-4 clicks to select" — the actual cause of
        // the bug we've been chasing for two passes. By scoping
        // dismissal to clicks that land on the RootGrid itself
        // (transparent empty area), the focus shift only happens for
        // genuine background taps.
        RootGrid.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, RootGrid))
                RootGrid.Focus(FocusState.Pointer);
        };

        this.Closed += (_, _) =>
        {
            _rmsTimer.Stop();
            _rmsClock.Stop();
            _rebuildDebounce.Stop();
            _screenCaptureFpsTimer.Stop();
            StopScreenCaptureIfRunning();
            TeardownHueIfActive();
            HuePairingService.Instance.BridgeChanged -= OnHueBridgeChangedFromPlayground;
            if (AmbientEngine.Current is { } engine)
                engine.StateChanged -= OnAmbientEngineStateChangedFromPlayground;
            // Detach the composition child first so the compositor stops
            // referencing the bundle's visual tree, then dispose. In the
            // singleton-hidden pattern (Closing→Hide) Closed only fires at
            // QuitApp so this is a belt-and-braces rather than a hot path.
            ElementCompositionPreview.SetElementChildVisual(NakedPreviewHost, null);
            _nakedPreview?.Dispose();
            _nakedPreview = null;
        };
    }

    // BridgeChanged handler — marshal to the UI thread because the
    // event can fire from any thread (a Settings page pair handler,
    // an AmbientEngine restore at start, …) and SyncHueUiFromService
    // touches XAML elements.
    private void OnHueBridgeChangedFromPlayground()
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

    // Sync the Hue pairing row visuals from HuePairingService state.
    // The service auto-restores its bridge at first access (lazy
    // singleton ctor), so by the time the Playground Loaded handler
    // calls us here the bridge is already either paired-from-settings
    // or absent (first run / forgotten state). We only project that
    // state into the UI — populate the IP textbox, swap the Pair
    // label between "Pair" / "Re-pair", flip the status dot, enable
    // the next-step row. No network call here — the first REST
    // request happens when the user clicks List groups or selects a
    // group ; that's where a stale username / unreachable bridge
    // surfaces.
    //
    // Idempotent : safe to call again whenever BridgeChanged fires
    // (e.g. the user pairs from Settings while the Playground is
    // open).
    private void SyncHueUiFromService()
    {
        var paired = HuePairingService.Instance.PairedBridge;
        var bridge = HuePairingService.Instance.Bridge;

        if (paired is null || bridge is null || !bridge.IsPaired)
        {
            // Nothing persisted yet — first run, or the user forgot
            // the bridge. Leave the IP textbox empty so the user can
            // discover or type, reset the row to the unpaired visual.
            HueBridgeIpTextBox.Text = string.Empty;
            HuePairLabel.Text       = "Pair (press link button)";
            HuePairStatusText.Text  = "Not paired";
            HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorNeutralBrush");
            HueListGroupsButton.IsEnabled = false;
            return;
        }

        var creds = bridge.Credentials!;
        HueBridgeIpTextBox.Text = paired.InternalIpAddress;
        HuePairLabel.Text       = "Re-pair";
        HuePairStatusText.Text  = $"Paired ({creds.UsernameHead}, saved)";
        HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorSuccessBrush");
        HueListGroupsButton.IsEnabled = true;
    }

    // ── Lifecycle surface (called by App) ───────────────────────────────

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        // Reset to Pause systematically on each show — état connu et
        // prévisible à chaque réouverture, indépendant de ce que
        // l'utilisateur a laissé en quittant. Le SelectionChanged
        // déclenche OnPlayPauseSelectionChanged → _isPlaying = false →
        // ApplyTarget() collapse la preview. Si déjà sur 1, no-op.
        if (PlayPauseGroup.SelectedIndex != 1)
            PlayPauseGroup.SelectedIndex = 1;

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

    // ── Target selector cards + Play/Pause ──────────────────────────────
    //
    // The 7 target cards (Charging, Recording, Transcribing, Rewriting,
    // Conic, ArcMask, Combined) live inside an ItemsRepeater +
    // UniformGridLayout so they reflow into multiple rows as the column
    // narrows. Mutual exclusion is enforced manually : exactly one card
    // is checked at any time, clicking an unchecked card checks it +
    // unchecks the previously-active, clicking the active card is a no-op
    // (the Unchecked handler re-checks it). _suppressTargetCardChange
    // guards against re-entrancy when we set IsChecked programmatically.
    private static readonly string[] _targetCardNames =
    {
        "Charging", "Recording", "Transcribing", "Rewriting", "Conic", "ArcMask", "Combined"
    };
    private readonly List<ToggleButton> _targetCards = new();
    private bool _suppressTargetCardChange;

    private void OnTargetCardPrepared(
        Microsoft.UI.Xaml.Controls.ItemsRepeater sender,
        Microsoft.UI.Xaml.Controls.ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not ToggleButton tb) return;

        // ElementPrepared fires once per recycled element ; the same
        // ToggleButton may be re-prepared if the layout virtualises rows.
        // Subscribe only once.
        if (!_targetCards.Contains(tb))
        {
            tb.Checked   += OnTargetCardChecked;
            tb.Unchecked += OnTargetCardUnchecked;
            _targetCards.Add(tb);
        }

        // Set initial IsChecked to match the current target, suppressing
        // the event so the mutual-exclusion handler doesn't fire during
        // initialisation.
        bool isCurrent = ParseTargetCardName(tb.Content as string ?? "") == _currentTarget;
        _suppressTargetCardChange = true;
        try   { tb.IsChecked = isCurrent; }
        finally { _suppressTargetCardChange = false; }
    }

    private void OnTargetCardChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressTargetCardChange) return;
        if (sender is not ToggleButton clicked) return;

        _suppressTargetCardChange = true;
        try
        {
            foreach (var card in _targetCards)
                if (!ReferenceEquals(card, clicked)) card.IsChecked = false;
        }
        finally { _suppressTargetCardChange = false; }

        var next = ParseTargetCardName(clicked.Content as string ?? "");
        if (next == _currentTarget) return;
        _currentTarget = next;
        ApplyTarget();
    }

    private void OnTargetCardUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressTargetCardChange) return;
        if (sender is not ToggleButton card) return;

        // Clicking the already-checked card unchecks it ; we re-check
        // immediately so at least one card stays selected at all times.
        _suppressTargetCardChange = true;
        try   { card.IsChecked = true; }
        finally { _suppressTargetCardChange = false; }
    }

    private static Target ParseTargetCardName(string name) => name switch
    {
        "Charging"     => Target.Charging,
        "Recording"    => Target.Recording,
        "Transcribing" => Target.Transcribing,
        "Rewriting"    => Target.Rewriting,
        "Conic"        => Target.Conic,
        "ArcMask"      => Target.ArcMask,
        _              => Target.Combined,
    };

    // Top-level navigation between the HUD page and the Ambient lighting
    // page. Pages are inline (Visibility toggle), not Frame-navigated —
    // both surfaces are cheap to keep instantiated and the alternative
    // (Frame + Page classes per surface) would force a refactor of every
    // x:Name reference scattered across this file. The HUD preview keeps
    // animating in the background when Ambient lighting is selected ;
    // suspending it on hide-tab is a polish item for later.
    //
    // First SelectionChanged fires during InitializeComponent (NavView
    // applies IsSelected="True" on NavItemHud) before HudPage is realised ;
    // null-guard on the page references so the early fire is a no-op.
    private void OnPlaygroundNavSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (HudPage is null || AmbientPage is null || HomePage is null) return;
        if (args.SelectedItem is not NavigationViewItem item) return;

        string tag = (item.Tag as string) ?? "home";
        HomePage.Visibility    = tag == "home"    ? Visibility.Visible : Visibility.Collapsed;
        HudPage.Visibility     = tag == "hud"     ? Visibility.Visible : Visibility.Collapsed;
        AmbientPage.Visibility = tag == "ambient" ? Visibility.Visible : Visibility.Collapsed;
    }

    // Home-card click handlers : route to the matching NavView item
    // so the existing OnPlaygroundNavSelectionChanged centralises the
    // visibility toggle. Keeps a single state-transition path.
    private void OnHomeHudCardClick(object sender, RoutedEventArgs e)
    {
        PlaygroundNav.SelectedItem = NavItemHud;
    }

    private void OnHomeAmbientCardClick(object sender, RoutedEventArgs e)
    {
        PlaygroundNav.SelectedItem = NavItemAmbient;
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

    // ── Reset all ───────────────────────────────────────────────────────
    //
    // The Playground has no persistence: tuning lives in memory for the
    // current process and dies on app exit. "Reset all" is just a "snap
    // back to compiled defaults" affordance — useful mid-session to start
    // over without quitting the app. We go through the shipping-default
    // objects directly instead of chaining the per-section Reset*()
    // methods: those would call RebuildTuningPanel once each, here we
    // want a single atomic transition.
    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        // TuningModel: swap for a fresh instance — its field initialisers
        // ARE the shipping defaults.
        _tuning = new TuningModel();

        // Sim fields: keep aligned with the defaults documented at the
        // field declarations above. Source of truth duplicated once here
        // and in the field initialisers — acceptable for this playground
        // scope.
        _simRmsMin             = 0.013f;
        _simRmsMax             = 0.100f;
        _simRmsPeriodSeconds   = 2.0f;
        _simManualOverride     = false;
        _simManualValue        = 0.012f;
        _simulateChangedDigits = true;

        // Swipe + Audio mapping expanders — the same values the individual
        // Reset* methods use. Swipe statics live on SwipeWaveAnimator since
        // 2026-05-02 (Deckle.Composition); audio mapping statics still
        // belong to HudChrono pending a future Capture migration.
        SwipeWaveAnimator.SwipeCycleSeconds = 3.0f;
        SwipeWaveAnimator.SwipeEaseP1       = new Vector2(0.5f, 0f);
        SwipeWaveAnimator.SwipeEaseP2       = new Vector2(0.2f, 1f);
        SwipeWaveAnimator.SwipeRiseAlpha    = 0.1f;
        SwipeWaveAnimator.SwipeDecayAlpha   = 0.025f;
        SwipeWaveAnimator.SwipeHeadDomain   = 8;
        AudioLevelMapper.EmaAlpha          = 0.25f;
        AudioLevelMapper.MinDbfs           = -55f;
        AudioLevelMapper.MaxDbfs           = -32f;
        AudioLevelMapper.DbfsCurveExponent = 1.0f;

        // HudComposition geometry mutables tuned via the HUD geometry
        // expander.
        HudComposition.InsetDip = -2f;

        BuildTuningPanel();
        ApplyTarget();
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

        AddHudGeometryExpander();
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

    // HUD geometry — dimensions of the stroke rect itself, independent of
    // any conic/arc/recording variant. InsetDip lives on HudComposition
    // (paint-time, baked into the stroke geometry on creation), so the
    // slider must trigger a stroke rebuild on change. Range covers a
    // generous tuning band around the shipping default (-6) so the
    // developer can explore both "stroke flush with edge" and "stroke pulled well
    // outward" without re-touching the slider bounds.
    private void AddHudGeometryExpander()
    {
        var stack = NewExpander("HUD geometry", ResetHudGeometry);
        AddFloatRow(stack, "InsetDip", -16, 4, 0.5, HudComposition.InsetDip,
            v => HudComposition.InsetDip = (float)v, rebuild: true);
    }

    private void ResetHudGeometry()
    {
        HudComposition.InsetDip = -2f;
        RebuildTuningPanel();
        RequestRebuild();
    }

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
        // Static mutables on SwipeWaveAnimator (Deckle.Composition) — read
        // live each vsync by the animator's Tick, no rebuild needed.
        AddFloatRow(stack, "SwipeCycleSeconds", 0.1, 6.0, 0.1,
            SwipeWaveAnimator.SwipeCycleSeconds,
            v => SwipeWaveAnimator.SwipeCycleSeconds = (float)v);
        AddFloatRow(stack, "SwipeEaseP1.X", 0, 1, 0.05, SwipeWaveAnimator.SwipeEaseP1.X,
            v => SwipeWaveAnimator.SwipeEaseP1 = new Vector2((float)v, SwipeWaveAnimator.SwipeEaseP1.Y));
        AddFloatRow(stack, "SwipeEaseP1.Y", -0.5, 1.5, 0.05, SwipeWaveAnimator.SwipeEaseP1.Y,
            v => SwipeWaveAnimator.SwipeEaseP1 = new Vector2(SwipeWaveAnimator.SwipeEaseP1.X, (float)v));
        AddFloatRow(stack, "SwipeEaseP2.X", 0, 1, 0.05, SwipeWaveAnimator.SwipeEaseP2.X,
            v => SwipeWaveAnimator.SwipeEaseP2 = new Vector2((float)v, SwipeWaveAnimator.SwipeEaseP2.Y));
        AddFloatRow(stack, "SwipeEaseP2.Y", -0.5, 1.5, 0.05, SwipeWaveAnimator.SwipeEaseP2.Y,
            v => SwipeWaveAnimator.SwipeEaseP2 = new Vector2(SwipeWaveAnimator.SwipeEaseP2.X, (float)v));
        AddFloatRow(stack, "SwipeRiseAlpha", 0.01, 1.0, 0.01, SwipeWaveAnimator.SwipeRiseAlpha,
            v => SwipeWaveAnimator.SwipeRiseAlpha = (float)v);
        AddFloatRow(stack, "SwipeDecayAlpha", 0.005, 0.5, 0.005, SwipeWaveAnimator.SwipeDecayAlpha,
            v => SwipeWaveAnimator.SwipeDecayAlpha = (float)v);
        AddIntRow(stack, "SwipeHeadDomain", 6, 12, SwipeWaveAnimator.SwipeHeadDomain,
            v => SwipeWaveAnimator.SwipeHeadDomain = v);
        AddToggleRow(stack, "Simulate changed digits",
            _simulateChangedDigits,
            v => { _simulateChangedDigits = v; ApplyTarget(); });
    }

    private void ResetSwipe()
    {
        SwipeWaveAnimator.SwipeCycleSeconds = 3.0f;
        SwipeWaveAnimator.SwipeEaseP1       = new Vector2(0.7f, 0f);
        SwipeWaveAnimator.SwipeEaseP2       = new Vector2(0.1f, 1f);
        SwipeWaveAnimator.SwipeRiseAlpha    = 0.05f;
        SwipeWaveAnimator.SwipeDecayAlpha   = 0.025f;
        SwipeWaveAnimator.SwipeHeadDomain   = 8;
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
        // Static mutables on AudioLevelMapper (Deckle.Audio) — no
        // rebuild, read live each sample.
        AddFloatRow(stack, "EmaAlpha", 0, 1, 0.05, AudioLevelMapper.EmaAlpha,
            v => AudioLevelMapper.EmaAlpha = (float)v);
        AddFloatRow(stack, "MinDbfs", -80, 0, 1, AudioLevelMapper.MinDbfs,
            v => AudioLevelMapper.MinDbfs = (float)v);
        AddFloatRow(stack, "MaxDbfs", -60, 0, 1, AudioLevelMapper.MaxDbfs,
            v => AudioLevelMapper.MaxDbfs = (float)v);
        AddFloatRow(stack, "DbfsCurveExponent", 0.5, 4, 0.05, AudioLevelMapper.DbfsCurveExponent,
            v => AudioLevelMapper.DbfsCurveExponent = (float)v);
    }

    private void ResetAudioMapping()
    {
        AudioLevelMapper.EmaAlpha          = 0.25f;
        AudioLevelMapper.MinDbfs           = -55f;
        AudioLevelMapper.MaxDbfs           = -32f;
        AudioLevelMapper.DbfsCurveExponent = 1.0f;
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
    //
    // The Playground does not persist tuning — RebuildTuningPanel is just
    // a UI refresh + stroke rebuild after a Reset* action.
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
    // tuning column is resizable now — if the user wants more label room
    // they drag the sash.

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

    // ── Ambient lighting — screen capture (J1) ─────────────────────────────
    //
    // Wires the Start / Stop button in the Ambient lighting card to a
    // freshly-built ScreenCaptureService. The service is created on first
    // Start so the capture pipeline stays inert until the user opts in.
    // FrameArrived is left unhandled at J1 — we only count frames via
    // ScreenCaptureService.FrameCount, no per-frame UI work. The FPS timer
    // samples the counter once a second and computes an FPS estimate from
    // the delta over the sample interval (low jitter, no per-frame
    // DispatcherQueue marshaling).

    // Segoe Fluent Icons glyphs swapped on the toggle button. E768 (Play)
    // when stopped — the action triggered by clicking. E71A (Stop, the
    // solid square) when running. Mirrors the Play/Pause RadioButton
    // pattern used for the HUD preview row: the icon names what the next
    // click does, not the current state.
    private const string ScreenCaptureGlyphStart = "\uE768";
    private const string ScreenCaptureGlyphStop  = "\uE71A";

    private void OnScreenCaptureToggleClick(object sender, RoutedEventArgs e)
    {
        // Entry log so we can confirm the Click reaches the handler ;
        // without it a non-firing event is indistinguishable from a
        // silent failure inside Start(). Verbose only — once the J1
        // pump is stable this can come out or move to a per-session
        // counter.
        LogService.Instance.Verbose(LogSource.Screen,
            $"playground toggle | running={_screenCapture is { IsRunning: true }}");

        if (_screenCapture is { IsRunning: true })
        {
            StopScreenCaptureIfRunning();
            return;
        }

        StartScreenCaptureService();
    }

    // Shared start logic for the Screen capture card and the Pipeline
    // card — both want the exact same setup (instantiate service if
    // null, subscribe Stopped, Start, refresh visuals, kick the FPS
    // timer). Returns true on success so the Pipeline path can decide
    // whether to proceed with engine construction or abort.
    private bool StartScreenCaptureService()
    {
        if (_screenCapture is { IsRunning: true }) return true;

        try
        {
            _screenCapture ??= new ScreenCaptureService();
            _screenCapture.Stopped += OnScreenCaptureStopped;
            // Read the persisted monitor selection — null = primary. The
            // settings field is populated by the J9 monitor selector in
            // AmbientPage ; until then it stays null and capture follows
            // the primary, matching the V0 behaviour.
            var targetMonitor = AmbientSettingsService.Instance.Current.SelectedMonitorDeviceName;
            _screenCapture.Start(targetMonitor);

            _screenCaptureLastSampledFrames = 0;
            _screenCaptureLastSampleTimestamp = Stopwatch.GetTimestamp();
            ScreenCaptureToggleIcon.Glyph = ScreenCaptureGlyphStop;
            ScreenCaptureToggleLabel.Text = "Stop";
            ScreenCaptureStatusText.Text = "Running";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorSuccessBrush");
            ScreenCaptureFramesText.Text = "0";
            ScreenCaptureFpsText.Text = "—";
            _screenCaptureFpsTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            // Service already logged Error + Verbose with the HRESULT in
            // its catch block — we only surface the recovered state to
            // the UI here. The button stays on "Start" so the user can
            // try again after fixing the underlying condition. Status dot
            // turns critical so the failure is visible at a glance even
            // without reading the message text.
            LogService.Instance.Warning(LogSource.Screen,
                $"Playground toggle aborted — {ex.GetType().Name}: {ex.Message}");
            ScreenCaptureStatusText.Text = $"Failed: {ex.Message}";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorCriticalBrush");
            StopScreenCaptureIfRunning();
            return false;
        }
    }

    private void StopScreenCaptureIfRunning()
    {
        _screenCaptureFpsTimer.Stop();

        if (_screenCapture is null) return;

        _screenCapture.Stopped -= OnScreenCaptureStopped;
        _screenCapture.Dispose();
        _screenCapture = null;

        // Reset visuals to the stopped baseline. The status dot returns to
        // neutral ; an error path that landed here right after raising the
        // critical dot will see the status text rewritten too, so the dot
        // and the text stay in sync. Null-guard for the early-Closed path
        // where the XAML controls may already be torn down.
        if (ScreenCaptureToggleButton is not null)
        {
            ScreenCaptureToggleIcon.Glyph = ScreenCaptureGlyphStart;
            ScreenCaptureToggleLabel.Text = "Start";
            ScreenCaptureStatusText.Text = "Stopped";
            ScreenCaptureStatusDot.Fill = GetThemeBrush("SystemFillColorNeutralBrush");
        }
    }

    // Theme resource → Brush helper. Looking up a system fill colour brush
    // from code-behind requires going through Application.Current.Resources
    // (the ThemeResource markup extension only resolves in XAML). Returns
    // the resolved Brush ; the cast is safe because the SystemFillColor*
    // entries in the WinUI theme dictionary are SolidColorBrush.
    private static Brush GetThemeBrush(string key)
        => (Brush)Application.Current.Resources[key];

    private void OnScreenCaptureStopped()
    {
        // Fires on the capture service's worker thread when the loop
        // exits unexpectedly (sustained ACCESS_LOST recreate failure —
        // display disconnected, signed-out, etc.). Marshal back to the
        // UI thread to update the button + status, then run the same
        // teardown as a user-driven Stop.
        DispatcherQueue.TryEnqueue(() => StopScreenCaptureIfRunning());
    }

    private void OnScreenCaptureFpsTick(object? sender, object e)
    {
        if (_screenCapture is not { IsRunning: true }) return;

        long currentFrames = _screenCapture.FrameCount;
        long currentTimestamp = Stopwatch.GetTimestamp();
        long deltaFrames = currentFrames - _screenCaptureLastSampledFrames;
        long deltaMs = (currentTimestamp - _screenCaptureLastSampleTimestamp) * 1000 / Stopwatch.Frequency;

        double fps = deltaMs > 0 ? deltaFrames * 1000.0 / deltaMs : 0.0;

        ScreenCaptureFramesText.Text = currentFrames.ToString();
        ScreenCaptureFpsText.Text = $"{fps:F1}";

        _screenCaptureLastSampledFrames = currentFrames;
        _screenCaptureLastSampleTimestamp = currentTimestamp;
    }

    // ── Ambient lighting — Hue REST handlers (J2) ──────────────────────
    //
    // All three operations are async and run on the UI thread until
    // the first await. The bridge client is created lazily on first
    // Pair and torn down on window Close ; Discover doesn't need it
    // (cloud lookup is a static method, no bridge state involved).
    //
    // Cancellation : the pair loop runs up to 30 s ; if the user
    // closes the window mid-pair we cancel via _huePairCts so the
    // HttpClient unblocks cleanly. The buttons disable themselves
    // while an op is in flight (Discover is short enough that we
    // don't bother, Pair has a visible 30 s deadline so the disabled
    // state matters).

    private async void OnHueDiscoverClick(object sender, RoutedEventArgs e)
    {
        // Disable the button during the lookup so the user can't
        // queue multiple cloud calls — discovery is short (≤ 10 s
        // timeout in HueDiscovery) so this stays unobtrusive.
        HueDiscoverButton.IsEnabled = false;
        try
        {
            var bridges = await HuePairingService.Instance
                .DiscoverAsync()
                .ConfigureAwait(true);
            if (bridges.Count == 0)
            {
                // Cloud returned empty — log + leave the textbox alone
                // so the user can enter the IP manually. No status
                // dot change ; the absence of autofill is the signal.
                return;
            }

            // Autofill with the first bridge ; the user can still
            // overwrite if they have several bridges and want a
            // specific one. The remaining bridges are visible in
            // LogWindow (HueDiscovery emits a Verbose line per
            // bridge found).
            HueBridgeIpTextBox.Text = bridges[0].InternalIpAddress;
        }
        finally
        {
            HueDiscoverButton.IsEnabled = true;
        }
    }

    private async void OnHuePairClick(object sender, RoutedEventArgs e)
    {
        if (_hueIsPairing) return;

        var ip = HueBridgeIpTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            HuePairStatusText.Text = "Bridge IP required";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCautionBrush");
            return;
        }

        // Cancel any local pair already running (paranoid — the
        // _hueIsPairing guard above usually wins first).
        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = new CancellationTokenSource();
        _hueIsPairing = true;

        HuePairButton.IsEnabled = false;
        HuePairLabel.Text       = "Waiting for link button…";
        HuePairStatusText.Text  = "Waiting (30 s)";
        HuePairStatusDot.Fill   = GetThemeBrush("SystemFillColorCautionBrush");

        var target = new HueBridge(Id: "manual", InternalIpAddress: ip, Port: 443);
        try
        {
            // The service runs the link-button countdown, persists the
            // creds on success, disposes the previous bridge client,
            // and fires BridgeChanged — our OnHueBridgeChangedFromPlayground
            // handler also fires SyncHueUiFromService(), but we still
            // touch the row visuals here because we want pair-specific
            // copy ("Paired (xxx)") that the generic sync helper
            // doesn't surface.
            var creds = await HuePairingService.Instance
                .PairAsync(target, ct: _huePairCts.Token)
                .ConfigureAwait(true);

            HuePairStatusText.Text = $"Paired ({creds.UsernameHead})";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorSuccessBrush");
            HuePairLabel.Text      = "Re-pair";

            // Pairing succeeded → unlock the next row. List groups is
            // a separate explicit step (the user might want to inspect
            // the LogWindow output before populating the combo) ; the
            // group combo + colour buttons stay disabled until the
            // list arrives and the user picks one.
            HueListGroupsButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-pair, or user re-clicked. Reset the
            // visuals — the service didn't swap the bridge so the
            // previous pairing (if any) is still intact.
            HuePairStatusText.Text = "Cancelled";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorNeutralBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        catch (TimeoutException)
        {
            HuePairStatusText.Text = "Timed out — try again";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCriticalBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        catch (Exception ex)
        {
            HuePairStatusText.Text = $"Failed: {ex.Message}";
            HuePairStatusDot.Fill  = GetThemeBrush("SystemFillColorCriticalBrush");
            HuePairLabel.Text      = HuePairingService.Instance.IsPaired ? "Re-pair" : "Pair (press link button)";
        }
        finally
        {
            _hueIsPairing = false;
            HuePairButton.IsEnabled = true;
        }
    }

    private async void OnHueListGroupsClick(object sender, RoutedEventArgs e)
    {
        if (!HuePairingService.Instance.IsPaired) return;

        HueListGroupsButton.IsEnabled = false;
        try
        {
            _hueGroups = await HuePairingService.Instance
                .ListGroupsAsync()
                .ConfigureAwait(true);

            // Repopulate the combo. The suppress flag stops the
            // SelectionChanged handler from firing while we're
            // rebuilding the items collection ; without it we'd
            // briefly construct a HueRestLightOutput on the wrong
            // (auto-selected) group during the rebuild.
            _hueGroupComboSuppress = true;
            HueGroupComboBox.Items.Clear();
            foreach (var g in _hueGroups)
            {
                HueGroupComboBox.Items.Add(new ComboBoxItem
                {
                    Content = g.DisplayLabel,
                    Tag     = g,
                });
            }
            _hueGroupComboSuppress = false;

            HueGroupComboBox.IsEnabled = _hueGroups.Count > 0;
            if (_hueGroups.Count > 0)
            {
                // Prefer the persisted last-group when the list contains
                // it ; otherwise fall back to index 0. Either way,
                // SelectionChanged fires once the suppress flag is off
                // and wires the colour buttons + pipeline to the group.
                string? lastId = AmbientSettingsService.Instance.Current.HueLastGroupId;
                int preselectIndex = 0;
                if (!string.IsNullOrEmpty(lastId))
                {
                    for (int i = 0; i < _hueGroups.Count; i++)
                    {
                        if (_hueGroups[i].Id == lastId)
                        {
                            preselectIndex = i;
                            break;
                        }
                    }
                }
                HueGroupComboBox.SelectedIndex = preselectIndex;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Listing groups failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HueListGroupsButton.IsEnabled = true;
        }
    }

    private async void OnHueGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hueGroupComboSuppress) return;
        var bridge = HuePairingService.Instance.Bridge;
        if (bridge is not { IsPaired: true }) return;

        // Cancel any in-flight rotation against the previous group
        // and dispose the previous HueRestLightOutput before pointing
        // at the new one. The cancellation is best-effort — the
        // rotation handler catches OperationCanceledException.
        CancelHueRotationIfRunning();
        if (_hueLightOutput is not null)
        {
            await _hueLightOutput.DisposeAsync().ConfigureAwait(true);
            _hueLightOutput = null;
        }

        // Tear down zone assignments UI + state carried over from the
        // previous group — light ids belong to a different fixture set
        // and the user shouldn't see stale entries on the new group.
        // ResolveLightsAndBuildPlacementAsync will repopulate _placementLights
        // / _suggestedZones from the new group's data before
        // BuildLightZonesUi reads them.
        _placementLights = null;
        _suggestedZones  = null;
        ClearLightZonesUi();

        if (HueGroupComboBox.SelectedItem is not ComboBoxItem { Tag: HueGroup group })
        {
            SetHueColorButtonsEnabled(false);
            return;
        }

        _hueLightOutput = new HueRestLightOutput(bridge, group.Id);
        try
        {
            await _hueLightOutput.ConnectAsync().ConfigureAwait(true);
            SetHueColorButtonsEnabled(true);
            SetPipelineReady();

            // Persist the chosen group so the next session restores the
            // same pre-selection — completes the "no link-button on
            // restart" UX (pair credentials + group are both persisted
            // and read back at Playground open).
            AmbientSettingsService.Instance.Current.HueLastGroupId = group.Id;
            AmbientSettingsService.Instance.Save();

            // Resolve the individual lights inside the group so the user
            // can place them on the preview. Best-effort — a failure
            // here doesn't break the group-mode pipeline, it just means
            // the per-light placement UI stays empty. We pass the
            // HueGroup along because the fallback (when /groups/{id}
            // returns no lights) needs the group's display name to
            // match against the v2 entertainment_configuration set.
            await ResolveLightsAndBuildPlacementAsync(group).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Selecting group failed — {ex.GetType().Name}: {ex.Message}");
            SetHueColorButtonsEnabled(false);
            SetPipelineNotReady();
        }
    }

    // Pipeline UI state is a function of "do we have an ILightOutput
    // wired ?" — that's the prerequisite. The capture is owned by the
    // canonical App-side engine ; the Playground only mirrors its
    // state via AmbientSettings.Enabled.
    private void SetPipelineReady()
    {
        PipelineToggleButton.IsEnabled = true;
        SyncPipelineUiFromSettings();
    }

    private void SetPipelineNotReady()
    {
        PipelineToggleButton.IsEnabled = false;
        PipelineToggleIcon.Glyph = ScreenCaptureGlyphStart;
        PipelineToggleLabel.Text = "Turn Ambient Light on";
        PipelineStatusText.Text = "Pair a bridge and pick a group first";
        PipelineStatusDot.Fill = GetThemeBrush("SystemFillColorNeutralBrush");
    }

    private async void OnHueTestColorClick(object sender, RoutedEventArgs e)
    {
        if (_hueLightOutput is null) return;
        if (sender is not Button { Tag: string tag }) return;

        var color = tag switch
        {
            "red"   => LightColor.Red,
            "green" => LightColor.Green,
            "blue"  => LightColor.Blue,
            "white" => LightColor.White,
            "off"   => LightColor.Black,
            _       => LightColor.Black,
        };

        try
        {
            await _hueLightOutput.SetColorAsync(color).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Push colour failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnHueTestRotationClick(object sender, RoutedEventArgs e)
    {
        if (_hueLightOutput is null) return;

        // Cancel any prior rotation so a second click starts fresh
        // instead of stacking two interleaved loops.
        CancelHueRotationIfRunning();
        _hueRotationCts = new CancellationTokenSource();
        var ct = _hueRotationCts.Token;

        HueTestRotationButton.IsEnabled = false;
        try
        {
            // R → G → B → W → Off with a 1 s pause between pushes.
            // 1 s is long enough to see the lamp settle but short
            // enough that the sequence stays under 5 s — useful for
            // a quick sanity check on the bridge link latency.
            LightColor[] sequence =
            [
                LightColor.Red,
                LightColor.Green,
                LightColor.Blue,
                LightColor.White,
                LightColor.Black,
            ];

            foreach (var color in sequence)
            {
                await _hueLightOutput.SetColorAsync(color, ct).ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user navigates away mid-sequence or
            // re-clicks ; nothing to surface.
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Test rotation failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HueTestRotationButton.IsEnabled = true;
        }
    }

    private void OnPipelineToggleClick(object sender, RoutedEventArgs e)
    {
        // The canonical Ambient engine lives in App now and owns its
        // own capture, sampler, bridge client and light output. The
        // Playground Pipeline button is just a shortcut that flips
        // AmbientSettings.Enabled — App's observer takes it from
        // there. The same flip is exposed by the tray menu and by
        // the AmbientPage master toggle ; all three surfaces drive
        // the same state. UI feedback (button glyph, status text)
        // syncs through the AmbientSettingsService.Changed event in
        // SyncPipelineUiFromSettings.
        var s = AmbientSettingsService.Instance.Current;
        s.Enabled = !s.Enabled;
        AmbientSettingsService.Instance.Save();
    }

    // Reflects the master Enabled state on the Pipeline card. Called
    // on the UI thread at load and from the AmbientSettings.Changed
    // observer (marshalled via DispatcherQueue). Doesn't touch the
    // local _screenCapture / _frameSampler path — that remains driven
    // by the dedicated Screen capture toggle for isolated sampler
    // testing.
    private void SyncPipelineUiFromSettings()
    {
        var ambient = AmbientSettingsService.Instance.Current;
        if (PipelineToggleButton is null) return;
        bool enabled = ambient.Enabled;
        PipelineToggleIcon.Glyph  = enabled ? ScreenCaptureGlyphStop : ScreenCaptureGlyphStart;
        PipelineToggleLabel.Text  = enabled ? "Turn Ambient Light off" : "Turn Ambient Light on";
        PipelineStatusText.Text   = enabled ? "Running" : "Stopped";
        PipelineStatusDot.Fill    = GetThemeBrush(enabled
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorNeutralBrush");

        // Mirror the multi-light flag into the radio buttons so a flip
        // from the AmbientPage (when that lands) propagates here. The
        // SelectionChanged handler short-circuits on equal values so
        // this assignment doesn't re-fire Save.
        int desiredIndex = ambient.UseMultiLight ? 1 : 0;
        if (PipelineModeRadios is not null
            && PipelineModeRadios.SelectedIndex != desiredIndex)
        {
            PipelineModeRadios.SelectedIndex = desiredIndex;
        }
    }

    private void OnAmbientSettingsChangedFromPlayground()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncPipelineUiFromSettings();
            ApplyPipelineReadiness();
            ResyncPlaygroundAmbientTuning();
        });
    }

    // Single source of truth for "can the user flip the Pipeline
    // toggle right now ?". The button used to be gated on a local
    // ConnectAsync that only ran once the user clicked through Hue →
    // List groups → pick a group inside the Playground. With the
    // canonical App-side AmbientEngine, the persisted Hue pair state
    // plus a saved group id is enough — the engine builds its own
    // ConnectAsync on Start. Re-evaluated on Loaded, on
    // BridgeChanged, and on AmbientSettings.Changed.
    private void ApplyPipelineReadiness()
    {
        if (PipelineToggleButton is null) return;
        var s = AmbientSettingsService.Instance.Current;
        bool paired = HuePairingService.Instance.Bridge?.IsPaired == true;
        bool hasGroup = !string.IsNullOrEmpty(s.HueLastGroupId);
        if (paired && hasGroup) SetPipelineReady();
        else                    SetPipelineNotReady();
    }

    // AmbientEngine.StateChanged observer. No longer drives the
    // preview lifecycle — the timer runs unconditionally while the
    // Playground is open. Kept as a no-op for now so subscribe /
    // unsubscribe stays symmetrical, and so future surfaces (status
    // dot colour, button label tween) can hang here without another
    // round-trip on the engine.
    private void OnAmbientEngineStateChangedFromPlayground(AmbientEngineState state)
    {
        // Intentionally empty. See StartPreviewTimer at Loaded for
        // why we don't gate on state here anymore.
    }

    // Start the preview timer when the engine starts. If a local
    // Screen capture session is already running, the timer is already
    // ticking and the OnPreviewTimerTick path prefers the engine's
    // LatestSample anyway, so this is a no-op in that case.
    private void StartCanonicalPreviewIfNeeded()
    {
        var engine = AmbientEngine.Current;
        if (engine is null || !engine.IsRunning) return;

        // Lazily build the preview grid the first time we see a sample
        // from the engine — its sampler dims are owned by the engine,
        // not the Playground.
        var sample = engine.LatestSample;
        if (sample is not null && _previewCells is null)
        {
            BuildPreviewGrid(sample.Cols, sample.Rows);
        }

        StartPreviewTimer();
        UpdatePreviewViewboxVisibility();
    }

    // FrameArrived handler that hands the frame to the FrameSampler.
    // Runs on the capture service's worker thread ; FrameSampler.Process
    // is thread-safe internally. The frame's TexturePtr is borrowed for
    // the handler scope only — we must not retain it (the capture
    // service Releases the underlying COM ref after this returns).
    // Cadence is throttled at the capture loop level, so we don't gate
    // per-frame work here.
    private void OnSamplerFrameArrived(CapturedFrame frame)
    {
        _frameSampler?.Process(frame);
    }

    // Build the preview Grid : N × M Rectangle children, sized so the
    // whole grid fits comfortably inside the preview Border with the
    // source aspect ratio preserved. Cells are 16×16 dip by default ;
    // smaller if the grid is denser. Called from the UI thread on the
    // start path, after the sampler has reported its dimensions.
    private void BuildPreviewGrid(int cols, int rows)
    {
        AmbientPreviewGrid.RowDefinitions.Clear();
        AmbientPreviewGrid.ColumnDefinitions.Clear();
        AmbientPreviewGrid.Children.Clear();

        const double Gap = 1;

        for (int c = 0; c < cols; c++)
            AmbientPreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewCellSize) });
        for (int r = 0; r < rows; r++)
            AmbientPreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PreviewCellSize) });

        _previewCells = new Microsoft.UI.Xaml.Shapes.Rectangle[cols * rows];

        // One SolidColorBrush per cell — MANDATORY. Sharing a single brush
        // across cells and mutating its Color in the preview tick made every
        // cell snap to the last pixel of the grid (typically the bottom-right
        // taskbar — dark), which is what produced the "everything is dark
        // while the screen is white" symptom even though the FrameSampler
        // average (and the lamp push) were correct.
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Fill   = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Margin = new Thickness(Gap),
                };
                Grid.SetRow(rect, r);
                Grid.SetColumn(rect, c);
                AmbientPreviewGrid.Children.Add(rect);
                _previewCells[r * cols + c] = rect;
            }
        }

        // The Viewbox above scales the whole stage uniformly, so we
        // size the stage to the grid's natural dip footprint here.
        // Zone overlay rectangles get re-laid out so they keep
        // tracking the right border bands when the sampler reports
        // new grid dimensions.
        double stageWidth  = cols * PreviewCellSize;
        double stageHeight = rows * PreviewCellSize;
        AmbientPreviewStage.Width  = stageWidth;
        AmbientPreviewStage.Height = stageHeight;
        LightZonesOverlay.Width    = stageWidth;
        LightZonesOverlay.Height   = stageHeight;
        LayoutLightZoneRects(stageWidth, stageHeight);

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

    private void StopPreviewTimer()
    {
        _previewTimer?.Stop();
        if (_previewCells is not null && AmbientPreviewGrid is not null)
        {
            AmbientPreviewGrid.Children.Clear();
            AmbientPreviewGrid.RowDefinitions.Clear();
            AmbientPreviewGrid.ColumnDefinitions.Clear();
            _previewCells = null;
        }
        // Clear the emitted-colour swatch strip when the pipeline
        // stops — without an active tick the visuals would otherwise
        // freeze on the last seen colour, which reads as still-live
        // to the user.
        if (_swatchByLight.Count > 0)
        {
            EmittedSwatches.Children.Clear();
            _swatchByLight.Clear();
        }
        EmittedSwatchesPanel.Visibility = Visibility.Collapsed;
        // Keep the Viewbox visible if light markers are still around —
        // the user may want to keep placing while the pipeline is
        // stopped. UpdatePreviewViewboxVisibility flips to the empty
        // state only when nothing's left to show.
        UpdatePreviewViewboxVisibility();
    }

    // Centralised visibility flip for the preview stage. The Viewbox
    // is shown whenever there's content to display — either an active
    // preview grid (pipeline running) or resolved lights with their
    // zone overlay (a group is selected on a multi-light-capable
    // driver). Otherwise the empty-state stack panel takes over.
    private void UpdatePreviewViewboxVisibility()
    {
        bool hasCells = _previewCells is not null;
        bool hasZones = _placementLights is { Count: > 0 };
        bool show     = hasCells || hasZones;
        AmbientPreviewViewbox.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        AmbientPreviewEmptyState.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPreviewTimerTick(object? sender, object e)
    {
        // Prefer the canonical App-side AmbientEngine sample : it's
        // the live pipeline the user actually drives from the tray /
        // Settings / Playground toggles. Fall back to the Playground's
        // own _frameSampler only when the engine isn't running but
        // the user explicitly started the local Screen capture for
        // sampler-isolated testing.
        var sample = AmbientEngine.Current?.LatestSample ?? _frameSampler?.LatestSample;

        // Lazy-build the preview grid the first time we see a sample
        // from the engine (the local capture path builds it earlier
        // from its own sampler dims).
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

        // Refresh the swatch strip from the engine's "intent" map.
        // Pulled on the same 200 ms cadence as the preview grid so
        // they stay visually in sync ; we don't subscribe to
        // EmittedColorsChanged to avoid a per-tick cross-thread
        // marshal that would buy nothing at this refresh rate.
        UpdateEmittedSwatches();
    }

    // Reads AmbientEngine.Current.SnapshotEmittedColors() and either
    // mutates the existing swatch brushes (steady state) or rebuilds
    // the strip when the set of light ids has shifted (group ↔ multi
    // switch, fixture (re-)pair). Visibility of the parent Border is
    // driven by the presence of at least one entry — the panel
    // disappears when the engine isn't running.
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
            EmittedSwatchesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var snapshot = engine.SnapshotEmittedColors();
        if (snapshot.Count == 0)
        {
            EmittedSwatchesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Detect set change : different ids or different count → wipe
        // and rebuild. Otherwise mutate brushes in place.
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
                var swatch = BuildSwatch(id, color);
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

        EmittedSwatchesPanel.Visibility = Visibility.Visible;
    }

    // Builds one swatch tile : a fixed-size coloured rectangle with
    // the light id label on top of it. Uses theme resources for the
    // border / corner so the tile follows light/dark + accent. The
    // brush is held on a dedicated field so the tick can mutate
    // Color without re-issuing a Fill (cf. the per-cell brush
    // pattern proven on the preview grid).
    private (Microsoft.UI.Xaml.Controls.Border Container, SolidColorBrush Fill, TextBlock Label) BuildSwatch(string id, LightColor color)
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
        var label = new TextBlock
        {
            Text = id,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(swatchRect);
        stack.Children.Add(label);
        var container = new Microsoft.UI.Xaml.Controls.Border
        {
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            Child = stack,
        };
        return (container, fill, label);
    }

    // ── Light zones (J4) ────────────────────────────────────────────────
    //
    // Lights → zone-assignment wiring. Called from
    // OnHueGroupSelectionChanged after a successful ConnectAsync : the
    // connected output is queried for its fixture list, one ComboBox-
    // bearing row is appended to LightZonesPanel per light, and the
    // four border-zone rectangles in LightZonesOverlay are sized to
    // match AmbientEngine.LateralBorderDepth (left/right) and
    // VerticalBorderDepth (top/bottom) × stage dims.

    // Display label for each LightZone enum value, in the order they
    // appear in the ComboBox. Kept in code-behind so the rest of the
    // module stays free of XAML strings (no x:Uid involvement yet —
    // the surface is dev-only Playground in V0, localisation lands in
    // a later milestone).
    // Zone options exposed by every per-light DropDownButton +
    // MenuFlyout pair in the Light zones card. The set is static :
    // five values, fixed shape, never changes at runtime. Used by
    // BuildLightZonesUi to populate each menu and by LabelForZone to
    // resolve the button caption for the current selection. We tried
    // ComboBox first — both with Items.Add and with ItemsSource — and
    // both surfaced a "first click doesn't open / select" bug that
    // tracks back to WinUI 3 ComboBox popup measure/focus quirks in
    // code-behind construction. DropDownButton + RadioMenuFlyoutItem
    // is deterministic : Click opens the flyout, item Click selects,
    // no popup measure dependency on the first interaction.
    private sealed record ZoneOption(LightZone Zone, string Label);

    private static readonly ZoneOption[] _zoneOptions =
    [
        new ZoneOption(LightZone.None,   "None"),
        new ZoneOption(LightZone.Top,    "Top"),
        new ZoneOption(LightZone.Bottom, "Bottom"),
        new ZoneOption(LightZone.Left,   "Left"),
        new ZoneOption(LightZone.Right,  "Right"),
    ];

    private async Task ResolveLightsAndBuildPlacementAsync(HueGroup group)
    {
        // Group-mode-only drivers don't expose IMultiLightOutput ; we
        // just leave the zones UI empty (the pipeline still works in
        // group mode).
        if (_hueLightOutput is not IMultiLightOutput multi)
        {
            _placementLights = null;
            _suggestedZones  = null;
            ClearLightZonesUi();
            return;
        }

        List<LightDescriptor> lights;
        try
        {
            var resolved = await multi.ListLightsAsync().ConfigureAwait(true);
            lights = new List<LightDescriptor>(resolved);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Listing lights failed — {ex.GetType().Name}: {ex.Message}");
            _placementLights = null;
            BuildLightZonesUi(); // Show card with empty state.
            return;
        }

        LogService.Instance.Verbose(LogSource.Ambient,
            $"resolve lights | group_id={group.Id} | group_name={group.Name} | from_group={lights.Count}");

        // Always try to resolve a matching entertainment area, even
        // when /groups returned lights — its placements drive the zone
        // suggestions. When /groups returned nothing AND the area
        // knows about lights, the area also becomes the fallback
        // lights source (typical for pure Entertainment groups whose
        // fixtures live only on the v2 surface).
        var matchedArea = await FindMatchingEntertainmentAreaAsync(group, lights).ConfigureAwait(true);

        if (lights.Count == 0 && matchedArea is { LightPlacements.Count: > 0 })
        {
            LogService.Instance.Info(LogSource.Hue,
                $"Using entertainment area '{matchedArea.Name}' as the lights source ({matchedArea.LightPlacements.Count} lights)");
            foreach (var p in matchedArea.LightPlacements)
            {
                lights.Add(new LightDescriptor(p.LightId, p.Name, IsReachable: true));
            }
        }

        _suggestedZones = matchedArea is not null
            ? BuildSuggestionsFromArea(matchedArea, lights)
            : null;

        _placementLights = lights;
        BuildLightZonesUi();
    }

    // Picks the entertainment area on the bridge that best matches the
    // selected group. Preference order :
    //   1. Exact case-insensitive name match (the Hue mobile app
    //      typically names the v1 group and its v2 area the same).
    //   2. Greatest overlap of light ids — fallback when names diverge
    //      (e.g. the user renamed one of the two).
    // Returns null when no entertainment areas exist on the bridge or
    // none can be associated with the selected group.
    private async Task<HueEntertainmentArea?> FindMatchingEntertainmentAreaAsync(
        HueGroup group, IReadOnlyList<LightDescriptor> lights)
    {
        if (!HuePairingService.Instance.IsPaired) return null;

        IReadOnlyList<HueEntertainmentArea> areas;
        try
        {
            areas = await HuePairingService.Instance
                .ListEntertainmentConfigurationsAsync()
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose(LogSource.Hue,
                $"List entertainment configs failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (areas.Count == 0)
        {
            LogService.Instance.Verbose(LogSource.Ambient,
                "match ent area | result=no_areas");
            return null;
        }

        // 1. Name match (case-insensitive).
        foreach (var a in areas)
        {
            if (string.Equals(a.Name, group.Name, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Instance.Verbose(LogSource.Ambient,
                    $"match ent area | result=name | ent_id={a.Id} | name={a.Name}");
                return a;
            }
        }

        // 2. Light-id overlap. Skip when /groups returned nothing —
        // overlap is necessarily 0 in that case.
        if (lights.Count > 0)
        {
            var idSet = new HashSet<string>(lights.Count);
            foreach (var l in lights) idSet.Add(l.Id);
            HueEntertainmentArea? best = null;
            int bestOverlap = 0;
            foreach (var a in areas)
            {
                int overlap = 0;
                foreach (var p in a.LightPlacements)
                    if (idSet.Contains(p.LightId)) overlap++;
                if (overlap > bestOverlap) { best = a; bestOverlap = overlap; }
            }
            if (best is not null)
            {
                LogService.Instance.Verbose(LogSource.Ambient,
                    $"match ent area | result=overlap | ent_id={best.Id} | name={best.Name} | overlap={bestOverlap}");
                return best;
            }
        }

        LogService.Instance.Verbose(LogSource.Ambient,
            "match ent area | result=no_match");
        return null;
    }

    private Dictionary<string, LightZone> BuildSuggestionsFromArea(
        HueEntertainmentArea area, IReadOnlyList<LightDescriptor> lights)
    {
        var lightIdSet = new HashSet<string>(lights.Count);
        foreach (var l in lights) lightIdSet.Add(l.Id);

        var suggestions = new Dictionary<string, LightZone>();
        foreach (var p in area.LightPlacements)
        {
            if (!lightIdSet.Contains(p.LightId)) continue;
            var zone = LightZoneSuggester.Suggest(p);
            suggestions[p.LightId] = zone;
            LogService.Instance.Verbose(LogSource.Ambient,
                $"zone suggest | id={p.LightId} | zone={zone} | from=ent_config | ent_name={area.Name} | xyz={p.X:F2},{p.Y:F2},{p.Z:F2}");
        }
        return suggestions;
    }

    private void BuildLightZonesUi()
    {
        ClearLightZonesUi();

        // Show the card the moment a group has been selected even if
        // the lights resolution came back empty — the empty state is
        // valuable feedback ("we see the group, it has no addressable
        // lights"). Hiding the card entirely looked like a silent
        // failure in earlier iterations.
        LightZonesCard.Visibility = Visibility.Visible;

        // Stage dims may not be set yet if no pipeline run has
        // happened ; fall back to the typical 30×17 footprint so the
        // overlay rectangles have somewhere to land in the meantime.
        // BuildPreviewGrid will overwrite the dims + call
        // LayoutLightZoneRects on the first pipeline start.
        double stageWidth  = AmbientPreviewStage.Width  > 0 ? AmbientPreviewStage.Width  : 30 * PreviewCellSize;
        double stageHeight = AmbientPreviewStage.Height > 0 ? AmbientPreviewStage.Height : 17 * PreviewCellSize;
        AmbientPreviewStage.Width  = stageWidth;
        AmbientPreviewStage.Height = stageHeight;
        LightZonesOverlay.Width    = stageWidth;
        LightZonesOverlay.Height   = stageHeight;
        LayoutLightZoneRects(stageWidth, stageHeight);

        if (_placementLights is null || _placementLights.Count == 0)
        {
            LightZonesEmptyState.Visibility = Visibility.Visible;
            UpdateZoneOverlayHighlight();
            UpdatePreviewViewboxVisibility();
            return;
        }
        LightZonesEmptyState.Visibility = Visibility.Collapsed;

        var settings = AmbientSettingsService.Instance.Current;

        // Build one row per light : "Light name" TextBlock + [Identify]
        // button + zone ComboBox. The combo's Tag carries the light id
        // so the SelectionChanged handler can route the persisted update
        // back to AmbientSettings.LightZones ; the Identify button's
        // Tag does the same for the flash routing.
        //
        // Resolution order for the initial combo value :
        //   1. The user's persisted choice in AmbientSettings.LightZones
        //      (any non-None entry wins — the user spoke).
        //   2. Otherwise, the suggestion derived from the Hue
        //      entertainment area (auto-pre-fill ; persisted on first
        //      build so the next session sees the same value).
        //   3. Otherwise, None.
        bool suggestionWritten = false;
        foreach (var light in _placementLights)
        {
            LightZone persistedZone = settings.LightZones.TryGetValue(light.Id, out var z)
                ? z
                : LightZone.None;

            LightZone suggestedZone = LightZone.None;
            if (_suggestedZones is not null && _suggestedZones.TryGetValue(light.Id, out var s))
                suggestedZone = s;

            LightZone effectiveZone;
            if (persistedZone != LightZone.None)
            {
                effectiveZone = persistedZone;
            }
            else if (suggestedZone != LightZone.None)
            {
                effectiveZone = suggestedZone;
                settings.LightZones[light.Id] = suggestedZone;
                suggestionWritten = true;
            }
            else
            {
                effectiveZone = LightZone.None;
            }

            // Row layout : one horizontal StackPanel per lamp, side by
            // side. The lamp name on the left anchors the row ; the
            // Identify button gives the user a way to confirm the
            // physical fixture ; the zone DropDownButton on the right
            // is the only thing the engine reads.
            //
            // We deliberately don't label the button with a "Zone"
            // header — the current value reads unambiguously on its
            // own ("Top" / "Bottom" / "Left" / "Right" / "None"), and
            // the lamp name to its left is enough anchor to know what
            // is being mapped.
            //
            // Brightness control omitted on purpose for V0 : "every
            // lamp at 100 %, balance later." Per-light brightness
            // still persists in AmbientSettings.LightBrightness and
            // the engine still honours an existing entry, but no UI
            // surfaces it yet — the control will return when its UX
            // is rethought.
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var nameLabel = new TextBlock
            {
                Text   = light.IsReachable ? light.Name : $"{light.Name} (offline)",
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // Identify button — fires alert=lselect to flash the lamp
            // for ~3 s so the user can spot which physical fixture
            // this row controls. Disabled for the flash duration so a
            // second click can't queue an overlapping flash.
            var identifyButton = new Button
            {
                Tag = light.Id,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        // Lightbulb glyph (Segoe Fluent Icons E7E8) —
                        // reads as "tell me which lamp this is".
                        new FontIcon { Glyph = "", FontSize = 14 },
                        new TextBlock { Text = "Identify" },
                    },
                },
                IsEnabled = light.IsReachable,
            };
            identifyButton.Click += OnIdentifyLightClick;

            // Zone picker built as DropDownButton + MenuFlyout instead
            // of ComboBox. The ComboBox path had a persistent "first
            // click doesn't open the dropdown, then 2-3 clicks before
            // selection registers" bug — WinUI 3 ComboBox has known
            // flakiness when created in code-behind, especially around
            // popup measure / focus handoff on the first interaction.
            // DropDownButton + MenuFlyout sidesteps all of it : the
            // button opens the flyout on Click (no measure-time
            // dependency), the flyout items are MenuFlyoutItem-style
            // (focus-friendly, click-to-select), and the visual state
            // machine is decoupled from popup positioning. Same shape
            // as File Explorer's "View" button or Photos' edit modes.
            //
            // Each RadioMenuFlyoutItem carries a ZoneMenuTag in its
            // Tag : the lightId + the zone + the button to relabel on
            // selection. OnZoneMenuItemClick reads from there ; no
            // closure capture, no dict to clean up.
            // DropDownButton with the label left-aligned (default is
            // centered — looks "Top" floating mid-button which was
            // hard to read at a glance). MinWidth=130 fits the longest
            // label ("Bottom") comfortably. MenuFlyoutItem (not
            // RadioMenuFlyoutItem) so the items don't reserve a radio-
            // dot column on the left ; the current selection is
            // already obvious from the button's own label, the
            // pastille was visual noise.
            var zoneButton = new DropDownButton
            {
                Content                   = LabelForZone(effectiveZone),
                MinWidth                  = 130,
                Tag                       = light.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var zoneFlyout = new MenuFlyout();
            foreach (var opt in _zoneOptions)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = opt.Label,
                    Tag  = new ZoneMenuTag(light.Id, light.Name, opt.Zone, zoneButton),
                };
                menuItem.Click += OnZoneMenuItemClick;
                zoneFlyout.Items.Add(menuItem);
            }
            zoneButton.Flyout = zoneFlyout;

            row.Children.Add(nameLabel);
            row.Children.Add(identifyButton);
            row.Children.Add(zoneButton);
            LightZonesPanel.Children.Add(row);
        }

        if (suggestionWritten) AmbientSettingsService.Instance.Save();

        LightZonesCard.Visibility = Visibility.Visible;
        UpdateZoneOverlayHighlight();
        UpdatePreviewViewboxVisibility();
    }

    private async void OnIdentifyLightClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Tag is not string lightId) return;
        if (_hueLightOutput is not IMultiLightOutput multi) return;

        // Swap the button content : the Lightbulb glyph becomes a
        // small ProgressRing so the user has unambiguous "yes, the
        // flash is running right now" feedback during the 3 s window.
        // The original content is restored in the finally block.
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content   = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new ProgressRing { Width = 14, Height = 14, IsActive = true },
                new TextBlock     { Text = "Flashing" },
            },
        };

        try
        {
            await multi.IdentifyLightAsync(lightId).ConfigureAwait(true);
            await Task.Delay(IdentifyFlashDuration).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Hue,
                $"Identify failed — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Best-effort cut of the bridge-side flash so the lamp
            // doesn't keep breathing for the remaining ~12 s of the
            // lselect window. Failure here is silent — the lamp will
            // auto-restore after the bridge's own timeout anyway.
            try { await multi.StopIdentifyAsync(lightId).ConfigureAwait(true); }
            catch { /* best effort */ }

            button.Content   = originalContent;
            button.IsEnabled = true;
        }
    }

    // DOM-only reset for the Light zones card : rip the per-light
    // rows, hide the card and its empty state. Does NOT touch the
    // _placementLights / _suggestedZones state fields — those are
    // owned by the resolver and reset explicitly by the caller when
    // appropriate (e.g. when the user switches group, the resolver
    // overwrites them with a fresh result). Clearing them here was
    // the root cause of the "no lights returned" bug : BuildLightZonesUi
    // calls Clear at start to rebuild from scratch, which would nuke
    // the freshly-resolved _placementLights before the rebuild loop
    // had a chance to use them.
    private void ClearLightZonesUi()
    {
        // Children.Clear() releases every row and the visual subtree
        // hanging off it — DropDownButton, MenuFlyout, RadioMenuFlyoutItems,
        // Identify button. Their event handlers (page methods or lambdas
        // captured in the row) become unreachable once the visuals are
        // detached and the GC reclaims them. No explicit unhook needed.
        LightZonesPanel.Children.Clear();
        LightZonesCard.Visibility       = Visibility.Collapsed;
        LightZonesEmptyState.Visibility = Visibility.Collapsed;
        ZoneTopRect.Visibility    = Visibility.Collapsed;
        ZoneBottomRect.Visibility = Visibility.Collapsed;
        ZoneLeftRect.Visibility   = Visibility.Collapsed;
        ZoneRightRect.Visibility  = Visibility.Collapsed;
        UpdatePreviewViewboxVisibility();
    }

    // Computes the dip-space rectangle each zone covers on the preview
    // stage and positions the matching overlay Rectangle accordingly.
    // The geometry mirrors the engine's SampleZone bounds : Top covers
    // the upper VerticalBorderDepth band, Bottom the lower, Left and
    // Right the matching lateral strips. We use the same depth
    // constants exposed by AmbientEngine so the user-visible band
    // matches what the engine actually samples — Top/Bottom share
    // the vertical depth (40 % default), Left/Right the lateral
    // depth (33 % default).
    private void LayoutLightZoneRects(double stageWidth, double stageHeight)
    {
        double bandH = stageHeight * AmbientEngine.VerticalBorderDepth;
        double bandV = stageWidth  * AmbientEngine.LateralBorderDepth;

        Canvas.SetLeft(ZoneTopRect, 0);
        Canvas.SetTop (ZoneTopRect, 0);
        ZoneTopRect.Width  = stageWidth;
        ZoneTopRect.Height = bandH;

        Canvas.SetLeft(ZoneBottomRect, 0);
        Canvas.SetTop (ZoneBottomRect, stageHeight - bandH);
        ZoneBottomRect.Width  = stageWidth;
        ZoneBottomRect.Height = bandH;

        Canvas.SetLeft(ZoneLeftRect, 0);
        Canvas.SetTop (ZoneLeftRect, 0);
        ZoneLeftRect.Width  = bandV;
        ZoneLeftRect.Height = stageHeight;

        Canvas.SetLeft(ZoneRightRect, stageWidth - bandV);
        Canvas.SetTop (ZoneRightRect, 0);
        ZoneRightRect.Width  = bandV;
        ZoneRightRect.Height = stageHeight;

        UpdateZoneOverlayHighlight();
    }

    // Shows a zone's overlay rectangle iff at least one light is
    // assigned to it. Pure visual cue — the engine samples the four
    // zones every tick regardless of whether anything reads the
    // result, but rendering the bands the user doesn't use would
    // clutter the preview.
    private void UpdateZoneOverlayHighlight()
    {
        bool hasTop    = false;
        bool hasBottom = false;
        bool hasLeft   = false;
        bool hasRight  = false;
        var settings = AmbientSettingsService.Instance.Current;
        if (_placementLights is not null)
        {
            foreach (var light in _placementLights)
            {
                if (!settings.LightZones.TryGetValue(light.Id, out var z)) continue;
                switch (z)
                {
                    case LightZone.Top:    hasTop    = true; break;
                    case LightZone.Bottom: hasBottom = true; break;
                    case LightZone.Left:   hasLeft   = true; break;
                    case LightZone.Right:  hasRight  = true; break;
                }
            }
        }
        ZoneTopRect.Visibility    = hasTop    ? Visibility.Visible : Visibility.Collapsed;
        ZoneBottomRect.Visibility = hasBottom ? Visibility.Visible : Visibility.Collapsed;
        ZoneLeftRect.Visibility   = hasLeft   ? Visibility.Visible : Visibility.Collapsed;
        ZoneRightRect.Visibility  = hasRight  ? Visibility.Visible : Visibility.Collapsed;
    }

    // Carrier for everything OnZoneMenuItemClick needs : the lamp id
    // (to route the assignment), the lamp display name (for the Info
    // Capital log line), the zone the menu item represents, and the
    // DropDownButton that should be re-labelled on selection. Sits on
    // the MenuFlyoutItem.Tag so the handler is a plain delegate
    // without per-row closure capture.
    private sealed record ZoneMenuTag(string LightId, string LightName, LightZone Zone, DropDownButton Button);

    private static string LabelForZone(LightZone zone)
    {
        for (int i = 0; i < _zoneOptions.Length; i++)
            if (_zoneOptions[i].Zone == zone) return _zoneOptions[i].Label;
        return _zoneOptions[0].Label; // Fallback to "None".
    }

    private void OnZoneMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        if (item.Tag is not ZoneMenuTag tag) return;

        // Update the button label to reflect the new selection — the
        // DropDownButton's Content is just text, no automatic binding
        // to the active flyout item.
        tag.Button.Content = item.Text;

        var settings = AmbientSettingsService.Instance.Current;
        if (tag.Zone == LightZone.None)
        {
            // Treat None as "unmapped" : remove the entry so the JSON
            // file stays tidy (no growing list of None entries for
            // every light that's ever been picked then unselected).
            settings.LightZones.Remove(tag.LightId);
        }
        else
        {
            settings.LightZones[tag.LightId] = tag.Zone;
        }
        AmbientSettingsService.Instance.Save();

        // Pair Info Capital + Verbose mirror, per logging doctrine
        // (cf. reference--logging-inventory--1.0.md §"Filtre runtime")
        // — Info is a semantic sentence for human readers in Activity
        // (no opaque IDs), Verbose carries the technical k=v with the
        // light id for grep / diag. The Verbose mirror is NOT gated by
        // LogAmbientCaptureActivity : that toggle only silences the
        // engine's per-tick chatter inside the push loop, never user
        // actions, even when they happen while the pipeline runs.
        string zoneSummary = tag.Zone == LightZone.None
            ? $"Zone cleared on {tag.LightName}"
            : $"Zone {tag.Zone} assigned to {tag.LightName}";
        LogService.Instance.Info(LogSource.Ambient, zoneSummary);
        LogService.Instance.Verbose(LogSource.Ambient,
            $"zone assign | id={tag.LightId} | zone={tag.Zone}");

        UpdateZoneOverlayHighlight();
    }

    private void OnPipelineModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not RadioButtons radios) return;
        // Index 0 = "group" (UseMultiLight=false), 1 = "per-zone"
        // (UseMultiLight=true). Anything else (no selection) ignored.
        bool useMulti = radios.SelectedIndex switch
        {
            0 => false,
            1 => true,
            _ => AmbientSettingsService.Instance.Current.UseMultiLight,
        };
        var settings = AmbientSettingsService.Instance.Current;
        if (settings.UseMultiLight == useMulti) return;
        settings.UseMultiLight = useMulti;
        AmbientSettingsService.Instance.Save();

        // Pair Info Capital + Verbose mirror, same doctrine as zone
        // assign above. Info reads as a human sentence in Activity ;
        // Verbose carries the property name / value for grep.
        string modeLabel = useMulti ? "per-zone" : "group";
        LogService.Instance.Info(LogSource.Ambient,
            $"Pipeline mode set to {modeLabel}");
        LogService.Instance.Verbose(LogSource.Ambient,
            $"settings update | key=UseMultiLight | value={useMulti}");
    }

    private void CancelHueRotationIfRunning()
    {
        try { _hueRotationCts?.Cancel(); } catch { /* best effort */ }
        _hueRotationCts?.Dispose();
        _hueRotationCts = null;
    }

    // StackPanel doesn't expose IsEnabled (it's a Panel, IsEnabled
    // lives on Control), so we toggle each child Button individually.
    // Iterating Children rather than naming each x:Name keeps the
    // helper agnostic to button additions / removals — adding a new
    // colour swatch in XAML is a XAML-only change.
    private void SetHueColorButtonsEnabled(bool enabled)
    {
        foreach (var child in HueColorButtonsPanel.Children)
        {
            if (child is Control c) c.IsEnabled = enabled;
        }
    }

    private void TeardownHueIfActive()
    {
        // The canonical bridge is owned by HuePairingService — both the
        // App-side AmbientEngine and the Settings AmbientPage point at
        // the same instance. Tearing it down here would kill ambient
        // lighting for the whole process every time the user closes
        // the Playground, which is wrong. Forget is an explicit user
        // action (Settings → Forget bridge), not a side-effect of
        // closing a debug window.

        // Sampler / preview teardown that mirrors the pipeline stop
        // path (capture-stop-symmetry handled by the pipeline click
        // handler ; here we only release the sampler + preview UI in
        // case a Hue teardown happens while the pipeline is still up).
        StopPreviewTimer();
        if (_screenCapture is not null)
            _screenCapture.FrameArrived -= OnSamplerFrameArrived;
        if (_frameSampler is not null)
        {
            _frameSampler.DisposeAsync().AsTask();
            _frameSampler = null;
        }
        if (_pipelineStartedCapture)
        {
            try { StopScreenCaptureIfRunning(); } catch { /* best effort */ }
            _pipelineStartedCapture = false;
        }

        // Cancel any pending pair loop first ; PairAsync will exit
        // with OperationCanceledException so the catch in
        // OnHuePairClick can update the visuals.
        try { _huePairCts?.Cancel(); } catch { /* best effort */ }
        _huePairCts?.Dispose();
        _huePairCts = null;

        // Cancel any in-flight colour rotation as well — same
        // best-effort pattern, the rotation handler catches the
        // OperationCanceledException quietly.
        CancelHueRotationIfRunning();

        // The HueRestLightOutput is a thin wrapper around the bridge
        // client ; disposing it doesn't close the HttpClient (the
        // bridge client is owned by HuePairingService). DisposeAsync
        // is fire-and-forget here — we can't await in a synchronous
        // teardown and the dispose work is non-blocking anyway.
        _hueLightOutput?.DisposeAsync().AsTask();
        _hueLightOutput = null;
        _hueGroups = [];

        // Reset the group-row UI so a follow-up Pair attempt starts
        // from a clean state. The Pair row itself is left alone — the
        // service still holds the paired bridge and SyncHueUiFromService
        // would re-populate the "Re-pair" / Paired (xxx) state on the
        // next open.
        if (HueListGroupsButton is not null)
        {
            _hueGroupComboSuppress = true;
            HueGroupComboBox.Items.Clear();
            HueGroupComboBox.IsEnabled = false;
            _hueGroupComboSuppress = false;
            SetHueColorButtonsEnabled(false);
        }

        SetPipelineNotReady();
    }

    // ── NavigationView tooltip i18n override ────────────────────────────────
    //
    // The NavigationView pane-toggle button (hamburger / "≡") inherits a
    // tooltip from the OS resource bundle — "Open navigation" on EN,
    // "Ouvrir navigation" on FR, etc. Deckle's UI is locked to English so
    // we override that string explicitly. The toggle button is a template
    // part named "TogglePaneButton" ; we walk the visual tree after the
    // NavigationView has applied its template (i.e. once it's loaded) and
    // set ToolTipService.ToolTip + AutomationProperties.Name on the
    // resolved button so both sighted and assistive surfaces match.
    //
    // No-op if the part can't be resolved (e.g. WinUI changes the
    // template part name in a future update) — better silently mismatched
    // than crashing on a cosmetic concern.
    private static void OverrideNavPaneToggleTooltip(NavigationView nav, string tooltip)
    {
        var toggle = FindVisualDescendantByName<Button>(nav, "TogglePaneButton");
        if (toggle is null) return;
        ToolTipService.SetToolTip(toggle, tooltip);
        AutomationProperties.SetName(toggle, tooltip);
    }

    private static T? FindVisualDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            var found = FindVisualDescendantByName<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    // ── Ambient HDR tuning sandbox (live) ───────────────────────────
    //
    // Mirrors Settings → Ambient lighting → HDR tuning : same four
    // knobs (γ / Exposure / Saturation / Min brightness), same
    // AmbientSettings keys, same engine hot-reload on the next push.
    // Surfaced inline in the Playground so the user can tune while
    // the preview grid runs next to them ; values persist exactly
    // as if set from Settings.
    //
    // _ambientTuningLoading mirrors AmbientPage's _loading flag : a
    // re-fire suppressor that lets ResyncPlaygroundAmbientTuning
    // populate slider Values without triggering Save loops back into
    // the settings store. SettingsChanged is observed so that a flip
    // from Settings (or another surface) propagates to the sandbox
    // sliders too — the user can have both windows open and stay in
    // sync.

    // Re-fire suppressor when ResyncPlaygroundAmbientTuning populates
    // the slider values — the ValueChanged handlers would otherwise
    // Save back into the same settings keys they just read.
    private bool _ambientTuningLoading = true;

    private void InitPlaygroundAmbientTuning()
    {
        // Set non-zero-Minimum slider ranges here rather than in XAML :
        // the WinUI 3 Slider parser rejects every attribute order
        // when the default Value (0) sits below the declared Minimum,
        // crashing the page with a XamlParseException at runtime.
        // Order matters here too — Maximum first, then Value, then
        // Minimum — so the RangeBase invariant holds at each step.

        // Curve param. Max 5.0 covers the SCurve steepness range ;
        // Gamma stays below 3 in practice, the rest of the slider is
        // just unused real estate when Gamma is the active type.
        PlaygroundGammaSlider.Maximum = 5.0;
        PlaygroundGammaSlider.Value   = 1.8;
        PlaygroundGammaSlider.Minimum = 1.0;

        // Smoothing α. Defaults seed the slider with the same value
        // the engine will read after ResyncPlaygroundAmbientTuning
        // overwrites Value from settings on the next line, so the
        // brief window between InitializeComponent and Resync still
        // has a coherent state.
        PlaygroundSmoothingSlider.Maximum = 1.0;
        PlaygroundSmoothingSlider.Value   = 0.30;
        PlaygroundSmoothingSlider.Minimum = 0.05;

        // Subscription to AmbientSettingsService.Changed already lives
        // in the main Loaded handler (OnAmbientSettingsChangedFromPlayground
        // calls ResyncPlaygroundAmbientTuning alongside the pipeline-row
        // sync). Here we just project the initial state into the sliders
        // and release the suppressor flag.
        ResyncPlaygroundAmbientTuning();
        _ambientTuningLoading = false;
    }

    private void ResyncPlaygroundAmbientTuning()
    {
        bool prev = _ambientTuningLoading;
        _ambientTuningLoading = true;
        try
        {
            var s = AmbientSettingsService.Instance.Current;

            PlaygroundExposureSlider.Value         = s.ExposureEv;
            PlaygroundSaturationSlider.Value       = s.SaturationBoost * 100.0;
            PlaygroundMinBrightnessSlider.Value    = s.MinBrightness;
            // Curve param slider mirrors the value of the *active*
            // curve's dedicated parameter. SCurve stores its steepness
            // separately so the slider doesn't carry Gamma's 1.8 over
            // when the user picks SCurve (where 1.8 is nearly invisible).
            PlaygroundGammaSlider.Value            = SelectCurveParamForType(s.BrightnessCurveType, s);
            PlaygroundGammaCanvas.Gamma            = s.BrightnessCurveParam;
            PlaygroundSmoothingSlider.Value        = s.SmoothingAlpha;
            PlaygroundChangeThresholdSlider.Value  = s.ChangeThreshold;
            SelectBrightnessCurveTypeInCombo(s.BrightnessCurveType);
            SelectAmbientModeInCombo(s.Mode);
            UpdatePlaygroundBrightnessCurveDependentUi();

            UpdatePlaygroundExposureText();
            UpdatePlaygroundSaturationText();
            UpdatePlaygroundMinBrightnessText();
            UpdatePlaygroundGammaText();
            UpdatePlaygroundSmoothingText();
            UpdatePlaygroundChangeThresholdText();
        }
        finally
        {
            _ambientTuningLoading = prev;
        }
    }

    // Maps the persisted enum into the ComboBox by Tag. Falls back to
    // Gamma (first non-Linear entry) if a future settings.json carries
    // an unknown value — keeps the UI usable instead of leaving the
    // selection empty.
    private void SelectBrightnessCurveTypeInCombo(BrightnessCurveType type)
    {
        string tag = type.ToString();
        foreach (var item in PlaygroundBrightnessCurveCombo.Items)
        {
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == tag)
            {
                PlaygroundBrightnessCurveCombo.SelectedItem = cbi;
                return;
            }
        }
        // Unknown / future enum value : pick Gamma (index 1).
        PlaygroundBrightnessCurveCombo.SelectedIndex = 1;
    }

    // Toggles the param slider enabled state, refreshes the caption,
    // and hides the BrightnessCurveCanvas visualisation for curves it
    // can't represent (it draws a gamma power-law shape only). Linear
    // and Logarithmic ignore param, so the slider is disabled to make
    // that obvious ; SCurve hides the canvas too until a proper SCurve
    // visualisation is implemented — drawing a gamma curve while the
    // user is tuning an SCurve is actively misleading.
    private void UpdatePlaygroundBrightnessCurveDependentUi()
    {
        var type = ReadBrightnessCurveTypeFromCombo();
        bool paramHasEffect = type == BrightnessCurveType.Gamma
                           || type == BrightnessCurveType.SCurve;
        PlaygroundGammaSlider.IsEnabled = paramHasEffect;

        // The Gamma canvas widget can only draw the power-law shape ;
        // showing it while a different curve is active would lie about
        // the response the engine actually applies. Cache for Gamma
        // only, collapsed otherwise — an SCurve / Log visualisation is
        // a separate palier.
        PlaygroundGammaCanvas.Visibility = type == BrightnessCurveType.Gamma
            ? Visibility.Visible
            : Visibility.Collapsed;

        PlaygroundGammaCaption.Text = type switch
        {
            BrightnessCurveType.Linear      => "Direct pass-through. The brightness param slider has no effect in this mode — disable the curve and rely on smoothing + min brightness instead.",
            BrightnessCurveType.Gamma       => "Power-law squash on the bottom of the bri range. Higher γ dims dim scenes harder without touching saturated highlights. 1.0 — linear · 1.8 — default · 2.5 — strongly dimmed shadows.",
            BrightnessCurveType.SCurve      => "Logistic S-curve pushed mid-tones away from grey in both directions. Higher steepness = harder contrast. 1.0 — almost linear · 2.0 — default · 5.0 — near-step.",
            BrightnessCurveType.Logarithmic => "Lifts the bottom of the range so even very dim scenes stay clearly lit. No param to tune — the curve is fixed.",
            _ => string.Empty,
        };
    }

    // Pick the right persisted parameter for the active curve : Gamma
    // and SCurve each store their own value (exponent vs steepness),
    // Linear and Logarithmic don't consume the param at all.
    private static double SelectCurveParamForType(BrightnessCurveType type, AmbientSettings s)
        => type switch
        {
            BrightnessCurveType.Gamma  => s.BrightnessCurveParam,
            BrightnessCurveType.SCurve => s.BrightnessCurveSCurveSteepness,
            _                          => s.BrightnessCurveParam,
        };

    private BrightnessCurveType ReadBrightnessCurveTypeFromCombo()
    {
        if (PlaygroundBrightnessCurveCombo.SelectedItem is ComboBoxItem cbi
         && cbi.Tag is string tag
         && Enum.TryParse<BrightnessCurveType>(tag, out var parsed))
        {
            return parsed;
        }
        return BrightnessCurveType.Gamma;
    }

    // Mode preset selector — synchronises the ComboBox with the
    // currently active mode. Falls back to Custom (index 3) when the
    // saved enum value is foreign — keeps the UI usable instead of
    // empty.
    private void SelectAmbientModeInCombo(AmbientMode mode)
    {
        string tag = mode.ToString();
        foreach (var item in PlaygroundAmbientModeCombo.Items)
        {
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == tag)
            {
                PlaygroundAmbientModeCombo.SelectedItem = cbi;
                return;
            }
        }
        PlaygroundAmbientModeCombo.SelectedIndex = 3;
    }

    // Switches Current.Mode to Custom and reflects the change in the
    // ComboBox without retriggering the SelectionChanged handler.
    // Caller is responsible for the Save() that follows the tuning
    // mutation — this helper purposefully doesn't save so two calls
    // in the same frame don't double-write the file.
    private void EnterCustomMode()
    {
        var current = AmbientSettingsService.Instance.Current;
        if (current.Mode == AmbientMode.Custom) return;
        bool prev = _ambientTuningLoading;
        _ambientTuningLoading = true;
        try
        {
            current.Mode = AmbientMode.Custom;
            SelectAmbientModeInCombo(AmbientMode.Custom);
        }
        finally
        {
            _ambientTuningLoading = prev;
        }
    }

    private void OnPlaygroundGammaSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundGammaText();
        var type = ReadBrightnessCurveTypeFromCombo();
        // Only the Gamma canvas reads BrightnessCurveParam directly ;
        // mirror the slider value there so the shape follows the
        // slider live. The canvas is hidden for other curves so the
        // assignment is harmless.
        if (type == BrightnessCurveType.Gamma)
            PlaygroundGammaCanvas.Gamma = PlaygroundGammaSlider.Value;

        if (_ambientTuningLoading) return;

        // Persist into the parameter that belongs to the active
        // curve, leaving the other curves' parameters untouched.
        // Linear / Logarithmic ignore the slider but we still write
        // through into the Gamma slot so the value is preserved if
        // the user switches back to Gamma later.
        var s = AmbientSettingsService.Instance.Current;
        switch (type)
        {
            case BrightnessCurveType.SCurve:
                s.BrightnessCurveSCurveSteepness = PlaygroundGammaSlider.Value;
                break;
            default:
                s.BrightnessCurveParam = PlaygroundGammaSlider.Value;
                break;
        }
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    private void OnPlaygroundExposureSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundExposureText();
        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.ExposureEv = PlaygroundExposureSlider.Value;
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    private void OnPlaygroundSaturationSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundSaturationText();
        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.SaturationBoost = PlaygroundSaturationSlider.Value / 100.0;
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    private void OnPlaygroundMinBrightnessSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundMinBrightnessText();
        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.MinBrightness =
            (int)Math.Round(PlaygroundMinBrightnessSlider.Value);
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    private void UpdatePlaygroundGammaText()
    {
        var type = ReadBrightnessCurveTypeFromCombo();
        PlaygroundGammaValueText.Text = type switch
        {
            BrightnessCurveType.Gamma  => $"γ {PlaygroundGammaSlider.Value:F2}",
            BrightnessCurveType.SCurve => $"k {PlaygroundGammaSlider.Value:F2}",
            _                          => "—",
        };
    }

    private void UpdatePlaygroundExposureText()
    {
        double v = PlaygroundExposureSlider.Value;
        PlaygroundExposureValueText.Text = $"{(v >= 0 ? "+" : "")}{v:F1} EV";
    }

    private void UpdatePlaygroundSaturationText()
        => PlaygroundSaturationValueText.Text = $"{(int)Math.Round(PlaygroundSaturationSlider.Value)} %";

    private void UpdatePlaygroundMinBrightnessText()
        => PlaygroundMinBrightnessValueText.Text = $"{(int)Math.Round(PlaygroundMinBrightnessSlider.Value)}";

    private void UpdatePlaygroundSmoothingText()
        => PlaygroundSmoothingValueText.Text = $"α {PlaygroundSmoothingSlider.Value:F2}";

    private void UpdatePlaygroundChangeThresholdText()
        => PlaygroundChangeThresholdValueText.Text = $"{(int)Math.Round(PlaygroundChangeThresholdSlider.Value)}";

    // Smoothing α slider — persists into AmbientSettings.SmoothingAlpha.
    // The engine re-reads it on every push tick so the move applies
    // within ~66 ms. Touching it counts as a tuning gesture — mode
    // implicitly flips to Custom.
    private void OnPlaygroundSmoothingSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundSmoothingText();
        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.SmoothingAlpha = PlaygroundSmoothingSlider.Value;
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    // Change threshold slider — persists into AmbientSettings.ChangeThreshold.
    // 0 disables the gate ; higher values let smoothing absorb more
    // before any push is allowed through.
    private void OnPlaygroundChangeThresholdSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdatePlaygroundChangeThresholdText();
        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.ChangeThreshold =
            (int)Math.Round(PlaygroundChangeThresholdSlider.Value);
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    // Curve type ComboBox — persists the enum into settings, refreshes
    // the param slider's enabled state + caption + canvas visibility,
    // and re-projects the slider Value from the dedicated parameter
    // of the newly-selected curve so the slider position stays
    // semantically meaningful. Changing the curve counts as a tuning
    // gesture so the mode implicitly switches to Custom.
    private void OnPlaygroundBrightnessCurveTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var type = ReadBrightnessCurveTypeFromCombo();

        // Bring the slider in line with the curve's own param before
        // we refresh the dependent UI — the suppressor avoids the
        // ValueChanged handler interpreting our re-projection as a
        // user tuning gesture (and re-persisting the value).
        bool prev = _ambientTuningLoading;
        _ambientTuningLoading = true;
        try
        {
            var s = AmbientSettingsService.Instance.Current;
            PlaygroundGammaSlider.Value = SelectCurveParamForType(type, s);
        }
        finally
        {
            _ambientTuningLoading = prev;
        }

        UpdatePlaygroundBrightnessCurveDependentUi();
        UpdatePlaygroundGammaText();

        if (_ambientTuningLoading) return;
        AmbientSettingsService.Instance.Current.BrightnessCurveType = type;
        EnterCustomMode();
        AmbientSettingsService.Instance.Save();
    }

    // Mode preset selector. Picking Game / Movie / Ambient runs the
    // matching AmbientModePresets snapshot through ApplyPreset (which
    // also saves) and the Changed event re-syncs every slider via
    // ResyncPlaygroundAmbientTuning. Picking Custom is a no-op — the
    // user explicitly opting in to their own values.
    private void OnPlaygroundAmbientModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ambientTuningLoading) return;
        if (PlaygroundAmbientModeCombo.SelectedItem is ComboBoxItem cbi
            && cbi.Tag is string tag
            && Enum.TryParse<AmbientMode>(tag, out var mode))
        {
            AmbientSettingsService.Instance.ApplyPreset(mode);
        }
    }
}
