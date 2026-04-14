using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Llm;
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
// La section Models dépend d'Ollama et reçoit le context via Initialize()
// + StateChanged. Les autres (General, Profiles, ManualShortcut, Rules)
// rechargent directement depuis SettingsService.

public sealed partial class LlmPage : Page
{
    private readonly LlmOllamaContext _context = new();

    public LlmPage()
    {
        InitializeComponent();
        IsTabStop = true;
        NavigationCacheMode = NavigationCacheMode.Required;

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
        ProfilesSection.Reload();
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

        // Update model ComboBoxes in profiles after Ollama responds.
        // ObservableCollection on the section propagates to bound ItemsSources
        // without needing to recreate the ProfileViewModels.
        var modelNames = new List<string>();
        foreach (var m in models) modelNames.Add(m.Name);
        ProfilesSection.SetAvailableModelNames(modelNames);
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't steal focus from a ComboBox — its Tapped bubbles up here
        // and re-focusing the page would close the dropdown before it opens,
        // forcing the user to click 2-3 times. Other interactive controls
        // (TextBox, NumberBox, editable ComboBox) mark Tapped as handled
        // internally so they don't reach this handler.
        if (e.OriginalSource is DependencyObject obj && IsInsideComboBox(obj))
            return;
        this.Focus(FocusState.Programmatic);
    }

    private static bool IsInsideComboBox(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is ComboBox) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Current.Llm = new LlmSettings();
        SettingsService.Instance.Save();
        Hydrate();
        await RefreshOllamaStateAsync();
    }
}
