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
// Extracted from GeneralPage in slice S3. In pass2 the Behaviour
// settings (auto-paste + overlay HUD) were moved back to GeneralPage —
// what remains is the capture pipeline itself : microphone device
// selection (Win32 waveIn enumeration) and voice level window
// calibration (sliders + auto-calibration toggle). Same patterns as
// GeneralPage / DiagnosticsPage : NavigationCacheMode.Required and
// the _initializing guard around the initial sync pass.
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

    private void ResetRecording_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetRecordingDefaults();
        _initializing = true;
        try
        {
            AudioInputCombo.SelectedIndex = 0;
        }
        finally { _initializing = false; }
    }
}
