using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Setup;

namespace Deckle.Shell.Setup;

// ── ChoicesPage ──────────────────────────────────────────────────────────────
//
// First step of the wizard. Collects every user choice up-front so the
// install step can run unattended afterwards:
//
//   • Install location — read-only display in V1 (default UserDataRoot).
//     Custom location lands in a later iteration.
//   • Speech runtime  — Browse for a folder containing the whisper DLLs.
//     Copy is immediate (NativeRuntime.CopyFromFolder), so the install
//     step has nothing native left to do.
//   • Speech model    — radio between the catalog's Whisper models.
//
// The Install button is enabled only when both the runtime is installed
// and a model is selected. Clicking Install navigates to InstallingPage
// (B.4), which downloads the chosen model + Silero VAD.
internal sealed partial class ChoicesPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    private SetupWindow? _setup;
    private SetupContext? _context;

    public ChoicesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SetupWindow setup) return;

        _setup   = setup;
        _context = setup.Context;

        setup.SetStepHeader(
            Loc.Get("Setup_StepTitle_Choices"),
            Loc.Get("Setup_StepSubtitle_Choices"));
        setup.SetBackEnabled(false);
        setup.SetNextLabel(Loc.Get("Setup_NextLabel_Install"));
        setup.SetNextVisible(true);
        setup.SetCancelVisible(true);
        setup.NextRequested += OnNextRequested;

        LocationPathText.Text = _context.Location;
        PopulateModelRadio();
        RefreshNativeStatus();
        UpdateTotalEstimate();
        UpdateNextEnabled();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_setup is not null) _setup.NextRequested -= OnNextRequested;
    }

    // ── Speech runtime ────────────────────────────────────────────────────────

    private async void OnBrowseNativeClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_setup is null) return;

        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*"); // required by the API for FolderPicker

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_setup);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            int copied = NativeRuntime.CopyFromFolder(folder.Path);
            _log.Info(LogSource.Setup,
                $"setup native source picked | source={folder.Path} | copied={copied}");

            RefreshNativeStatus();
            UpdateNextEnabled();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Setup,
                $"setup browse native failed: {ex.GetType().Name}: {ex.Message}");
            NativeStatusText.Text = Loc.Format("Setup_Native_ImportFailed_Format", ex.Message);
        }
    }

    private void RefreshNativeStatus()
    {
        if (NativeRuntime.IsInstalled())
        {
            // Already installed — local copy from a previous run, setup-assets.ps1,
            // or a prior Browse... pass. The button lets the user replace it
            // (e.g. after a manual rebuild of whisper.cpp).
            NativeStatusText.Text = Loc.Get("Setup_Native_Installed");
            BrowseNativeButton.Content = Loc.Get("Setup_Native_Replace");
        }
        else if (NativeRuntime.BundleUrlIsPlaceholder)
        {
            // Auto-download disabled (build not yet wired to a published release).
            // Browse... is the only path forward — surface it as a primary action.
            int missing = NativeRuntime.GetMissing().Count;
            NativeStatusText.Text = Loc.Format("Setup_Native_Missing_Format", missing);
            BrowseNativeButton.Content = Loc.Get("Common_Browse");
        }
        else
        {
            // Auto-download available — surface the size so the user understands
            // what's about to happen. Browse... stays as a secondary "I have it
            // locally already" affordance.
            NativeStatusText.Text = Loc.Format(
                "Setup_Native_WillDownload_Format",
                FormatBytes(NativeRuntime.CurrentBundle.SizeBytes));
            BrowseNativeButton.Content = Loc.Get("Setup_Native_BrowseLocal");
        }
    }

    // ── Speech model ──────────────────────────────────────────────────────────

    private void PopulateModelRadio()
    {
        ModelRadio.Items.Clear();

        int defaultIndex = 0;
        for (int i = 0; i < SpeechModels.WhisperModels.Count; i++)
        {
            var entry = SpeechModels.WhisperModels[i];
            ModelRadio.Items.Add(new RadioButton
            {
                Content = entry.DisplayName,
                Tag     = entry.Id,
            });
            if (entry.FileName == SpeechModels.DefaultModelFileName) defaultIndex = i;
        }

        ModelRadio.SelectedIndex = defaultIndex;
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_context is null) return;

        if (ModelRadio.SelectedItem is RadioButton rb && rb.Tag is string id)
        {
            foreach (var entry in SpeechModels.WhisperModels)
            {
                if (entry.Id == id)
                {
                    _context.SelectedModel = entry;
                    break;
                }
            }
        }

        UpdateTotalEstimate();
        UpdateNextEnabled();
    }

    // ── Estimate + Next gate ──────────────────────────────────────────────────

    private void UpdateTotalEstimate()
    {
        if (_context is null) return;

        long modelsBytes = _context.SelectedModel.SizeBytes + SpeechModels.VadModel.SizeBytes;
        bool nativeInstalled  = NativeRuntime.IsInstalled();
        bool autoDownloadable = !nativeInstalled && !NativeRuntime.BundleUrlIsPlaceholder;

        if (autoDownloadable)
        {
            // Auto-download path — fold the bundle size into the total so the
            // user sees a single number for what the install page will fetch.
            long totalBytes = modelsBytes + NativeRuntime.CurrentBundle.SizeBytes;
            TotalEstimateBar.Message = Loc.Format(
                "Setup_TotalEstimate_WithNative_Format",
                FormatBytes(totalBytes));
        }
        else
        {
            // Either already installed (no native traffic) or placeholder URL
            // (the user will Browse... locally). The legacy two-suffix wording
            // covers both, with the suffix telling the truth about native.
            TotalEstimateBar.Message = Loc.Format(
                "Setup_TotalEstimate_Format",
                FormatBytes(modelsBytes),
                Loc.Get(nativeInstalled
                    ? "Setup_TotalEstimate_NativeAlreadyInstalled"
                    : "Setup_TotalEstimate_NativeNotInstalled"));
        }
    }

    private void UpdateNextEnabled()
    {
        if (_setup is null) return;
        // Native runtime gate is conditional on the bundle URL: when auto-DL
        // is wired, the install page handles missing runtime via download.
        // When the URL is still a placeholder, only a manual Browse... can
        // unblock the wizard, so we keep the legacy gate.
        bool nativeReady = NativeRuntime.IsInstalled() || !NativeRuntime.BundleUrlIsPlaceholder;
        bool ready = nativeReady && ModelRadio.SelectedIndex >= 0;
        _setup.SetNextEnabled(ready);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)               return $"{bytes} B";
        if (bytes < 1024L * 1024)       return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
    }

    // ── Next ──────────────────────────────────────────────────────────────────

    private void OnNextRequested()
    {
        if (_setup is null || _context is null) return;
        _context.ChoicesConfirmed = true;
        _log.Info(LogSource.Setup,
            $"setup choices confirmed | location={_context.Location} | model={_context.SelectedModel.Id}");
        _setup.Body.Navigate(typeof(InstallingPage), _setup);
    }
}
