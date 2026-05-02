using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Llm;

namespace Deckle.Settings.Llm;

// ─── Section General de LlmPage ────────────────────────────────────────────
//
// Gère le toggle "Enable rewriting" et le champ "Ollama endpoint".
// Autosave à chaque changement. Émet EndpointChanged pour signaler au host
// qu'il doit re-vérifier la disponibilité d'Ollama (la disponibilité dépend
// de l'URL, donc le changement d'URL invalide le cache).

public sealed partial class LlmGeneralSection : UserControl
{
    private bool _loading;

    public event EventHandler? EndpointChanged;

    // Exposed as a DependencyProperty so LlmPage can bind the IsEnabled of
    // dependent sections (endpoint, shortcut slots, rules, profiles, models)
    // to this master toggle via x:Bind OneWay.
    public static readonly DependencyProperty IsRewritingEnabledProperty =
        DependencyProperty.Register(
            nameof(IsRewritingEnabled),
            typeof(bool),
            typeof(LlmGeneralSection),
            new PropertyMetadata(false));

    public bool IsRewritingEnabled
    {
        get => (bool)GetValue(IsRewritingEnabledProperty);
        private set => SetValue(IsRewritingEnabledProperty, value);
    }

    public LlmGeneralSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        var s = SettingsService.Instance.Current.Llm;
        EnabledToggle.IsOn = s.Enabled;
        EndpointBox.Text = s.OllamaEndpoint;
        IsRewritingEnabled = s.Enabled;
        _loading = false;
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Llm.Enabled = EnabledToggle.IsOn;
        SettingsService.Instance.Save();
        IsRewritingEnabled = EnabledToggle.IsOn;
    }

    private void EndpointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Llm.OllamaEndpoint = EndpointBox.Text.Trim();
        SettingsService.Instance.Save();
        EndpointChanged?.Invoke(this, EventArgs.Empty);
    }

    // Scope: Enabled + OllamaEndpoint. Source of truth for defaults is a fresh
    // LlmSettings — no duplication of literal defaults here. Fires
    // EndpointChanged so LlmPage re-checks Ollama availability against the
    // restored endpoint.
    private void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new LlmSettings();
        var s = SettingsService.Instance.Current.Llm;
        s.Enabled = defaults.Enabled;
        s.OllamaEndpoint = defaults.OllamaEndpoint;
        SettingsService.Instance.Save();
        Reload();
        EndpointChanged?.Invoke(this, EventArgs.Empty);
    }
}
