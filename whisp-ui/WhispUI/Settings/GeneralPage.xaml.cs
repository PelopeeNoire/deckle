using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WhispUI.Settings;

public sealed partial class GeneralPage : Page
{
    private bool _loading;

    public GeneralPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        _loading = true;
        try
        {
            PopulateAudioInputDevices();
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Microphone source ────────────────────────────────────────────────────
    //
    // Énumère les périphériques waveIn via l'API Win32, peuple le ComboBox
    // avec "System default" en premier puis chaque device par nom.
    // L'index stocké dans AppSettings.Recording.AudioInputDeviceId est -1
    // pour WAVE_MAPPER (défaut) ou l'index waveIn (0-based).

    private void PopulateAudioInputDevices()
    {
        AudioInputCombo.Items.Clear();
        AudioInputCombo.Items.Add("System default");

        uint numDevs = NativeMethods.waveInGetNumDevs();
        for (uint i = 0; i < numDevs; i++)
        {
            var caps = new NativeMethods.WAVEINCAPSW();
            uint err = NativeMethods.waveInGetDevCapsW(i, ref caps, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WAVEINCAPSW>());
            string name = err == 0 ? caps.szPname : $"Device {i}";
            AudioInputCombo.Items.Add(name);
        }

        int configuredId = SettingsService.Instance.Current.Recording.AudioInputDeviceId;
        // configuredId = -1 → index 0 ("System default"), sinon +1 (offset du premier item)
        int comboIndex = configuredId < 0 ? 0 : configuredId + 1;
        if (comboIndex >= AudioInputCombo.Items.Count)
            comboIndex = 0; // device disparu → retour au défaut
        AudioInputCombo.SelectedIndex = comboIndex;
    }

    private void AudioInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int comboIndex = AudioInputCombo.SelectedIndex;
        // index 0 = "System default" → deviceId -1 ; sinon index - 1
        int deviceId = comboIndex <= 0 ? -1 : comboIndex - 1;

        App.Log?.Log($"[GENERAL] Audio input device ← {deviceId} ({AudioInputCombo.SelectedItem})");
        SettingsService.Instance.Current.Recording.AudioInputDeviceId = deviceId;
        SettingsService.Instance.Save();
    }
}
