using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Deckle.Interop;
using Deckle.Logging;
using Deckle.Shell;

namespace Deckle.Playground;

// ─── Playground window shell ─────────────────────────────────────────────────
//
// Native TitleBar + Mica backdrop + NavigationView Auto + Frame. Hosts
// three pages : HomePage, HudPage, AmbientPage — each owning its tuning
// surface, ViewModel, and runtime resources. The window itself only
// routes navigation and forwards the lifecycle calls the App makes
// (SetRecordingState, ShowAndActivate, Closing→Hide, Closed→DisposeResources).
//
// Pattern : same as SettingsWindow. NavigationViewItem.Tag carries the
// fully-qualified Page type name, resolved via Type.GetType in
// OnNavSelectionChanged. Pages declare NavigationCacheMode.Required so
// their tuning state and runtime resources survive nav switches ;
// PlaygroundWindow.Closed disposes the resources owned by each page
// via the DisposeResources() entry points.
//
// Singleton-hidden : Close → Cancel + Hide (App.OnLaunched creates the
// instance once and reuses it). Closed only fires at QuitApp ; that's
// where the runtime resources owned by HudPage and AmbientPage are
// torn down.
public sealed partial class PlaygroundWindow : Window
{
    private static readonly LogService _log = LogService.Instance;
    private readonly IntPtr _hwnd;

    // Icons shared with tray / LogWindow / SettingsWindow via IconAssets.
    // Swapping the .ico on disk propagates everywhere.
    private BitmapImage? _iconIdle;
    private BitmapImage? _iconRecording;
    private string? _iconIdlePath;
    private string? _iconRecordingPath;

    // Page references resolved on first navigate so the shell can call
    // ForcePause / DisposeResources without walking the Frame's content
    // tree on every interaction.
    private HudPage? _hudPage;
    private AmbientPage? _ambientPage;

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

        // Wire the Pages → shell navigation callback. HomePage's
        // routing cards invoke PlaygroundShell.NavigateTo("hud" /
        // "ambient") to bring the matching NavigationView item into
        // selected state without holding a back-reference to this
        // Window.
        PlaygroundShell.NavigateTo = NavigateTo;

        Title = "Deckle Playground";
        // Default 1800×1440 — comfortable two-column footprint (preview
        // + tuning expanders) on a typical 1440p display. Min 1280×600
        // keeps everything reachable below that.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1800, 1440));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = true;
        presenter.IsResizable   = true;
        presenter.PreferredMinimumWidth  = 1280;
        presenter.PreferredMinimumHeight = 600;
        AppWindow.SetPresenter(presenter);

        // Close → real destruction. Diverges intentionally from
        // SettingsWindow / LogWindow (which Cancel→Hide to preserve
        // state across opens). The Playground holds heavy runtime
        // resources (Win2D composition, screen capture, frame sampler,
        // Hue REST client, preview timers) — when the user dismisses
        // the window they expect the costs to go with it. App.xaml.cs
        // nullifies its reference on Closed so the next ShowPlaygroundLazy
        // call builds a fresh instance, starting from the persisted
        // AmbientSettings without any in-memory carry-over from the
        // previous session.

