using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Interop;
using WhispUI.Settings.ViewModels;

namespace WhispUI.Settings;

public sealed partial class GeneralPage : Page
{
    public GeneralViewModel ViewModel { get; } = new();

    // Guards combo SelectionChanged during initial sync — these handlers
    // set VM properties which would trigger PushToSettings() needlessly.
    private bool _initializing;

    public GeneralPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        _initializing = true;
        ViewModel.Load();
        PopulateAudioInputDevices();
        SyncOverlayPositionCombo();
        SyncThemeCombo();
        _initializing = false;
    }

    // NavigationCacheMode.Required reuses the page instance — the constructor
    // and Loaded only fire once. Without this override, navigating away then
    // back would show stale values, and PushToSettings() (which writes ALL VM
    // properties) would silently overwrite any changes made from another page.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initializing = true;
        ViewModel.Load();
        PopulateAudioInputDevices();
        SyncOverlayPositionCombo();
        SyncThemeCombo();
        _initializing = false;
    }

    // ── Audio input ──────────────────────────────────────────────────────────
    //
    // Peuplé dynamiquement via Win32 waveIn — reste dans le code-behind car
    // c'est de l'énumération hardware, pas du setting. Le combo a "System
    // default" en index 0, donc comboIndex ↔ deviceId nécessite une conversion.

    private void PopulateAudioInputDevices()
    {
        AudioInputCombo.Items.Clear();
        AudioInputCombo.Items.Add("System default");

        uint numDevs = NativeMethods.waveInGetNumDevs();
        for (uint i = 0; i < numDevs; i++)
        {
            var caps = new NativeMethods.WAVEINCAPSW();
            uint err = NativeMethods.waveInGetDevCapsW(i, ref caps,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WAVEINCAPSW>());
            string name = err == 0 ? caps.szPname : $"Device {i}";
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

    // ── Theme ────────────────────────────────────────────────────────────────

    private void SyncThemeCombo()
    {
        for (int i = 0; i < ThemeCombo.Items.Count; i++)
        {
            if (ThemeCombo.Items[i] is ComboBoxItem item &&
                item.Tag as string == ViewModel.Theme)
            {
                ThemeCombo.SelectedIndex = i;
                return;
            }
        }
        ThemeCombo.SelectedIndex = 0;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string theme)
        {
            ViewModel.Theme = theme;
        }
    }
}
