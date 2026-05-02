using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Whisp.Setup;

namespace Deckle.Shell.Setup;

// ── InstallingPage ───────────────────────────────────────────────────────────
//
// Runs the installs in bulk (sequential V1) and reports per-item progress.
// Three rows now: native runtime (whisper.cpp + Vulkan + MinGW DLLs),
// the chosen Whisper model, and the mandatory Silero VAD model. The
// native row was added when the wizard moved away from forcing the user
// to Browse... for a folder of DLLs — the auto-download path fetches the
// versioned zip from the Deckle GitHub Release defined in
// NativeRuntime.CurrentBundle.
//
// Already-installed shortcut applies to all three: a row that detects an
// existing valid install renders as "already installed" green check
// without consuming bandwidth.
//
// Cancellation is handled inline (Cancel install button on the page),
// not via the shell footer. The shell's Cancel button is hidden while
// this page is active to avoid two competing affordances.
//
// On completion (success or failure), the page navigates to SummaryPage
// which renders the Results captured here. Errors don't abort the run —
// each item's result is appended independently so the user sees what
// worked and what didn't, EXCEPT for a placeholder native bundle URL,
// which short-circuits the run since nothing else can be installed
// without the runtime.
internal sealed partial class InstallingPage : Page
{
    private const int TotalSteps = 3;
    private const string NativeRuntimeItemId = "native-runtime";

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

    // ── Orchestration ─────────────────────────────────────────────────────────