        // NavigationView pane toggle tooltip override : default OS locale
        // string ("Ouvrir navigation" on FR) clashes with the rest of the
        // UI which is locked in English. Tooltip applied after the
        // template generator has materialised the button (Loaded → Low
        // priority dispatch), re-applied on PaneOpened / PaneClosed.
        Nav.Loaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => OverrideNavPaneToggleTooltip(Nav, "Open navigation"));
        };
        Nav.PaneOpened += (_, _) =>
            OverrideNavPaneToggleTooltip(Nav, "Open navigation");
        Nav.PaneClosed += (_, _) =>
            OverrideNavPaneToggleTooltip(Nav, "Open navigation");

        // Tap on empty background → move focus to RootGrid, dismisses
        // the caret from a NumberBox the user just edited. Filter on
        // OriginalSource so clicks landing on buttons / dropdowns /
        // ComboBox flyouts don't steal focus mid-action.
        RootGrid.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, RootGrid))
                RootGrid.Focus(FocusState.Pointer);
        };

        // Initial selection : Home. Setting SelectedItem fires
        // OnNavSelectionChanged → PageFrame.Navigate(HomePage).
        Nav.SelectedItem = Nav.MenuItems[0];

        this.Closed += OnWindowClosed;
    }

    // ── Lifecycle surface (called by App) ───────────────────────────────────

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter op &&
            op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        // Reset to Pause systematically on each show — known, predictable
        // state on every reopen, independent of what the user left when
        // they last closed. The HudPage handles the actual ApplyTarget
        // through its VM observer.
        _hudPage?.ForcePause();

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

    // ── Window close → page resource disposal ───────────────────────────────

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Tear down resources owned by the pages — composition preview,
        // capture service, frame sampler, light output, observers. The
        // shell deliberately drives this from Closed (terminal) rather
        // than Closing (Cancel→Hide path) so a hide / show cycle
        // preserves tuning state.
        try { _hudPage?.DisposeResources(); } catch { /* best effort */ }
        try { _ambientPage?.DisposeResources(); } catch { /* best effort */ }

        // Drop the shell's nav callback so a stale page reference can't
        // route into a destroyed window. ReferenceEquals (not ==) because
        // Window inherits the default object equality and the compiler
        // can't prove no derived operator== was added — explicit identity
        // check sidesteps the CS0252 warning.
        if (PlaygroundShell.NavigateTo is not null
            && ReferenceEquals(PlaygroundShell.NavigateTo.Target, this))
        {
            PlaygroundShell.NavigateTo = null;
        }
    }

    // ── Navigation routing ──────────────────────────────────────────────────
    //
    // Same Tag → Type.GetType pattern as SettingsWindow. The CurrentSourcePageType
    // guard avoids redundant re-Navigate on the initial seed.

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        if (item.Tag is not string tag)
        {
            _log.Warning(LogSource.Playground, $"nav impossible | reason=no-tag | item={item.Content}");
            return;
        }

        var pageType = Type.GetType(tag);
        if (pageType is null)
        {
            _log.Error(LogSource.Playground, $"nav failed | reason=type-not-found | tag={tag}");
            return;
        }

        if (PageFrame.CurrentSourcePageType == pageType) return;

        try
        {
            bool ok = PageFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            if (!ok)
            {
                _log.Error(LogSource.Playground, $"navigate failed | page={pageType.Name} | reason=frame-returned-false");
                return;
            }

            // Cache the resolved page instance so ShowAndActivate /
            // DisposeResources don't have to walk the Frame's content
            // tree. NavigationCacheMode.Required on each Page means the
            // first nav builds it ; subsequent navs reuse the same
            // instance, so the reference captured here stays valid for
            // the lifetime of the shell.
            switch (PageFrame.Content)
            {
                case HudPage hud:
                    _hudPage = hud;
                    break;
                case AmbientPage amb:
                    _ambientPage = amb;
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Playground, $"navigate threw | page={pageType.Name} | error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // PlaygroundShell.NavigateTo callback target. Pages invoke this with
    // a short tag ("home" / "hud" / "ambient") and the shell maps it to
    // the matching NavigationViewItem.Tag prefix.
    private void NavigateTo(string shortTag)
    {
        string fullTag = shortTag switch
        {
            "home"    => "Deckle.Playground.HomePage",
            "hud"     => "Deckle.Playground.HudPage",
            "ambient" => "Deckle.Playground.AmbientPage",
            _         => "",
        };
        if (string.IsNullOrEmpty(fullTag)) return;

        foreach (var menuItem in Nav.MenuItems)
        {
            if (menuItem is NavigationViewItem nvi
                && nvi.Tag is string tag
                && tag == fullTag)
            {
                Nav.SelectedItem = nvi;
                return;
            }
        }
    }

    // ── NavigationView tooltip i18n override ────────────────────────────────
    //
    // Same helper as in SettingsWindow ; kept duplicated by design — two
    // callsites isn't enough to justify a shared assembly, and pulling it
    // into Deckle.Localization would force that pure resw-facing module
    // to take a WinUI 3 control dependency it doesn't otherwise need.
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
}
