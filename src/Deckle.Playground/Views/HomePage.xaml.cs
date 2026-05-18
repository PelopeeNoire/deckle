using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deckle.Playground;

// Landing page of the Playground. Holds no state, no settings, no
// timers — just two routing handlers. Lives in NavigationCacheMode.Required
// for consistency with the other pages (cheap to keep around, avoids
// re-instantiation on every back-nav).
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    // Route via the shell's callback registry — the page doesn't reach
    // into PlaygroundWindow directly, so the routing target can move
    // (e.g. become a Frame.Navigate in a future split). Same shape as
    // SettingsHost.OpenSetupWizard which GeneralPage uses.
    private void OnHudCardClick(object sender, RoutedEventArgs e)
    {
        PlaygroundShell.NavigateTo?.Invoke("hud");
    }

    private void OnAmbientCardClick(object sender, RoutedEventArgs e)
    {
        PlaygroundShell.NavigateTo?.Invoke("ambient");
    }
}
