using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;
using Deckle.Shell;

namespace Deckle.Settings.ViewModels;

// ViewModel for GeneralPage — bridges shell-level AppSettings sections
// (Hotkeys, Appearance, Behaviour, Startup, Backup) to the XAML via x:Bind.
// Recording was extracted in slice S3 to RecordingViewModel ; Telemetry
// in slice S2 to DiagnosticsViewModel. Behaviour (auto-paste + overlay)
// was rapatriated here in pass2 — these are user-facing behaviors of the
// app as a whole, not Recording-page-specific settings.
//
// Pattern: Load() pulls from the POCO, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
//
// Partial properties (not fields) for WinRT/AOT compatibility (MVVMTK0045).
public partial class GeneralViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

    // ── Appearance ───────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string Theme { get; set; }

    partial void OnThemeChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Theme ← {value}");
        PushToSettings();
        SettingsHost.ApplyTheme?.Invoke(value);
    }

    // ── Behaviour ────────────────────────────────────────────────────────────
    //
    // Auto-paste : whether the transcript text is pasted into the focused
    // window after copy. Overlay : the on-screen HUD shown during recording
    // (master toggle + fade-on-proximity, animations, position). Both used
    // to live on the Recording page in slice S3 — moved here in pass2
    // because they describe the app's overall behaviour, not the capture
    // pipeline itself.
    //
    // Persistence stays in shell.Paste / shell.Overlay (settings.json).
    // The Recording page no longer reads or writes these.

    [ObservableProperty]
    public partial bool AutoPasteEnabled { get; set; }

    [ObservableProperty]
    public partial bool OverlayEnabled { get; set; }

    [ObservableProperty]
    public partial bool OverlayFadeOnProximity { get; set; }

    [ObservableProperty]
    public partial bool OverlayAnimations { get; set; }

    [ObservableProperty]
    public partial string OverlayPosition { get; set; }

    partial void OnAutoPasteEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Auto-paste ← {value}");
        PushToSettings();
    }

    partial void OnOverlayEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay enabled ← {value}");
        PushToSettings();
    }

    partial void OnOverlayFadeOnProximityChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay fade ← {value}");
        PushToSettings();
    }

    partial void OnOverlayAnimationsChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay animations ← {value}");
        PushToSettings();
    }

    partial void OnOverlayPositionChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Overlay position ← {value}");
        PushToSettings();
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    // AutostartEnabled is not backed by AppSettings — the source of truth is
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Deckle. Load() reads
    // the registry, OnAutostartEnabledChanged writes it. If the write fails,
    // we revert the UI state so the toggle stays consistent with reality.
    [ObservableProperty]
    public partial bool AutostartEnabled { get; set; }

    [ObservableProperty]
    public partial bool WarmupOnLaunch { get; set; }

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        bool ok = value ? AutostartService.Enable() : AutostartService.Disable();
        if (ok)
        {
            _log.Info(LogSource.SetGeneral, $"Start with Windows ← {value}");
            return;
        }

        // Write refused (GPO, ACL, missing ProcessPath…) — revert the toggle
        // so what the user sees matches what's actually in the registry.
        _isSyncing = true;
        try { AutostartEnabled = !value; }
        finally { _isSyncing = false; }
    }

    partial void OnWarmupOnLaunchChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Warmup on launch ← {value}");
        PushToSettings();
    }

    // ── Backup ───────────────────────────────────────────────────────────────
    //
    // BackupDirectory is the user override for where snapshots live (empty =
    // AppPaths.SettingsBackupDirectory, see SettingsService.ResolveBackupDirectory).
    // Backups is the live list refilled by RefreshBackups() — called on Load,
    // after CreateBackup, and any time BackupDirectory changes. The PowerToys-
    // style UI only surfaces the latest snapshot (file name + created at);
    // older snapshots remain on disk for manual access via the folder picker.

    [ObservableProperty]
    public partial string BackupDirectory { get; set; }

    public ObservableCollection<BackupInfo> Backups { get; } = new();

    public BackupInfo? LatestBackup => Backups.Count > 0 ? Backups[0] : null;

    public bool HasBackup => LatestBackup is not null;

    public string LatestBackupFileName => LatestBackup is null
        ? "—"
        : Path.GetFileName(LatestBackup.Path);

    public string LatestBackupCreatedAt => LatestBackup is null
        ? "No backup yet"
        : LatestBackup.Timestamp.LocalDateTime.ToString("g");

    partial void OnBackupDirectoryChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetGeneral, $"Paths.BackupDirectory ← \"{value}\"");
        PushToSettings();
        RefreshBackups();
    }

    public void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in SettingsBackupService.ListBackups())
            Backups.Add(b);

        OnPropertyChanged(nameof(LatestBackup));
        OnPropertyChanged(nameof(HasBackup));
        OnPropertyChanged(nameof(LatestBackupFileName));
        OnPropertyChanged(nameof(LatestBackupCreatedAt));
    }

    // ── Sync with SettingsService ────────────────────────────────────────────

    public GeneralViewModel()
    {
        _isSyncing = true;

        Theme = "System";
        AutoPasteEnabled = false;
        OverlayEnabled = true;
        OverlayFadeOnProximity = true;
        OverlayAnimations = true;
        OverlayPosition = "BottomCenter";
        AutostartEnabled = false;
        WarmupOnLaunch = true;
        BackupDirectory = "";

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var shell = SettingsService.Instance.Current;
            Theme = shell.Appearance.Theme;
            AutoPasteEnabled = shell.Paste.AutoPasteEnabled;
            OverlayEnabled = shell.Overlay.Enabled;
            OverlayFadeOnProximity = shell.Overlay.FadeOnProximity;
            OverlayAnimations = shell.Overlay.Animations;
            OverlayPosition = shell.Overlay.Position;
            AutostartEnabled = AutostartService.IsEnabled();
            WarmupOnLaunch = shell.Startup.WarmupOnLaunch;
            BackupDirectory = shell.Paths.BackupDirectory;
        }
        finally
        {
            _isSyncing = false;
        }

        // Refresh outside the _isSyncing guard so any future logic in
        // RefreshBackups that touches observable state behaves normally.
        RefreshBackups();
    }

    private void PushToSettings()
    {
        var shell = SettingsService.Instance.Current;
        shell.Appearance.Theme = Theme;
        shell.Paste.AutoPasteEnabled = AutoPasteEnabled;
        shell.Overlay.Enabled = OverlayEnabled;
        shell.Overlay.FadeOnProximity = OverlayFadeOnProximity;
        shell.Overlay.Animations = OverlayAnimations;
        shell.Overlay.Position = OverlayPosition;
        shell.Startup.WarmupOnLaunch = WarmupOnLaunch;
        shell.Paths.BackupDirectory = BackupDirectory ?? "";
        SettingsService.Instance.Save();
    }

    // ── Reset per section ───────────────────────────────────────────────────

    public void ResetAppearanceDefaults()
    {
        _isSyncing = true;
        try { Theme = "System"; }
        finally { _isSyncing = false; }
        PushToSettings();
        SettingsHost.ApplyTheme?.Invoke(Theme);
        _log.Info(LogSource.SetGeneral, "Appearance section reset to defaults");
    }

    public void ResetBehaviourDefaults()
    {
        _isSyncing = true;
        try
        {
            AutoPasteEnabled = false;
            OverlayEnabled = true;
            OverlayFadeOnProximity = true;
            OverlayAnimations = true;
            OverlayPosition = "BottomCenter";
        }
        finally { _isSyncing = false; }
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Behaviour section reset to defaults");
    }

    public void ResetStartupDefaults()
    {
        // Autostart lives in the registry — AutostartService handles the write
        // and returns false when the write is refused (GPO, ACL…). Mirror the
        // actual registry state back into the VM so the toggle matches reality.
        AutostartService.Disable();

        _isSyncing = true;
        try
        {
            AutostartEnabled = AutostartService.IsEnabled();
            WarmupOnLaunch = true;
        }
        finally { _isSyncing = false; }
        PushToSettings();
        _log.Info(LogSource.SetGeneral, "Startup section reset to defaults");
    }
}
