using Microsoft.UI.Xaml.Controls;

namespace Deckle.Lighting.Ambient;

// Settings page for the Ambient Light module. Resolved by the Settings
// NavigationView from src/Deckle.Settings/SettingsWindow.xaml via the
// item Tag "Deckle.Lighting.Ambient.AmbientPage, Deckle.Lighting.Ambient".
//
// Minimal at J0c — title + placeholder text only. Real controls land in
// later milestones: J2 surfaces the Hue connection wizard entry point,
// J6 adds the Realistic / Game mode selector, J8 adds the game-profile
// auto-launch list, J9 wires the master Enabled toggle to the top.
public sealed partial class AmbientPage : Page
{
    public AmbientPage()
    {
        InitializeComponent();
    }
}
