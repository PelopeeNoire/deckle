using Microsoft.UI.Xaml;

namespace HudPlayground;

// Minimal Application bootstrap. Unlike WhispUI we have no tray, no
// hotkey manager, no WhispEngine — the playground is a single
// MainWindow that lives for the duration of the process. Closing the
// window ends the process the usual way (default WinUI 3 WinExe
// behaviour when the only Window closes).
public partial class App : Application
{
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();

        // Dev tool: surface crashes directly to the debugger rather than
        // silently swallowing them like WhispUI's production handlers.
        // If a state / slider wiring mis-fires, we want the stack trace
        // in the debugger, not a log file.
        this.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HudPlayground] UnhandledException: {e.Exception}");
            // Let the process terminate so Louis sees the failure
            // immediately and re-runs with a fix.
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
