using System;
using Microsoft.Win32;
using WhispUI.Logging;

namespace WhispUI.Shell;

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
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"Autostart probe failed: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    public static bool Enable()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _log.Warning(LogSource.Settings, "Autostart enable skipped — Environment.ProcessPath is empty");
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
                _log.Warning(LogSource.Settings, $"Autostart enable failed — cannot open HKCU\\{RunKeyPath}");
                return false;
            }
            key.SetValue(ValueName, command, RegistryValueKind.String);
            _log.Info(LogSource.Settings, $"Autostart enabled → {command}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"Autostart enable failed: {ex.GetType().Name} {ex.Message}");
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
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _log.Info(LogSource.Settings, "Autostart disabled");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.Settings, $"Autostart disable failed: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }
}
