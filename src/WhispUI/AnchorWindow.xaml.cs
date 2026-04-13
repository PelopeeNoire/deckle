using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using WhispUI.Interop;
using WhispUI.Logging;
using WhispUI.Shell;

namespace WhispUI;

// Window invisible — sert d'ancre lifetime à la pump WinUI 3.
// Tant qu'elle existe, le runtime XAML ne déclenche pas PostQuitMessage,
// même si toutes les autres windows sont cachées.
//
// Jamais affichée, jamais fermée par l'utilisateur. Hôte HWND stable
// pour HotkeyManager + TrayIconManager.
public sealed partial class AnchorWindow : Window
{
    private IntPtr _hwnd;
    private readonly TrayIconManager _tray;
    private readonly HotkeyManager _hotkeyManager;

    public AnchorWindow(TrayIconManager tray, Action<int> onHotkey)
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _tray = tray;
        _hotkeyManager = new HotkeyManager(_hwnd, onHotkey);

        // Jamais visible mais on lui pose quand même titre + icône pour les
        // outils Windows qui pourraient lister la fenêtre (Task Manager, debug).
        Title = "WhispUI";
        IconAssets.ApplyToWindow(AppWindow);

        AppWindow.Closing += OnClosing;
        RootGrid.Loaded += OnRootLoaded;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;

        // Attacher tray + hotkeys maintenant que l'arbre de messages WinUI 3
        // est en place (SetWindowSubclass refuse de s'accrocher avant).
        _tray.Register(_hwnd);
        _hotkeyManager.Register();

        // Cache proprement la fenêtre ancre.
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        DebugLog.Write("ANCHOR", "tray + hotkeys registered");
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // L'ancre ne doit jamais se fermer — sortie unique via Application.Current.Exit().
        args.Cancel = true;
    }
}
