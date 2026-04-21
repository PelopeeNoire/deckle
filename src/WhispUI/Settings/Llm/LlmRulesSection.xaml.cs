using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Settings.ViewModels;

namespace WhispUI.Settings.Llm;

// ─── Auto-rewrite rules section of LlmPage ─────────────────────────────────
//
// Two lists of rules, one keyed on duration and one on word count, plus a
// RadioButtons pivot that selects which list the engine uses at runtime
// (LlmSettings.RuleMetric). The UI shows both lists but only the active
// panel is visible — neither is "masked sub-options under a toggle", it's a
// mutually-exclusive selection of the underlying concept.
//
// Display order: ascending by threshold. Engine evaluates descending (longest
// matching rule wins) — cf. memory project_llm_rules_sort_order.

public sealed partial class LlmRulesSection : UserControl
{
    private bool _loading;

    public ObservableCollection<RuleViewModel> Rules { get; } = new();
    public ObservableCollection<RuleByWordsViewModel> RulesByWords { get; } = new();

    public LlmRulesSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        Rules.Clear();
        RulesByWords.Clear();

        var s = SettingsService.Instance.Current.Llm;

        s.AutoRewriteRules.Sort((a, b) => a.MinDurationSeconds.CompareTo(b.MinDurationSeconds));
        for (int i = 0; i < s.AutoRewriteRules.Count; i++)
        {
            var vm = new RuleViewModel();
            vm.Load(i, s.AutoRewriteRules[i]);
            Rules.Add(vm);
        }

        s.AutoRewriteRulesByWords.Sort((a, b) => a.MinWordCount.CompareTo(b.MinWordCount));
        for (int i = 0; i < s.AutoRewriteRulesByWords.Count; i++)
        {
            var vm = new RuleByWordsViewModel();
            vm.Load(i, s.AutoRewriteRulesByWords[i]);
            RulesByWords.Add(vm);
        }

        bool byWords = !string.Equals(s.RuleMetric, "Duration", StringComparison.OrdinalIgnoreCase);
        MetricRadios.SelectedIndex = byWords ? 0 : 1;
        ApplyMetricVisibility(byWords);

        _loading = false;
    }

    private void ApplyMetricVisibility(bool byWords)
    {
        WordsRulesPanel.Visibility    = byWords ? Visibility.Visible : Visibility.Collapsed;
        DurationRulesPanel.Visibility = byWords ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MetricRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        bool byWords = MetricRadios.SelectedIndex == 0;
        SettingsService.Instance.Current.Llm.RuleMetric = byWords ? "Words" : "Duration";
        SettingsService.Instance.Save();
        ApplyMetricVisibility(byWords);
    }

    // ── Duration list ───────────────────────────────────────────────────────

    private void ProfileCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not RuleViewModel vm)
            return;
        PopulateProfileCombo(combo, vm.ProfileName);
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
        string defaultProfileName = s.Profiles.Count > 0 ? s.Profiles[0].Name : "";
        string defaultProfileId   = s.Profiles.Count > 0 ? s.Profiles[0].Id   : "";
        s.AutoRewriteRules.Add(new AutoRewriteRule
        {
            MinDurationSeconds = 60,
            ProfileName = defaultProfileName,
            ProfileId   = defaultProfileId
        });
        SettingsService.Instance.Save();
        Reload();
    }

    // ── Words list ──────────────────────────────────────────────────────────

    private void ProfileComboByWords_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not RuleByWordsViewModel vm)
            return;
        PopulateProfileCombo(combo, vm.ProfileName);
    }

    private void ProfileComboByWords_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is ComboBox combo
            && combo.Tag is RuleByWordsViewModel vm
            && combo.SelectedItem is ComboBoxItem item)
        {
            vm.ProfileName = item.Content?.ToString() ?? "";
        }
    }

    private void DeleteRuleByWords_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RuleByWordsViewModel vm)
            return;
        var rules = SettingsService.Instance.Current.Llm.AutoRewriteRulesByWords;
        if (vm.RuleIndex < rules.Count)
        {
            rules.RemoveAt(vm.RuleIndex);
            SettingsService.Instance.Save();
        }
        Reload();
    }

    private void AddRuleByWords_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance.Current.Llm;
        string defaultProfileName = s.Profiles.Count > 0 ? s.Profiles[0].Name : "";
        string defaultProfileId   = s.Profiles.Count > 0 ? s.Profiles[0].Id   : "";
        s.AutoRewriteRulesByWords.Add(new AutoRewriteRuleByWords
        {
            MinWordCount = 100,
            ProfileName = defaultProfileName,
            ProfileId   = defaultProfileId
        });
        SettingsService.Instance.Save();
        Reload();
    }

    // Scope: both rule lists + the metric pivot. Profile references are left
    // as-is in the defaults (they point to the default profile names) —
    // SettingsService.MigrateProfileIds resolves them to current IDs on save.
    private void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new LlmSettings();
        var s = SettingsService.Instance.Current.Llm;
        s.AutoRewriteRules         = defaults.AutoRewriteRules;
        s.AutoRewriteRulesByWords  = defaults.AutoRewriteRulesByWords;
        s.RuleMetric               = defaults.RuleMetric;
        SettingsService.MigrateProfileIds(SettingsService.Instance.Current);
        SettingsService.Instance.Save();
        Reload();
    }

    // ── Shared combo population ────────────────────────────────────────────

    private static void PopulateProfileCombo(ComboBox combo, string currentName)
    {
        combo.Items.Clear();
        var profiles = SettingsService.Instance.Current.Llm.Profiles;
        int selectedIdx = -1;
        for (int i = 0; i < profiles.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = profiles[i].Name });
            if (string.Equals(profiles[i].Name, currentName, StringComparison.OrdinalIgnoreCase))
                selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : (profiles.Count > 0 ? 0 : -1);
    }
}