    private async Task InstallAllAsync()
    {
        if (_setup is null || _context is null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Directory.CreateDirectory(AppPaths.ModelsDirectory);

        // Step 1 — native runtime (whisper.cpp + Vulkan + MinGW DLLs).
        bool nativeOk = await InstallNativeRuntimeStepAsync(ct);
        if (!nativeOk)
        {
            // Placeholder URL with no local install — the run cannot proceed
            // because everything downstream depends on libwhisper. Hand off
            // to summary so the user sees the explicit failure.
            _setup.Body.Navigate(typeof(SummaryPage), _setup);
            return;
        }

        // Step 2 — Whisper model (skip if already on disk — saves a 3 GB
        // re-download when the user only wiped the native bundle).
        if (SpeechModels.IsInstalled(_context.SelectedModel))
        {
            _context.Results.Add(new InstallResult(
                ItemId:      _context.SelectedModel.Id,
                DisplayName: _context.SelectedModel.DisplayName,
                Success:     true,
                ErrorMessage: null,
                Bytes:        new FileInfo(Path.Combine(AppPaths.ModelsDirectory, _context.SelectedModel.FileName)).Length));
            UpdateGlobalStep(2, Loc.Get("Setup_Install_AlreadyInstalled"));
            SetItemDone(WhisperIcon, WhisperProgress, WhisperStatus, Loc.Get("Setup_Install_AlreadyInstalled"));
        }
        else
        {
            await DownloadModelStepAsync(
                entry:     _context.SelectedModel,
                stepIndex: 1,
                icon:      WhisperIcon,
                label:     WhisperLabel,
                bar:       WhisperProgress,
                status:    WhisperStatus,
                ct:        ct);
        }

        // Step 3 — Silero VAD (only if not already there — saves a redundant ~700 KB).
        if (SpeechModels.IsVadInstalled())
        {
            _context.Results.Add(new InstallResult(
                ItemId:      SpeechModels.VadModel.Id,
                DisplayName: SpeechModels.VadModel.DisplayName,
                Success:     true,
                ErrorMessage: null,
                Bytes:        new FileInfo(Path.Combine(AppPaths.ModelsDirectory, SpeechModels.VadModelFileName)).Length));
            UpdateGlobalStep(TotalSteps, Loc.Get("Setup_Install_VadAlreadyInstalledStatus"));
            SetItemDone(VadIcon, VadProgress, VadStatus, Loc.Get("Setup_Install_AlreadyInstalled"));
        }
        else
        {
            await DownloadModelStepAsync(
                entry:     SpeechModels.VadModel,
                stepIndex: 2,
                icon:      VadIcon,
                label:     VadLabel,
                bar:       VadProgress,
                status:    VadStatus,
                ct:        ct);
        }

        UpdateGlobalStep(TotalSteps, Loc.Get("Setup_Install_Done"));

        // Hand off to the summary page. Frame.Navigate is UI-thread-safe
        // when invoked from an awaited continuation that resumed on the
        // UI thread (the awaiter for Downloader.DownloadAsync runs on the
        // captured SyncContext = DispatcherQueueSynchronizationContext).
        _setup.Body.Navigate(typeof(SummaryPage), _setup);
    }

    // ── Native runtime step ───────────────────────────────────────────────────

    // Returns true when the native runtime is present at the end of the step
    // (already installed, or just installed). Returns false only when the
    // bundle URL is still a placeholder AND nothing was previously installed
    // — that's the one fatal short-circuit because libwhisper is required
    // for everything downstream.
    private async Task<bool> InstallNativeRuntimeStepAsync(CancellationToken ct)
    {
        if (_context is null) return false;

        // Path A — already installed (Browse... on ChoicesPage, setup-assets.ps1,
        // or a previous wizard run).
        if (NativeRuntime.IsInstalled())
        {
            _context.Results.Add(new InstallResult(
                ItemId:       NativeRuntimeItemId,
                DisplayName:  NativeRuntime.CurrentBundle.DisplayName,
                Success:      true,
                ErrorMessage: null,
                Bytes:        null));
            SetItemDone(NativeIcon, NativeProgress, NativeStatus, Loc.Get("Setup_Install_AlreadyInstalled"));
            return true;
        }

        // Path B — placeholder URL (build artifact: the Deckle release that
        // hosts the runtime zip hasn't been published yet). ChoicesPage gates
        // Next on IsInstalled when the URL is a placeholder, so reaching this
        // branch means a bug. Surface it explicitly rather than crashing on
        // a 404 download.
        if (NativeRuntime.BundleUrlIsPlaceholder)
        {
            _context.Results.Add(new InstallResult(
                ItemId:       NativeRuntimeItemId,
                DisplayName:  NativeRuntime.CurrentBundle.DisplayName,
                Success:      false,
                ErrorMessage: "auto-download URL is a placeholder; use Browse... on the previous step",
                Bytes:        null));
            _log.Error(LogSource.Setup,
                "setup native runtime aborted | reason=placeholder_url");
            SetItemFailed(NativeIcon, NativeProgress, NativeStatus,
                Loc.Get("Setup_Install_NativePlaceholderUrl"));
            return false;
        }

        // Path C — auto-download from the Deckle release.
        return await DownloadAndInstallNativeAsync(ct);
    }

    private async Task<bool> DownloadAndInstallNativeAsync(CancellationToken ct)
    {
        if (_context is null) return false;

        var bundle = NativeRuntime.CurrentBundle;

        UpdateGlobalStep(0, Loc.Format("Setup_Install_Step1Of3_Format", bundle.DisplayName));
        SetItemRunning(NativeIcon, NativeProgress, NativeStatus, Loc.Get("Setup_Install_Connecting"));

        // Stage to <NativeDirectory>/_bundle.zip — same drive as the final
        // location, so File.Move during InstallFromZipAsync is rename-only,
        // not copy-then-delete.
        Directory.CreateDirectory(AppPaths.NativeDirectory);
        string zipPath = Path.Combine(AppPaths.NativeDirectory, "_bundle.zip");

        long startTicks = Environment.TickCount64;
        var progress = new Progress<Downloader.DownloadProgress>(p => OnDownloadProgress(p, NativeProgress, NativeStatus));

        try
        {
            var dl = await Downloader.DownloadAsync(
                bundle.Url, zipPath, bundle.Sha256, progress, ct);

            if (!dl.Success)
            {
                _context.Results.Add(new InstallResult(
                    ItemId:       NativeRuntimeItemId,
                    DisplayName:  bundle.DisplayName,
                    Success:      false,
                    ErrorMessage: dl.ErrorMessage,
                    Bytes:        null));
                _log.Warning(LogSource.Setup,
                    $"setup native download failed | error={dl.ErrorMessage}");
                SetItemFailed(NativeIcon, NativeProgress, NativeStatus,
                    dl.ErrorMessage ?? Loc.Get("Setup_Install_UnknownError"));
                TryDelete(zipPath);
                return false;
            }

            // Extract → atomic rename per file → delete the staged zip.
            SetItemRunning(NativeIcon, NativeProgress, NativeStatus, Loc.Get("Setup_Install_Extracting"));
            int extracted = await NativeRuntime.InstallFromZipAsync(zipPath, ct);
            TryDelete(zipPath);

            long bytes = bundle.SizeBytes;
            long durMs = Environment.TickCount64 - startTicks;

            if (extracted < NativeRuntime.RequiredDllNames.Count)
            {
                string err = $"bundle is incomplete (extracted {extracted}/{NativeRuntime.RequiredDllNames.Count} DLLs)";
                _context.Results.Add(new InstallResult(
                    ItemId:       NativeRuntimeItemId,
                    DisplayName:  bundle.DisplayName,
                    Success:      false,
                    ErrorMessage: err,
                    Bytes:        bytes));
                _log.Error(LogSource.Setup,
                    $"setup native incomplete | extracted={extracted} | expected={NativeRuntime.RequiredDllNames.Count}");
                SetItemFailed(NativeIcon, NativeProgress, NativeStatus, err);
                return false;
            }

            _context.Results.Add(new InstallResult(
                ItemId:       NativeRuntimeItemId,
                DisplayName:  bundle.DisplayName,
                Success:      true,
                ErrorMessage: null,
                Bytes:        bytes));
            _log.Info(LogSource.Setup,
                $"setup native ok | bundle=native-v{bundle.Version} | bytes={bytes} | dur_ms={durMs} | sha256={dl.ActualSha256}");
            SetItemDone(NativeIcon, NativeProgress, NativeStatus,
                Loc.Format("Setup_Install_Done_Format", FormatBytes(bytes)));
            return true;
        }
        catch (OperationCanceledException)
        {
            _context.Results.Add(new InstallResult(
                ItemId:       NativeRuntimeItemId,
                DisplayName:  bundle.DisplayName,
                Success:      false,
                ErrorMessage: "cancelled",
                Bytes:        null));
            _log.Info(LogSource.Setup, "setup native cancelled");
            SetItemFailed(NativeIcon, NativeProgress, NativeStatus, Loc.Get("Setup_Install_Cancelled"));
            TryDelete(zipPath);
            return false;
        }
    }

    // ── Model step (Whisper + VAD share this) ─────────────────────────────────

    // Runs one model download against an entry that exposes a Url. Updates
    // the matching UI controls + emits the per-item Result. Catches inside,
    // never throws to the caller — failures land in Results and the run
    // continues with the next item.
    private async Task DownloadModelStepAsync(
        ModelEntry entry,
        int stepIndex,
        FontIcon icon,
        TextBlock label,
        ProgressBar bar,
        TextBlock status,
        CancellationToken ct)
    {
        if (_context is null) return;

        UpdateGlobalStep(stepIndex, FormatStepLabel(stepIndex, entry.DisplayName));
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

    private static string FormatStepLabel(int stepIndex, string itemName) => stepIndex switch
    {
        0 => Loc.Format("Setup_Install_Step1Of3_Format", itemName),
        1 => Loc.Format("Setup_Install_Step2Of3_Format", itemName),
        2 => Loc.Format("Setup_Install_Step3Of3_Format", itemName),
        _ => itemName,
    };

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
        icon.Glyph = "";   // FileDownload
        bar.Visibility = Visibility.Visible;
        bar.IsIndeterminate = true;
        status.Text = text;
    }

    private void SetItemDone(FontIcon icon, ProgressBar bar, TextBlock status, string text)
    {
        icon.Glyph = "";   // CheckMark
        bar.IsIndeterminate = false;
        bar.Maximum = 1;
        bar.Value = 1;
        status.Text = text;
    }

    private void SetItemFailed(FontIcon icon, ProgressBar bar, TextBlock status, string text)
    {
        icon.Glyph = "";   // ErrorBadge
        bar.IsIndeterminate = false;
        bar.Value = 0;
        status.Text = text;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)               return $"{bytes} B";
        if (bytes < 1024L * 1024)       return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
