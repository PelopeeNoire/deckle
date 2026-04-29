using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Localization;
using WhispUI.Logging;
using WhispUI.Setup;

namespace WhispUI.Shell.Setup;

// ── InstallingPage ───────────────────────────────────────────────────────────
//
// Runs the installs in bulk (sequential V1) and reports per-item progress.
// Native runtime is already in place by this point — ChoicesPage's Browse
// flow copied it on demand. Two downloads remain: the chosen Whisper model
// and the mandatory Silero VAD.
//
// Cancellation is handled inline (Cancel install button on the page),
// not via the shell footer. The shell's Cancel button is hidden while
// this page is active to avoid two competing affordances.
//
// On completion (success or failure), the page navigates to SummaryPage
// which renders the Results captured here. Errors don't abort the run —
// each item's result is appended independently so the user sees what
// worked and what didn't.
internal sealed partial class InstallingPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    private SetupWindow? _setup;
    private SetupContext? _context;
    private CancellationTokenSource? _cts;
    private DispatcherQueue? _dispatcher;

    public InstallingPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SetupWindow setup) return;

        _setup       = setup;
        _context     = setup.Context;
        _dispatcher  = DispatcherQueue.GetForCurrentThread();

        setup.SetStepHeader(
            Loc.Get("Setup_StepTitle_Installing"),
            Loc.Get("Setup_StepSubtitle_Installing"));
        setup.SetBackEnabled(false);
        setup.SetNextEnabled(false);
        setup.SetNextVisible(false);
        setup.SetCancelVisible(false); // inline Cancel install button instead

        WhisperLabel.Text = _context.SelectedModel.DisplayName;

        _ = InstallAllAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
    }

    private async Task InstallAllAsync()
    {
        if (_setup is null || _context is null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Directory.CreateDirectory(AppPaths.ModelsDirectory);

        // 1. Whisper model
        await DownloadOneAsync(
            _context.SelectedModel,
            isFirst: true,
            ct);

        // 2. Silero VAD (only if not already there — saves a redundant ~700 KB)
        if (SpeechModels.IsVadInstalled())
        {
            _context.Results.Add(new InstallResult(
                ItemId:      SpeechModels.VadModel.Id,
                DisplayName: SpeechModels.VadModel.DisplayName,
                Success:     true,
                ErrorMessage: null,
                Bytes:        new FileInfo(Path.Combine(AppPaths.ModelsDirectory, SpeechModels.VadModelFileName)).Length));
            UpdateGlobalStep(2, Loc.Get("Setup_Install_VadAlreadyInstalledStatus"));
            SetItemDone(VadIcon, VadProgress, VadStatus, Loc.Get("Setup_Install_AlreadyInstalled"));
        }
        else
        {
            await DownloadOneAsync(
                SpeechModels.VadModel,
                isFirst: false,
                ct);
        }

        UpdateGlobalStep(2, Loc.Get("Setup_Install_Done"));

        // Hand off to the summary page. Frame.Navigate is UI-thread-safe
        // when invoked from an awaited continuation that resumed on the
        // UI thread (the awaiter for Downloader.DownloadAsync runs on the
        // captured SyncContext = DispatcherQueueSynchronizationContext).
        _setup.Body.Navigate(typeof(SummaryPage), _setup);
    }

    // Runs one download against an entry that exposes a Url. Updates the
    // matching UI controls + emits the per-item Result. Catches inside,
    // never throws to the caller — failures land in Results and the run
    // continues with the next item.
    private async Task DownloadOneAsync(ModelEntry entry, bool isFirst, CancellationToken ct)
    {
        if (_context is null) return;

        ProgressBar bar = isFirst ? WhisperProgress : VadProgress;
        TextBlock   label = isFirst ? WhisperLabel : VadLabel;
        TextBlock   status = isFirst ? WhisperStatus : VadStatus;
        FontIcon    icon  = isFirst ? WhisperIcon : VadIcon;

        UpdateGlobalStep(isFirst ? 0 : 1,
            Loc.Format(isFirst ? "Setup_Install_Step1Of2_Format" : "Setup_Install_Step2Of2_Format",
                       entry.DisplayName));

        SetItemRunning(icon, bar, status, Loc.Get("Setup_Install_Connecting"));

        long startTicks = Environment.TickCount64;
        var progress = new Progress<Downloader.DownloadProgress>(p => OnDownloadProgress(p, bar, status));

        try
        {
            var result = await Downloader.DownloadAsync(
                entry.Url,
                Path.Combine(AppPaths.ModelsDirectory, entry.FileName),
                entry.Sha256,
                progress,
                ct);

            long durMs = Environment.TickCount64 - startTicks;

            if (result.Success)
            {
                long bytes = new FileInfo(Path.Combine(AppPaths.ModelsDirectory, entry.FileName)).Length;
                _context.Results.Add(new InstallResult(
                    ItemId:      entry.Id,
                    DisplayName: entry.DisplayName,
                    Success:     true,
                    ErrorMessage: null,
                    Bytes:        bytes));
                _log.Info(LogSource.Setup,
                    $"setup item ok | id={entry.Id} | bytes={bytes} | dur_ms={durMs} | sha256={result.ActualSha256}");
                SetItemDone(icon, bar, status, Loc.Format("Setup_Install_Done_Format", FormatBytes(bytes)));
            }
            else
            {
                _context.Results.Add(new InstallResult(
                    ItemId:      entry.Id,
                    DisplayName: entry.DisplayName,
                    Success:     false,
                    ErrorMessage: result.ErrorMessage,
                    Bytes:        null));
                _log.Warning(LogSource.Setup,
                    $"setup item failed | id={entry.Id} | error={result.ErrorMessage}");
                SetItemFailed(icon, bar, status, result.ErrorMessage ?? Loc.Get("Setup_Install_UnknownError"));
            }
        }
        catch (OperationCanceledException)
        {
            _context.Results.Add(new InstallResult(
                ItemId:      entry.Id,
                DisplayName: entry.DisplayName,
                Success:     false,
                ErrorMessage: "cancelled",
                Bytes:        null));
            _log.Info(LogSource.Setup, $"setup item cancelled | id={entry.Id}");
            SetItemFailed(icon, bar, status, Loc.Get("Setup_Install_Cancelled"));
        }
    }

    // ── UI helpers (must run on UI thread) ────────────────────────────────────

    private void OnDownloadProgress(Downloader.DownloadProgress p, ProgressBar bar, TextBlock status)
    {
        if (_dispatcher is null) return;

        _dispatcher.TryEnqueue(() =>
        {
            if (p.Percent is double pct)
            {
                bar.IsIndeterminate = false;
                bar.Minimum = 0;
                bar.Maximum = 1;
                bar.Value   = pct;
                status.Text = Loc.Format(
                    "Setup_Install_Progress_WithTotal_Format",
                    FormatBytes(p.BytesDownloaded),
                    FormatBytes(p.TotalBytes ?? 0),
                    pct.ToString("P0", CultureInfo.CurrentCulture));
            }
            else
            {
                bar.IsIndeterminate = true;
                status.Text = Loc.Format("Setup_Install_Progress_NoTotal_Format", FormatBytes(p.BytesDownloaded));
            }
        });
    }

    private void UpdateGlobalStep(int completedTasks, string status)
    {
        GlobalProgress.Value     = completedTasks;
        GlobalStatusText.Text    = status;
    }

    private void SetItemRunning(FontIcon icon, ProgressBar bar, TextBlock status, string text)
    {
        icon.Glyph = "";   // FileDownload
        bar.Visibility = Visibility.Visible;
        bar.IsIndeterminate = true;
        status.Text = text;
    }

    private void SetItemDone(FontIcon icon, ProgressBar bar, TextBlock status, string text)
    {
        icon.Glyph = "";   // CheckMark
        bar.IsIndeterminate = false;
        bar.Maximum = 1;
        bar.Value = 1;
        status.Text = text;
    }

    private void SetItemFailed(FontIcon icon, ProgressBar bar, TextBlock status, string text)
    {
        icon.Glyph = "";   // ErrorBadge
        bar.IsIndeterminate = false;
        bar.Value = 0;
        status.Text = text;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)               return $"{bytes} B";
        if (bytes < 1024L * 1024)       return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
