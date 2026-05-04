using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Settings.ViewModels;

namespace Deckle.Settings;

public sealed partial class GeneralPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public GeneralViewModel ViewModel { get; } = new();

    // Guards combo SelectionChanged during initial sync — the ThemeCombo
    // handler sets a VM property which would trigger PushToSettings().
    private bool _initializing;

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
        SyncThemeCombo();
        SyncOverlayPositionCombo();
        SyncFolderPickerDefaults();
        DataFolderPathText.Text = AppPaths.UserDataRoot;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => _initializing = false);
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

    // ── Overlay position ─────────────────────────────────────────────────────
    //
    // ComboBoxItem avec Tag — pas bindable en TwoWay, conversion manuelle.
    // Older settings.json may carry corner values (TopLeft / BottomRight…)
    // that the combo no longer exposes — Sync normalizes them to
    // TopCenter / BottomCenter on Load.

    private void SyncOverlayPositionCombo()
    {
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

    // ── Folder picker defaults ───────────────────────────────────────────────

    private void SyncFolderPickerDefaults()
    {
        BackupFolderPicker.DefaultPath = AppPaths.SettingsBackupDirectory;
    }

    // ── Reset per section ───────────────────────────────────────────────────

    private void ResetStartup_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetStartupDefaults();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetAppearanceDefaults();
        _initializing = true;
        try { SyncThemeCombo(); }
        finally { _initializing = false; }
    }

    private void ResetBehaviour_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetBehaviourDefaults();
        _initializing = true;
        try { SyncOverlayPositionCombo(); }
        finally { _initializing = false; }
    }

    // Opens the UserDataRoot in File Explorer — entry point for users who
    // want to inspect, back up, or wipe everything mutable the app stores.
    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = AppPaths.UserDataRoot;

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
                $"Open data folder failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Re-opens the first-run wizard on demand. Used to swap the Whisper
    // model (Browse + download) or replace the native runtime without
    // wiping <UserDataRoot>. The wizard runs detached from the Settings
    // window — Settings stays open behind it.
    private void ReRunSetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The first-run wizard lives in Deckle.Shell.Setup (App-side
            // until factored into a dedicated module). Going through the
            // SettingsHost hook keeps Deckle.Settings free of a back
            // reference to Deckle.exe.
            if (SettingsHost.OpenSetupWizard is null)
            {
                _log.Warning(LogSource.SetGeneral, "setup wizard hook not wired — ignoring");
                return;
            }
            SettingsHost.OpenSetupWizard.Invoke();
            _log.Info(LogSource.SetGeneral, "setup window opened from Settings");
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetGeneral,
                $"open setup window failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Backup ──────────────────────────────────────────────────────────────
    //
    // PowerToys-style: a single SettingsExpander, two header actions
    // (Back up / Restore), and a folder picker for the location. Restore
    // targets the latest snapshot — older ones live in the folder and are
    // restorable by hand if ever needed. The folder picker is a
    // FolderPickerCard bound to ViewModel.BackupDirectory; its DefaultPath
    // is wired in SyncFolderPickerDefaults to AppPaths.SettingsBackupDirectory.

    private void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsBackupService.CreateBackup();
        ViewModel.RefreshBackups();
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var latest = ViewModel.LatestBackup;
        if (latest is null)
        {
            _log.Warning(LogSource.SetGeneral,
                "restore skipped | reason=no_backup");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = Loc.Get("Settings_RestoreDialog_Title"),
            Content = Loc.Format("Settings_RestoreDialog_Content_Format", latest.DisplayName),
            PrimaryButtonText = Loc.Get("Settings_RestoreDialog_PrimaryButton"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        bool ok = SettingsBackupService.RestoreFromBackup(latest.Path);
        if (!ok) return;

        // Settings have been replaced and SettingsService.Reload has fired
        // Changed. Refill the VM from the new in-memory snapshot. Other
        // pages (RecordingPage, DiagnosticsPage, WhisperPage) will refill
        // their own VMs on next OnNavigatedTo via NavigationCacheMode.
        _initializing = true;
        try
        {
            ViewModel.Load();
            SyncThemeCombo();
            SyncOverlayPositionCombo();
            SyncFolderPickerDefaults();

            // Apply theme side-effect beyond the VM — RecordingViewModel
            // owns the level-window mapper push so we don't touch it from
            // here anymore.
            SettingsHost.ApplyTheme?.Invoke(ViewModel.Theme);
        }
        finally
        {
            _initializing = false;
        }
    }
}
