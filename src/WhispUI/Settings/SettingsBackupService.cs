using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ── SettingsBackupService ──────────────────────────────────────────────────
//
// PowerToys-style backup & restore for the live settings.json.
//
// What it does:
//   • CreateBackup()       — copies the current settings.json into the
//                            backup directory under a timestamped name
//                            (settings-YYYYMMDD-HHmmss.json).
//   • ListBackups()        — enumerates existing snapshots, newest first.
//   • RestoreFromBackup(p) — overwrites settings.json with the snapshot,
//                            then asks SettingsService to Reload so every
//                            subscribed page refreshes.
//
// Where snapshots live:
//   • Default: <AppPaths.ConfigDirectory>/backups/. Created on first write.
//   • Override: PathsSettings.BackupDirectory (absolute). Pattern PowerToys —
//     point it at OneDrive / Drive to carry settings across machines.
//
// Format: a flat .json copy, byte-identical to the live file at backup time.
// No zip, no archive — settings.json is small (a few KB) and a per-snapshot
// flat file stays inspectable from the file manager and from a text editor.
// The user can prune by deleting files; we never auto-delete.
//
// Concurrency: backups are user-initiated UI actions (button clicks). We
// don't try to defend against concurrent CreateBackup calls — the timestamp
// has millisecond precision, so collisions would require user-impossibly
// fast clicking. Restore takes the SettingsService write path so the live
// file mutation is atomic.
//
// Fail-soft: every public method returns a result type or false on error and
// logs at Warning. The UI surfaces failures via overlay feedback; nothing
// here throws into the caller.
internal static class SettingsBackupService
{
    private const string FilenamePrefix = "settings-";
    private const string FilenameExtension = ".json";
    private const string FilenameTimestampFormat = "yyyyMMdd-HHmmss";

    // Returns the backup directory path (resolved by SettingsService). May
    // not exist yet — callers that write to it (CreateBackup) create it
    // on demand.
    public static string GetDirectory() => SettingsService.Instance.ResolveBackupDirectory();

    // Snapshot the current settings.json into the backup directory. Returns
    // the BackupInfo on success, null on any failure (logged Warning).
    public static BackupInfo? CreateBackup()
    {
        try
        {
            // Flush any pending debounced Save first — without this a
            // backup taken right after a slider change would capture the
            // pre-change state still on disk. Cheap and sync.
            SettingsService.Instance.Flush();

            string source = SettingsService.Instance.ConfigPath;
            if (!File.Exists(source))
            {
                LogService.Instance.Warning(LogSource.Settings,
                    $"backup skipped — source missing | path={source}");
                return null;
            }

            string dir = GetDirectory();
            Directory.CreateDirectory(dir);

            DateTimeOffset stampNow = DateTimeOffset.Now;
            string filename = FilenamePrefix
                + stampNow.ToString(FilenameTimestampFormat, CultureInfo.InvariantCulture)
                + FilenameExtension;
            string destination = Path.Combine(dir, filename);

            File.Copy(source, destination, overwrite: false);

            LogService.Instance.Info(LogSource.Settings,
                $"backup created | path={destination}");
            return new BackupInfo(destination, stampNow);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Settings,
                $"backup failed | error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Enumerate snapshots in the backup directory, newest first. Empty list
    // when the directory is missing or unreadable — callers render an empty
    // state without surfacing the error.
    public static IReadOnlyList<BackupInfo> ListBackups()
    {
        try
        {
            string dir = GetDirectory();
            if (!Directory.Exists(dir))
                return Array.Empty<BackupInfo>();

            var items = new List<BackupInfo>();
            foreach (string path in Directory.EnumerateFiles(dir, FilenamePrefix + "*" + FilenameExtension))
            {
                if (TryParseTimestamp(path, out DateTimeOffset stamp))
                    items.Add(new BackupInfo(path, stamp));
                else
                    // File name doesn't match the timestamp pattern (a copy
                    // pasted in by hand?). Fall back to the file's last
                    // write time so we still surface it to the user.
                    items.Add(new BackupInfo(path, new DateTimeOffset(File.GetLastWriteTime(path))));
            }

            return items
                .OrderByDescending(b => b.Timestamp)
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning(LogSource.Settings,
                $"backup list failed | error={ex.GetType().Name}: {ex.Message}");
            return Array.Empty<BackupInfo>();
        }
    }

    // Replace the live settings.json with the snapshot at `backupPath` and
    // reload SettingsService. Returns true on success. The caller (UI)
    // is responsible for showing a confirmation dialog beforehand.
    public static bool RestoreFromBackup(string backupPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            {
                LogService.Instance.Warning(LogSource.Settings,
                    $"restore skipped — snapshot missing | path={backupPath ?? "(null)"}");
                return false;
            }

            string destination = SettingsService.Instance.ConfigPath;
            string destinationDir = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(destinationDir);

            // Atomic-ish: copy to a sibling temp file then move over the
            // live file. Mirrors the SettingsService.Flush write pattern
            // so an interrupted restore can't leave a half-written
            // settings.json on disk.
            string tmp = destination + ".restore.tmp";
            File.Copy(backupPath, tmp, overwrite: true);
            File.Move(tmp, destination, overwrite: true);

            SettingsService.Instance.Reload();

            LogService.Instance.Info(LogSource.Settings,
                $"restored from backup | path={backupPath}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(LogSource.Settings,
                $"restore failed | error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseTimestamp(string fullPath, out DateTimeOffset stamp)
    {
        stamp = default;
        string name = Path.GetFileNameWithoutExtension(fullPath);
        if (!name.StartsWith(FilenamePrefix, StringComparison.Ordinal)) return false;

        string token = name.Substring(FilenamePrefix.Length);
        if (DateTime.TryParseExact(token,
                FilenameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime parsed))
        {
            stamp = new DateTimeOffset(parsed);
            return true;
        }
        return false;
    }
}

// One snapshot entry. Path is absolute. Timestamp is local time when the
// snapshot was taken (parsed back from the filename, falls back to the
// file's last-write-time when the name doesn't match the pattern).
// DisplayName is the user-locale formatting used as ComboBox label —
// computed once on read so the binding stays simple (no value converter).
public sealed record BackupInfo(string Path, DateTimeOffset Timestamp)
{
    public string DisplayName => Timestamp.LocalDateTime.ToString("g");
}
