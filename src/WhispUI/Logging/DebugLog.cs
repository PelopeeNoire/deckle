namespace WhispUI.Logging;

// No-op. Historical file logger to %TEMP%\whisp-debug.log removed — it
// accumulated without rotation and leaked into the user's temp folder.
// Crash handlers and diagnostics flow exclusively through LogService /
// LogWindow now. The type and its Write method are kept so the 30+
// existing call sites continue to compile; they silently discard.
internal static class DebugLog
{
    public static void Write(string phase, string message) { }
}
