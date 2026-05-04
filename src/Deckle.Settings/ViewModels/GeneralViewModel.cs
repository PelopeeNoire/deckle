using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;
using Deckle.Shell;

namespace Deckle.Settings.ViewModels;

// ViewModel for GeneralPage — bridges shell-level AppSettings sections
// (Startup, Appearance, Backup) to the XAML via x:Bind. Recording was
// extracted in slice S3 to RecordingViewModel ; Telemetry in slice S2 to
// DiagnosticsViewModel. What remains is the cross-cutting shell config
// (theme + autostart + warmup + backup directory).
//
// Pattern: Load() pulls from the POCO, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
//
// Partial properties (not fields) for WinRT/AOT compatibility (MVVMTK0045).
public partial class GeneralViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

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

        AutostartEnabled = false;
        WarmupOnLaunch = true;
        Theme = "System";
        BackupDirectory = "";

        // _isSyncing stays true — Load() will set it to false.
    }

    public void Load()
    {
        _isSyncing = true;
        try
        {
            var shell = SettingsService.Instance.Current;
            AutostartEnabled = AutostartService.IsEnabled();
            WarmupOnLaunch = shell.Startup.WarmupOnLaunch;
            Theme = shell.Appearance.Theme;
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
        shell.Startup.WarmupOnLaunch = WarmupOnLaunch;
        shell.Appearance.Theme = Theme;
        shell.Paths.BackupDirectory = BackupDirectory ?? "";
        SettingsService.Instance.Save();
    }

    // ── Reset per section ───────────────────────────────────────────────────

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

    public void ResetAppearanceDefaults()
    {
        _isSyncing = true;
        try { Theme = "System"; }
        finally { _isSyncing = false; }
        PushToSettings();
        SettingsHost.ApplyTheme?.Invoke(Theme);
        _log.Info(LogSource.SetGeneral, "Appearance section reset to defaults");
    }
}
