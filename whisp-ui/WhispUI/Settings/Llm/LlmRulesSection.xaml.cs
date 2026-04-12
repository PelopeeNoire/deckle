using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Settings.ViewModels;

namespace WhispUI.Settings.Llm;

// ─── Section Auto-rewrite rules de LlmPage ─────────────────────────────────
//
// Liste des règles "recording > Ns → profil X". Tout le contenu est
// déclaratif (XAML DataTemplate + RuleViewModel auto-save). Le code-behind
// gère Reload(), la population des ComboBox de profil via Loaded, et les
// handlers Delete/Add.
//
// Ordre d'affichage : ascendant par durée minimum. L'évaluation runtime
// côté engine parcourt à l'envers (la plus spécifique qui matche gagne).

public sealed partial class LlmRulesSection : UserControl
{
    private bool _loading;

    public ObservableCollection<RuleViewModel> Rules { get; } = new();

    public LlmRulesSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        Rules.Clear();

        var s = SettingsService.Instance.Current.Llm;
        SortRulesByDurationAsc(s.AutoRewriteRules);

        for (int i = 0; i < s.AutoRewriteRules.Count; i++)
        {
            var vm = new RuleViewModel();
            vm.Load(i, s.AutoRewriteRules[i]);
            Rules.Add(vm);
        }
        _loading = false;
    }

    private static void SortRulesByDurationAsc(System.Collections.Generic.List<AutoRewriteRule> rules)
    {
        rules.Sort((a, b) => a.MinDurationSeconds.CompareTo(b.MinDurationSeconds));
    }

    // Populate profile ComboBox when it loads inside the DataTemplate.
    private void ProfileCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not RuleViewModel vm)
            return;

        combo.Items.Clear();
        var profiles = SettingsService.Instance.Current.Llm.Profiles;
        int selectedIdx = -1;
        for (int i = 0; i < profiles.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = profiles[i].Name });
            if (string.Equals(profiles[i].Name, vm.ProfileName, StringComparison.OrdinalIgnoreCase))
                selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : (profiles.Count > 0 ? 0 : -1);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is ComboBox combo
            && combo.Tag is RuleViewModel vm
            && combo.SelectedItem is ComboBoxItem item)
        {
            vm.ProfileName = item.Content?.ToString() ?? "";
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RuleViewModel vm)
            return;

        var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
        if (vm.RuleIndex < rules.Count)
        {
            rules.RemoveAt(vm.RuleIndex);
            SettingsService.Instance.Save();
        }
        Reload();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance.Current.Llm;
        string defaultProfile = s.Profiles.Count > 0 ? s.Profiles[0].Name : "";
        s.AutoRewriteRules.Add(new AutoRewriteRule
        {
            MinDurationSeconds = 60,
            ProfileName = defaultProfile
        });
        SettingsService.Instance.Save();
        Reload();
    }
}
