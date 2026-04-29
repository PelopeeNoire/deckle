using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WhispUI.Llm;
using WhispUI.Localization;
using WhispUI.Logging;
using WhispUI.Settings.Llm;

namespace WhispUI.Settings;

// ─── LlmPage — host fin ────────────────────────────────────────────────────
//
// Empile les cinq sections en UserControls autonomes (General, ShortcutSlots,
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
// + StateChanged. Les autres (General, Profiles, ShortcutSlots, Rules)
// rechargent directement depuis SettingsService.

public sealed partial class LlmPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    // Borne agressive sur les appels Ollama d'admin (list, show). Sans CTS,
    // le HttpClient partagé d'OllamaService a un timeout de 30 min — adapté
    // au push de blob GGUF, fatal pour un appel "rapide" dont on attend un
    // retour quasi-instantané. Si Ollama est saturé (benchmark GPU concurrent,
    // modèle qui crashe), on tombe en état "unavailable" plutôt que de geler
    // la page Settings.
    private static readonly TimeSpan OllamaAdminTimeout = TimeSpan.FromSeconds(5);

    private readonly LlmOllamaContext _context = new();

    public LlmPage()
    {
        InitializeComponent();
        IsTabStop = true;
        NavigationCacheMode = NavigationCacheMode.Required;

        ModelsSection.Initialize(_context);

        // Handlers nommés (et non lambdas async inline) parce qu'une lambda
        // async (_, _) => await ... est compilée comme async void : toute
        // exception non attrapée remonte au dispatcher. Les méthodes nommées
        // ci-dessous embarquent leur try/catch.
        GeneralSection.EndpointChanged += OnEndpointChanged;
        ProfilesSection.ProfilesChanged += OnProfilesChanged;
        ModelsSection.RefreshRequested += OnRefreshRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // OnNavigatedTo doit être async void (signature override imposée par
        // WinUI). Try/catch global obligatoire : sans ce filet, une exception
        // non attrapée pendant Hydrate()/RefreshOllamaStateAsync() remonte au
        // dispatcher et peut tuer l'app malgré Application.UnhandledException.
        base.OnNavigatedTo(e);
        try
        {
            Hydrate();
            await RefreshOllamaStateAsync();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetLlm, $"OnNavigatedTo failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnEndpointChanged(object? sender, EventArgs e)
    {
        try { await RefreshOllamaStateAsync(); }
        catch (Exception ex)
        {
            _log.Warning(LogSource.SetLlm, $"Endpoint refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        try { await RefreshOllamaStateAsync(); }
        catch (Exception ex)
        {
            _log.Warning(LogSource.SetLlm, $"Manual refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        // Synchrone, pas de try/catch nécessaire — Reload() ne touche que des
        // collections en mémoire. Si un Reload lève, c'est un bug d'état
        // ailleurs et l'UnhandledException global le capture.
        RulesSection.Reload();
        ShortcutSlotsSection.Reload();
    }

    private void Hydrate()
    {
        GeneralSection.Reload();
        ProfilesSection.Reload();
        ShortcutSlotsSection.Reload();
        RulesSection.Reload();
    }

    private async Task RefreshOllamaStateAsync()
    {
        var service = new OllamaService(() => SettingsService.Instance.Current.Llm.OllamaEndpoint);

        bool available = false;
        IReadOnlyList<OllamaModel> models = Array.Empty<OllamaModel>();

        // Try/catch englobant : IsAvailableAsync est déjà fail-soft (retourne
        // false sur exception), mais ListModelsAsync peut lever (HttpRequest,
        // TaskCanceled si CTS expire, JsonException si Ollama renvoie une
        // erreur HTML). Tomber en état "unavailable" couvre tous les cas
        // sans casser la page.
        try
        {
            available = await service.IsAvailableAsync();
            if (available)
            {
                using var cts = new CancellationTokenSource(OllamaAdminTimeout);
                models = await service.ListModelsAsync(cts.Token);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(LogSource.SetLlm, $"Ollama refresh skipped: {ex.GetType().Name}: {ex.Message}");
            available = false;
            models = Array.Empty<OllamaModel>();
        }

        _context.Service = service;
        _context.Available = available;
        _context.Models = models;

        string ep = SettingsService.Instance.Current.Llm.OllamaEndpoint;
        OllamaStatusBar.Message = Loc.Format("Settings_OllamaStatusMessage_Format", ep);
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
        // Event handler async void — try/catch obligatoire pour éviter qu'une
        // exception (Save IO, hydration UI) remonte au dispatcher.
        try
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("Settings_ResetLlmDialog_Title"),
                Content = Loc.Get("Settings_ResetLlmDialog_Content"),
                PrimaryButtonText = Loc.Get("LlmPageResetAllLabel.Text"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            SettingsService.Instance.Current.Llm = new LlmSettings();
            SettingsService.MigrateProfileIds(SettingsService.Instance.Current);
            SettingsService.Instance.Save();
            Hydrate();
            await RefreshOllamaStateAsync();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetLlm, $"Reset all failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
