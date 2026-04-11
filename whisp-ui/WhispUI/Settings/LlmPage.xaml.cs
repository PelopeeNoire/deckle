using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Settings.Llm;

namespace WhispUI.Settings;

// ─── LlmPage — host fin ────────────────────────────────────────────────────
//
// Empile les cinq sections en UserControls autonomes (General, ManualShortcut,
// Rules, Profiles, Models) + l'InfoBar de statut Ollama. Tout le contenu
// fonctionnel vit dans Settings/Llm/. Seules restent ici :
//
//  - l'orchestration (hydration + refresh Ollama)
//  - l'état partagé Ollama via LlmOllamaContext
//  - les événements inter-sections (EndpointChanged, ProfilesChanged,
//    RefreshRequested) qui redéclenchent soit un refresh Ollama, soit un
//    Reload ciblé des sections dépendantes
//  - le Reset all global
//
// Les sections qui dépendent d'Ollama (Profiles, Models) reçoivent le context
// via Initialize() et s'abonnent à StateChanged. Les autres (General,
// ManualShortcut, Rules) rechargent directement depuis SettingsService.

public sealed partial class LlmPage : Page
{
    private readonly LlmOllamaContext _context = new();

    public LlmPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        ProfilesSection.Initialize(_context);
        ModelsSection.Initialize(_context);

        GeneralSection.EndpointChanged += async (_, _) => await RefreshOllamaStateAsync();
        ProfilesSection.ProfilesChanged += (_, _) =>
        {
            RulesSection.Reload();
            ManualShortcutSection.Reload();
        };
        ModelsSection.RefreshRequested += async (_, _) => await RefreshOllamaStateAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Hydrate();
        await RefreshOllamaStateAsync();
    }

    private void Hydrate()
    {
        GeneralSection.Reload();
        ManualShortcutSection.Reload();
        RulesSection.Reload();
    }

    private async Task RefreshOllamaStateAsync()
    {
        var service = new OllamaService(() => SettingsService.Instance.Current.Llm.OllamaEndpoint);

        bool available = await service.IsAvailableAsync();
        IReadOnlyList<OllamaModel> models = Array.Empty<OllamaModel>();
        if (available)
        {
            try { models = await service.ListModelsAsync(); }
            catch { models = Array.Empty<OllamaModel>(); }
        }

        _context.Service = service;
        _context.Available = available;
        _context.Models = models;

        string ep = SettingsService.Instance.Current.Llm.OllamaEndpoint;
        OllamaStatusBar.Message = $"Start Ollama or check the endpoint setting ({ep}).";
        OllamaStatusBar.IsOpen = !available;

        _context.RaiseStateChanged();
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Current.Llm = new LlmSettings();
        SettingsService.Instance.Save();
        Hydrate();
        await RefreshOllamaStateAsync();
    }
}
