using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WhispUI.Settings;

// Page General — démo visuelle SettingsCard / SettingsExpander (pattern
// canonique Microsoft Learn). Aucune logique métier branchée : les contrôles
// sont là pour valider le rendu Windows 11 avant la vraie implémentation.
public sealed partial class GeneralPage : Page
{
    public GeneralPage()
    {
        InitializeComponent();
        // Cache la page en mémoire pour préserver l'état (scroll, toggles) quand
        // l'utilisateur navigue vers Whisper/Llm et revient. Pattern documenté
        // sur learn.microsoft.com (Frame caching).
        NavigationCacheMode = NavigationCacheMode.Required;
    }
}
