using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhispUI.Settings.Llm;

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
        _loading = false;
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Llm.Enabled = EnabledToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void EndpointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Llm.OllamaEndpoint = EndpointBox.Text.Trim();
        SettingsService.Instance.Save();
        EndpointChanged?.Invoke(this, EventArgs.Empty);
    }
}
