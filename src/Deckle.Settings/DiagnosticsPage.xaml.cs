using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Logging;
using Deckle.Settings.ViewModels;

namespace Deckle.Settings;

// ── DiagnosticsPage ─────────────────────────────────────────────────────────
//
// Extracted from GeneralPage in slice S2. Hosts the telemetry opt-ins
// (Application log, Microphone, Latency, Corpus + Audio corpus, Storage
// folder) that previously crowded GeneralPage's bottom half. Same
// patterns as GeneralPage : NavigationCacheMode.Required, _initializing
// guard around the initial sync pass, per-toggle consent flow with
// _suppress* re-entry guards.
public sealed partial class DiagnosticsPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public DiagnosticsViewModel ViewModel { get; } = new();

    private bool _initializing;

    // Re-entry guards for the consent flows : the Toggled handler reverts
    // the switch when the user cancels the dialog, and that revert would
    // retrigger Toggled in turn.
    private bool _suppressCorpusToggle;
    private bool _suppressAudioCorpusToggle;
    private bool _suppressApplicationLogToggle;
    private bool _suppressMicrophoneTelemetryToggle;

    public DiagnosticsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        LoadAndSync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadAndSync();
    }

    private void LoadAndSync()
    {
        _initializing = true;
        ViewModel.Load();
        SyncFolderPickerDefault();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => _initializing = false);
    }

    // FolderPickerCard.DefaultPath drives the read-only display when
    // TelemetryStorageDirectory is empty + the Open button's target.
    // Resolves to <UserDataRoot>\telemetry\ — what
    // CorpusPaths.GetDefaultDirectoryPath returns, which equals
    // AppPaths.TelemetryDirectory.
    private void SyncFolderPickerDefault()
    {
        TelemetryFolderPicker.DefaultPath = AppPaths.TelemetryDirectory;
    }

    // ── Consent flows ───────────────────────────────────────────────────────
    //
    // Off → On : show a consent dialog. Cancel reverts the toggle (guarded
    // via _suppress*Toggle to avoid re-entering this handler during the
    // revert). On → Off : no confirmation — the user can turn it back on
    // later if needed.

    private async void ApplicationLogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressApplicationLogToggle) return;
        if (!ApplicationLogToggle.IsOn) return;

        bool confirmed = await ApplicationLogConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressApplicationLogToggle = true;
        try { ApplicationLogToggle.IsOn = false; }
        finally { _suppressApplicationLogToggle = false; }
    }

    private async void MicrophoneTelemetryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressMicrophoneTelemetryToggle) return;
        if (!MicrophoneTelemetryToggle.IsOn) return;

        bool confirmed = await MicrophoneTelemetryConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressMicrophoneTelemetryToggle = true;
        try { MicrophoneTelemetryToggle.IsOn = false; }
        finally { _suppressMicrophoneTelemetryToggle = false; }
    }

    private async void CorpusLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressCorpusToggle) return;
        if (!CorpusLoggingToggle.IsOn) return;

        bool confirmed = await CorpusConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressCorpusToggle = true;
        try { CorpusLoggingToggle.IsOn = false; }
        finally { _suppressCorpusToggle = false; }
    }

    private async void AudioCorpusToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressAudioCorpusToggle) return;
        if (!AudioCorpusToggle.IsOn) return;

        bool confirmed = await AudioCorpusConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressAudioCorpusToggle = true;
        try { AudioCorpusToggle.IsOn = false; }
        finally { _suppressAudioCorpusToggle = false; }
    }

    private void ResetTelemetry_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetTelemetryDefaults();
    }
}
