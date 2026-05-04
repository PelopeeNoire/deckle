using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Interop;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Settings.ViewModels;

namespace Deckle.Settings;

// ── RecordingPage ───────────────────────────────────────────────────────────
//
// Extracted from GeneralPage in slice S3. Owns everything around the
// capture pipeline : microphone device selection (Win32 waveIn
// enumeration), auto-paste, voice level window calibration (sliders +
// auto-calibration toggle), overlay HUD configuration. Same patterns
// as GeneralPage / DiagnosticsPage : NavigationCacheMode.Required,
// _initializing guard around the initial sync pass.
public sealed partial class RecordingPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public RecordingViewModel ViewModel { get; } = new();

    private bool _initializing;

    public RecordingPage()
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
        PopulateAudioInputDevices();
        SyncOverlayPositionCombo();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => _initializing = false);
    }

    // ── Audio input ──────────────────────────────────────────────────────────
    //
    // Peuplé dynamiquement via Win32 waveIn — reste dans le code-behind car
    // c'est de l'énumération hardware, pas du setting. Le combo a "System
    // default" en index 0, donc comboIndex ↔ deviceId nécessite une conversion.

    private void PopulateAudioInputDevices()
    {
        AudioInputCombo.Items.Clear();
        AudioInputCombo.Items.Add(Loc.Get("Settings_AudioInput_SystemDefault"));

        uint numDevs = NativeMethods.waveInGetNumDevs();
        for (uint i = 0; i < numDevs; i++)
        {
            var caps = new NativeMethods.WAVEINCAPSW();
            uint err = NativeMethods.waveInGetDevCapsW(i, ref caps,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WAVEINCAPSW>());
            string name = err == 0 ? caps.szPname : Loc.Format("Settings_AudioInput_Device_Format", i);
            AudioInputCombo.Items.Add(name);
        }

        int deviceId = ViewModel.AudioInputDeviceId;
        int comboIndex = deviceId < 0 ? 0 : deviceId + 1;
        if (comboIndex >= AudioInputCombo.Items.Count)
            comboIndex = 0;
        AudioInputCombo.SelectedIndex = comboIndex;
    }

    private void AudioInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || AudioInputCombo.SelectedIndex < 0) return;
        int deviceId = AudioInputCombo.SelectedIndex <= 0 ? -1 : AudioInputCombo.SelectedIndex - 1;
        ViewModel.AudioInputDeviceId = deviceId;
    }

    // ── Overlay position ─────────────────────────────────────────────────────
    //
    // ComboBoxItem avec Tag — pas bindable en TwoWay, conversion manuelle.

    private void SyncOverlayPositionCombo()
    {
        // Normalize legacy corner values (TopLeft/TopRight/BottomLeft/BottomRight)
        // from older settings.json to their Top/Bottom equivalent — the combo now
        // only exposes centered positions.
        string current = ViewModel.OverlayPosition ?? "BottomCenter";
        string normalized = current.StartsWith("Top") ? "TopCenter" : "BottomCenter";
        if (normalized != current)
            ViewModel.OverlayPosition = normalized;

        for (int i = 0; i < OverlayPositionCombo.Items.Count; i++)
        {
            if (OverlayPositionCombo.Items[i] is ComboBoxItem item &&
                item.Tag as string == normalized)
            {
                OverlayPositionCombo.SelectedIndex = i;
                return;
            }
        }
        OverlayPositionCombo.SelectedIndex = 0;
    }

    private void OverlayPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (OverlayPositionCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string position)
        {
            ViewModel.OverlayPosition = position;
        }
    }

    private void ResetRecording_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetRecordingDefaults();
        _initializing = true;
        try
        {
            AudioInputCombo.SelectedIndex = 0;
            SyncOverlayPositionCombo();
        }
        finally { _initializing = false; }
    }
}
