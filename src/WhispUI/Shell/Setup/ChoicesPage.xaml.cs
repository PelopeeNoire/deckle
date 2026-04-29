using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WhispUI.Logging;
using WhispUI.Setup;

namespace WhispUI.Shell.Setup;

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
            "Setup",
            "Tell the app where to install everything, and pick the speech model you want.");
        setup.SetBackEnabled(false);
        setup.SetNextLabel("Install");
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
            NativeStatusText.Text = $"Could not import: {ex.Message}";
        }
    }

    private void RefreshNativeStatus()
    {
        if (NativeRuntime.IsInstalled())
        {
            NativeStatusText.Text = "Installed";
            BrowseNativeButton.Content = "Replace...";
        }
        else
        {
            int missing = NativeRuntime.GetMissing().Count;
            NativeStatusText.Text = $"Missing {missing} file(s)";
            BrowseNativeButton.Content = "Browse...";
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
        long totalBytes = _context.SelectedModel.SizeBytes + SpeechModels.VadModel.SizeBytes;
        TotalEstimateBar.Message =
            $"~{FormatBytes(totalBytes)} to download for the model and VAD. " +
            $"Native runtime is {(NativeRuntime.IsInstalled() ? "already installed." : "not yet installed.")}";
    }

    private void UpdateNextEnabled()
    {
        if (_setup is null) return;
        bool ready = NativeRuntime.IsInstalled() && ModelRadio.SelectedIndex >= 0;
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
