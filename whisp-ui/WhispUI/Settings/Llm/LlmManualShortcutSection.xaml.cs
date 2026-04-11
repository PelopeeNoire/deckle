using System;
using Microsoft.UI.Xaml.Controls;

namespace WhispUI.Settings.Llm;

// ─── Section Manual shortcut de LlmPage ─────────────────────────────────────
//
// Unique contrôle : ComboBox du profil associé au raccourci manuel (Alt+Ctrl+`).
// La liste dépend des profils définis — Reload() doit être appelée par le host
// après chaque mutation de la section Profiles.

public sealed partial class LlmManualShortcutSection : UserControl
{
    private bool _loading;

    public LlmManualShortcutSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        var s = SettingsService.Instance.Current.Llm;
        ManualProfileCombo.Items.Clear();
        int selectedIndex = -1;
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            ManualProfileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[i].Name });
            if (string.Equals(s.Profiles[i].Name, s.ManualProfileName, StringComparison.OrdinalIgnoreCase))
                selectedIndex = i;
        }
        ManualProfileCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        _loading = false;
    }

    private void ManualProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ManualProfileCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsService.Instance.Current.Llm.ManualProfileName = item.Content?.ToString() ?? "";
        SettingsService.Instance.Save();
    }
}
