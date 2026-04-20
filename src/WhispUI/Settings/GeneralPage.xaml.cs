using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Interop;
using WhispUI.Logging;
using WhispUI.Settings.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WhispUI.Settings;

public sealed partial class GeneralPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public GeneralViewModel ViewModel { get; } = new();

    // Guards combo SelectionChanged during initial sync — these handlers
    // set VM properties which would trigger PushToSettings() needlessly.
    private bool _initializing;

    // Re-entry guard for the corpus consent flow: the Toggled handler reverts
    // the switch when the user cancels the dialog, and that revert would
    // retrigger Toggled in turn.
    private bool _suppressCorpusToggle;
    private bool _suppressAudioCorpusToggle;

    public GeneralPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        LoadAndSync();
    }

    // NavigationCacheMode.Required reuses the page instance — the constructor
    // and Loaded only fire once. Without this override, navigating away then
    // back would show stale values, and PushToSettings() (which writes ALL VM
    // properties) would silently overwrite any changes made from another page.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadAndSync();
    }

    // x:Bind TwoWay bindings apply their initial value to the visual tree
    // during the first layout pass — AFTER the ctor returns. That causes
    // ToggleSwitch.Toggled to fire unsynchronously for the seed value, so
    // a simple `_initializing = false` at the end of this method would come
    // too early and let the handler think the user flipped the switch.
    // Deferring the flag release via DispatcherQueue priority Low pushes it
    // past the layout pass, after all initial bindings have settled.
    private void LoadAndSync()
    {
        _initializing = true;
        ViewModel.Load();
        PopulateAudioInputDevices();
        SyncOverlayPositionCombo();
        SyncThemeCombo();
        SyncCorpusFolderPlaceholder();
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

    // ── Corpus storage folder ────────────────────────────────────────────────
    //
    // Show the default resolver's path as the TextBox placeholder rather than
    // a generic "(auto)" — lets the user see where logs will land without
    // opening File Explorer. Empty fallback "(auto)" only when the dev layout
    // resolver can't find a benchmark/ folder.

    private void SyncCorpusFolderPlaceholder()
    {
        string? defaultPath = CorpusLog.GetDefaultDirectoryPath();
        CorpusFolderBox.PlaceholderText = string.IsNullOrEmpty(defaultPath) ? "(auto)" : defaultPath;
    }

    // ── Corpus logging handlers ─────────────────────────────────────────────
    //
    // Off → On: show a consent dialog. Cancel reverts the toggle (guarded
    // via _suppressCorpusToggle to avoid re-entering this handler during
    // the revert). On → Off: no confirmation — the user can turn it back on
    // later if needed.

    private async void CorpusLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressCorpusToggle) return;
        if (!CorpusLoggingToggle.IsOn) return;

        bool confirmed = await CorpusConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressCorpusToggle = true;
        try
        {
            CorpusLoggingToggle.IsOn = false;
        }
        finally
        {
            _suppressCorpusToggle = false;
        }
    }

    // Audio corpus consent — separate from the text corpus dialog because
    // voice recordings carry a different privacy posture. The toggle is
    // guarded by IsEnabled={CorpusLoggingEnabled} in XAML so it can't reach
    // the on state while the master text toggle is off.
    private async void AudioCorpusToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressAudioCorpusToggle) return;
        if (!AudioCorpusToggle.IsOn) return;

        bool confirmed = await AudioCorpusConsentDialog.ShowAsync(this.XamlRoot);
        if (confirmed) return;

        _suppressAudioCorpusToggle = true;
        try
        {
            AudioCorpusToggle.IsOn = false;
        }
        finally
        {
            _suppressAudioCorpusToggle = false;
        }
    }

    // FolderPicker is a WinRT API designed for packaged apps — in an
    // unpackaged WinUI 3 host it needs the parent HWND wired via
    // WinRT.Interop or ShowAsync throws E_INVALIDARG (COMException 0x80070057).
    private async void ChangeCorpusFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            var settingsWin = App.SettingsWin
                ?? throw new InvalidOperationException("Settings window not initialized");
            var hwnd = WindowNative.GetWindowHandle(settingsWin);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            ViewModel.CorpusDataDirectory = folder.Path;
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"Change corpus folder failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OpenCorpusFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // GetDirectoryPath() already falls back to the default resolver when
        // CorpusLogging.DataDirectory is empty.
        string? path = CorpusLog.GetDirectoryPath();

        if (string.IsNullOrEmpty(path))
        {
            _log.Warning(LogSource.SetGeneral, "Corpus folder unresolved — cannot open");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"Open corpus folder failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
