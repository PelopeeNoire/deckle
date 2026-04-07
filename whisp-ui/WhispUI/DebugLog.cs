namespace WhispUI;

// Helper de diagnostic de démarrage.
// Écriture synchrone dans %TEMP%\whisp-debug.log — conçu pour survivre à un crash
// immédiat et pour ne jamais masquer le diagnostic en cas d'échec d'écriture.
internal static class DebugLog
{
    private static readonly string _path =
        Path.Combine(Path.GetTempPath(), "whisp-debug.log");

    private static readonly object _lock = new();

    static DebugLog()
    {
        try
        {
            File.AppendAllText(
                _path,
                $"=== WhispUI démarrage {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
        }
        catch
        {
            // Ne jamais laisser le logger faire tomber l'app.
        }
    }

    public static void Write(string phase, string message)
    {
        try
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"[{ts}] [{phase}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // Silencieux par construction.
        }
    }
}
