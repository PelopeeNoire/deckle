using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Whisp.Setup;

namespace Deckle.Shell.Setup;

// ── SetupWindow ──────────────────────────────────────────────────────────────
//
// Shell of the first-run wizard. Three rows: a drag-region pad above the
// step header, the page Frame in the middle, and a fixed footer with
// Cancel + Back + Next. Mica backdrop, custom Tall title bar (no back
// button — the Back button lives in the footer instead).
//
// **B.2 scope** — shell only. The Frame is empty in this commit; pages
// (Choices, Installing, Summary) come in B.3-B.5 and Navigate into
// ContentFrame with a SetupContext passed as parameter.
//
// Pages drive the shell via the public surface below:
//   • Header text     — SetStepHeader(title, subtitle)
//   • Footer state    — SetBackEnabled / SetNextEnabled / SetNextLabel /
//                       SetNextVisible / SetCancelVisible
//   • Footer events   — NextRequested / BackRequested  (the Window itself
//                       doesn't know how to advance; the active page does)
//   • Termination     — Complete(success)              (pages call this
//                       on Done; CancelButton + window close call false)
//
// Lifecycle: App.OnLaunched (later, in B.6) instantiates this Window,
// awaits Completion, and either boots the engine (success=true) or
// exits the app (false). The TaskCompletionSource resolves on Complete()
// or on Window.Closed, whichever fires first.
internal sealed partial class SetupWindow : Window
{
    private static readonly LogService _log = LogService.Instance;
    private readonly TaskCompletionSource<bool> _completion = new();

    // Shared state every page reads/writes. Created here so pages don't
    // each instantiate their own — the Window is the lifetime owner.
    public SetupContext Context { get; }

    // Resolves true when a page calls Complete(true); false on Cancel,
    // window close, or Complete(false). App.OnLaunched awaits this before
    // booting the engine.
    public Task<bool> Completion => _completion.Task;

    // Exposed so pages can Frame.Navigate without going through the
    // Window's internals. Pages pass `this.Frame.Navigate(typeof(Next),
    // setupWindow)` and the next page picks the SetupWindow up from
    // OnNavigatedTo.Parameter.
    public Frame Body => ContentFrame;

    public event Action? NextRequested;
    public event Action? BackRequested;

    public SetupWindow()
    {
        InitializeComponent();
        Context = new SetupContext();

        // Mica on long-lived windows — same primitive as Settings, Logs,
        // and the rest of the app's persistent surfaces. DWM applies the
        // shell rounded corners and shadow.
        SystemBackdrop = new MicaBackdrop();

        ConfigureWindow();

        Closed += OnWindowClosed;
        _log.Info(LogSource.Setup, "setup window opened");
    }

    // ── Public surface for pages ───────────────────────────────────────────

    public void SetStepHeader(string title, string subtitle)
    {
        StepTitle.Text    = title;
        StepSubtitle.Text = subtitle;
    }

    public void SetBackEnabled(bool enabled)  => BackButton.IsEnabled = enabled;
    public void SetNextEnabled(bool enabled)  => NextButton.IsEnabled = enabled;
    public void SetNextLabel(string label)    => NextButton.Content   = label;
    public void SetNextVisible(bool visible)  => NextButton.Visibility   = ToVisibility(visible);
    public void SetCancelVisible(bool visible) => CancelButton.Visibility = ToVisibility(visible);

    public void Complete(bool success)
    {
        _log.Info(LogSource.Setup, $"setup window closing | success={success}");
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(success);
        Close();
    }

    // ── Plumbing ───────────────────────────────────────────────────────────

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private void ConfigureWindow()
    {
        // 720×520 centred on the primary work area — matches Dev Home
        // SetupFlowPage / Settings Win11 OOBE. Tall enough for a card-
        // based choices page plus a progress section without scrolling.
        ExtendsContentIntoTitleBar = true;
        if (AppWindow is { } appWindow)
        {
            appWindow.Title = Loc.Get("Setup_WindowTitle");

            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null)
            {
                int w = 720, h = 520;
                int x = area.WorkArea.X + (area.WorkArea.Width  - w) / 2;
                int y = area.WorkArea.Y + (area.WorkArea.Height - h) / 2;
                appWindow.MoveAndResize(new RectInt32(x, y, w, h));
            }

            if (appWindow.TitleBar is { } titleBar)
            {
                titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
                titleBar.ButtonBackgroundColor         = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)   => BackRequested?.Invoke();
    private void OnNextClick(object sender, RoutedEventArgs e)   => NextRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Complete(false);

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // X button or Alt+F4 path — treat as cancel if no page completed.
        if (!_completion.Task.IsCompleted) _completion.TrySetResult(false);
    }
}
