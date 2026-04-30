using System;
using Microsoft.Win32;
using Deckle.Logging;

namespace Deckle.Shell;

// ── AutostartService ─────────────────────────────────────────────────────────
//
// Registre la valeur HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WhispUI
// qui pointe vers l'exe courant. Windows démarre l'exe à la prochaine
// ouverture de session.
//
// Pourquoi la Run key plutôt que schtasks.exe :
//
//   - Pas de UAC prompt : HKCU (current user) est accessible en user
//     standard. Task Scheduler avec /RL HIGHEST exige l'élévation parce que
//     la tâche s'exécute elevated.
//   - WhispUI n'a aucun besoin d'élévation : tray + hotkey global +
//     transcription locale. Élever inutilement réduit la sécurité (BlueHat
//     reports, privilege sprawl).
//   - C'est la primitive que Windows 11 Settings → "Startup apps" expose
//     à l'utilisateur. Toggle aligné avec le modèle mental système.
//   - Aucun conflit avec la tâche planifiée `Whisp` héritée de
//     WhispInteropTest (cf. CLAUDE.md).
//
// Cohabitation multi-install (dev + publish sur la même machine) : la Run
// key porte un nom fixe `WhispUI`, donc une seule install peut être en
// autostart à la fois. `IsEnabled` compare la valeur stockée à
// `Environment.ProcessPath` : chaque install ne voit ON que si elle est
// celle qui pointe dans le registre. `Disable` ne supprime que si la
// valeur appartient à l'exe courant, pour éviter qu'une install en
// désactive une autre. `Enable` écrase toujours — activer depuis une
// install prend le relais sur l'autre. Deux instances simultanées
// collisionneraient de toute façon sur RegisterHotKey (err 1409).
//
// Tous les appels registry sont encapsulés avec try/catch : en cas de
// refus (GPO machine, profil corrompu), on log + on retourne false/l'état
// actuel sans propager — le toggle Settings ne doit jamais crasher l'UI.
public static class AutostartService
{
    private static readonly LogService _log = LogService.Instance;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "WhispUI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string s) return false;
            return IsOwnedByCurrentExe(s);
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"autostart probe failed | error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool Enable()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _log.Warning(LogSource.Settings, "autostart enable skipped | reason=Environment.ProcessPath empty");
            return false;
        }

        // Wrap the path in quotes so spaces in the install path don't split
        // it into arguments when Windows launches the entry.
        string command = $"\"{exePath}\"";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _log.Warning(LogSource.Settings, $"autostart enable failed | reason=cannot open HKCU\\{RunKeyPath}");
                return false;
            }
            key.SetValue(ValueName, command, RegistryValueKind.String);
            _log.Info(LogSource.Settings, "Autostart enabled");
            _log.Verbose(LogSource.Settings, $"autostart enabled | command={command}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"autostart enable failed | error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                // No key means no entry — treat as already disabled.
                return true;
            }
            if (key.GetValue(ValueName) is string s && !IsOwnedByCurrentExe(s))
            {
                // Entry belongs to another install of WhispUI — leave it alone.
                _log.Verbose(LogSource.Settings, "autostart disable skipped | reason=entry points to different install");
                return true;
            }
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _log.Info(LogSource.Settings, "Autostart disabled");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"autostart disable failed | error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // The Run value is stored as `"C:\path\to\exe.exe"` (quoted). An older
    // entry without quotes is also tolerated. Anything after the exe path
    // (arguments) is ignored for ownership comparison.
    private static string? ExtractExePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end < 0 ? null : command.Substring(1, end - 1);
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static bool IsOwnedByCurrentExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string? stored = ExtractExePath(command);
        if (string.IsNullOrWhiteSpace(stored)) return false;
        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return false;
        return string.Equals(stored, current, StringComparison.OrdinalIgnoreCase);
    }
}
