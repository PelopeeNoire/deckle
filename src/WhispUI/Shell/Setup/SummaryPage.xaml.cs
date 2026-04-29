using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Logging;
using WhispUI.Setup;

namespace WhispUI.Shell.Setup;

// ── SummaryPage ──────────────────────────────────────────────────────────────
//
// Final wizard step. Renders the per-item Results captured by InstallingPage,
// surfaces success or failure as an InfoBar at the top, and configures the
// shell footer to either complete the wizard ("Get started") or offer a
// retry on the install step ("Retry" + "Quit").
//
// Retry path navigates back to InstallingPage with a fresh Results list —
// the user gets another shot at the failed download(s) without re-doing
// the choices step.
internal sealed partial class SummaryPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    private SetupWindow? _setup;
    private SetupContext? _context;

    public SummaryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SetupWindow setup) return;

        _setup   = setup;
        _context = setup.Context;

        bool ok = _context.AllSucceeded;

        setup.SetStepHeader(
            ok ? "All set" : "Some items could not be installed",
            ok ? "Dependencies installed. The app is ready to use."
               : "Review the issues below and either retry or quit.");
        setup.SetBackEnabled(false);
        setup.SetNextVisible(true);
        setup.SetNextEnabled(true);
        setup.SetNextLabel(ok ? "Get started" : "Retry");
        setup.SetCancelVisible(!ok);
        setup.NextRequested += OnNextRequested;

        ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title    = ok ? "Installation complete" : "Installation incomplete";
        ResultBar.Message  = ok
            ? $"Stored under {_context.Location}."
            : $"{CountFailed(_context)} of {_context.Results.Count} item(s) failed.";

        RenderResults();

        _log.Info(LogSource.Setup,
            $"setup summary | success={ok} | items={_context.Results.Count}");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_setup is not null) _setup.NextRequested -= OnNextRequested;
    }

    private void OnNextRequested()
    {
        if (_setup is null || _context is null) return;

        if (_context.AllSucceeded)
        {
            _setup.Complete(true);
            return;
        }

        // Retry: clear previous results and re-enter the install step.
        _context.Results.Clear();
        _setup.Body.Navigate(typeof(InstallingPage), _setup);
    }

    private void RenderResults()
    {
        if (_context is null) return;

        ItemsPanel.Children.Clear();

        foreach (var r in _context.Results)
        {
            ItemsPanel.Children.Add(BuildResultRow(r));
        }
    }

    private static Grid BuildResultRow(InstallResult r)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            // E73E = CheckMark, E711 = CrossMark, both in Segoe Fluent Icons.
            Glyph = r.Success ? "" : "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = r.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        string detail = r.Success
            ? (r.Bytes is long b ? FormatBytes(b) + " installed" : "installed")
            : r.ErrorMessage ?? "unknown error";

        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        return grid;
    }

    private static int CountFailed(SetupContext ctx)
    {
        int n = 0;
        foreach (var r in ctx.Results) if (!r.Success) n++;
        return n;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)               return $"{bytes} B";
        if (bytes < 1024L * 1024)       return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
