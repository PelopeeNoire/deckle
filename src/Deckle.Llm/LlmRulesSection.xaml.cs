using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Llm;
using Deckle.Localization;

namespace Deckle.Llm;

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
//
// ── Architecture note ──────────────────────────────────────────────────────
//
// Cards are built imperatively in code-behind (BuildWordsRuleCard /
// BuildDurationRuleCard), aligned on LlmShortcutSlotsSection's pattern. The
// previous ItemsRepeater + DataTemplate + per-VM ProfileChoices design lost
// the user's selection on every metric switch, profile add/remove, and
// page navigation because of five overlapping fragilities:
//
//   1. ItemsRepeater does NOT propagate DataContext to template descendants
//      (decision design, not bug — see memory reference_winui_itemsrepeater_datacontext).
//   2. x:Bind TwoWay on ComboBox.SelectedItem (object) → string property
//      with [ObservableProperty] partial-property generation never wrote
//      back the user's selection.
//   3. Per-VM ObservableCollection<string> ProfileChoices.Clear()+Add() at
//      Reload time fires a Reset event that resets ComboBox.SelectedItem
//      to null.
//   4. Visibility flips of the parent panel detached and recycled
//      ComboBox templates, dropping their SelectedItem mid-flight.
//   5. The two-pass Loaded retry trick papered over (1)–(4) on the happy
//      path but failed on every mutation that triggered a Reload.
//
// The imperative construction here has zero of those failure modes:
//   • No DataContext — controls hold the rule index in their Tag.
//   • No x:Bind — Items are populated directly with ComboBoxItem instances
//     (the same shape as LlmShortcutSlotsSection).
//   • No template recycling — cards are created fresh per Reload, the
//     full StackPanel is repopulated when profiles change.
//   • SelectedIndex set explicitly after Items, no race with binding.

public sealed partial class LlmRulesSection : UserControl
{
    // Suppresses event handlers during programmatic mutations — Reload sets
    // SelectedIndex on every combo, which fires SelectionChanged. Without
    // this gate every Reload would rewrite Settings to its current state and
    // burn a Save() per combo.
    private bool _loading;

    public LlmRulesSection()
    {
        InitializeComponent();
    }

    // ── Public entry points ─────────────────────────────────────────────────

    public void Reload()
    {
        _loading = true;
        try
        {
            var s = LlmSettingsService.Instance.Current;

            // Display order: ascending. Engine evaluates descending (longest
            // matching rule wins) at runtime — kept in the same list, so we
            // don't need a separate display projection.
            s.AutoRewriteRules.Sort((a, b) => a.MinDurationSeconds.CompareTo(b.MinDurationSeconds));
            s.AutoRewriteRulesByWords.Sort((a, b) => a.MinWordCount.CompareTo(b.MinWordCount));

            BuildWordsPanel(s);
            BuildDurationPanel(s);

            bool byWords = !string.Equals(s.RuleMetric, "Duration", StringComparison.OrdinalIgnoreCase);
            MetricRadios.SelectedIndex = byWords ? 0 : 1;
            ApplyMetricVisibility(byWords);
        }
        finally
        {
            _loading = false;
        }
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
        LlmSettingsService.Instance.Current.RuleMetric = byWords ? "Words" : "Duration";
        LlmSettingsService.Instance.Save();
        ApplyMetricVisibility(byWords);
    }

    // ── Words panel ─────────────────────────────────────────────────────────

    private void BuildWordsPanel(LlmSettings s)
    {
        WordsRulesPanel.Children.Clear();
        var profileNames = s.Profiles.Select(p => p.Name).ToList();

        for (int i = 0; i < s.AutoRewriteRulesByWords.Count; i++)
            WordsRulesPanel.Children.Add(BuildWordsRuleCard(s.AutoRewriteRulesByWords[i], i, profileNames));

        var addBtn = new Button { Content = Loc.Get("Settings_AddRuleButton"), Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += AddRuleByWords_Click;
        WordsRulesPanel.Children.Add(addBtn);
    }

    private SettingsCard BuildWordsRuleCard(AutoRewriteRuleByWords rule, int index, List<string> profileNames)
    {
        var card = new SettingsCard
        {
            Header      = HeaderForRule(rule.ProfileName),
            Description = $"Recordings longer than {rule.MinWordCount} words"
        };

        var panel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var numberBox = new NumberBox
        {
            Minimum                = 0,
            Maximum                = 10000,
            SmallChange            = 50,
            LargeChange            = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value                  = rule.MinWordCount,
            MinWidth               = 100,
            Tag                    = index
        };
        numberBox.ValueChanged += WordsMinChanged;
        panel.Children.Add(numberBox);

        panel.Children.Add(BuildUnitLabel("words"));

        var combo = BuildProfileCombo(profileNames, rule.ProfileName, index);
        combo.SelectionChanged += WordsProfileChanged;
        panel.Children.Add(combo);

        var removeBtn = BuildRemoveButton(index);
        removeBtn.Click += DeleteRuleByWords_Click;
        panel.Children.Add(removeBtn);

        card.Content = panel;
        return card;
    }

    private void WordsMinChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (sender.Tag is not int index) return;
        if (double.IsNaN(args.NewValue)) return;

        var rules = LlmSettingsService.Instance.Current.AutoRewriteRulesByWords;
        if (index >= rules.Count) return;

        rules[index].MinWordCount = (int)args.NewValue;
        LlmSettingsService.Instance.Save();

        // Refresh the description live — the Header (= profile name) does
        // not depend on the threshold so it stays untouched.
        if (sender.Parent is Panel p && p.Parent is SettingsCard card)
            card.Description = $"Recordings longer than {(int)args.NewValue} words";
    }

    private void WordsProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo || combo.Tag is not int index) return;
        if (combo.SelectedItem is not ComboBoxItem item) return;
        string? name = item.Content?.ToString();
        if (string.IsNullOrEmpty(name)) return;

        var s = LlmSettingsService.Instance.Current;
        if (index >= s.AutoRewriteRulesByWords.Count) return;

        var rule = s.AutoRewriteRulesByWords[index];
        rule.ProfileName = name;
        rule.ProfileId   = s.Profiles.Find(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        LlmSettingsService.Instance.Save();

        // Live header refresh — the user expects the card title (= profile
        // name) to change with the dropdown without waiting for a reload.
        if (combo.Parent is Panel p && p.Parent is SettingsCard card)
            card.Header = HeaderForRule(name);
    }

    private void DeleteRuleByWords_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int index) return;
        var rules = LlmSettingsService.Instance.Current.AutoRewriteRulesByWords;
        if (index >= rules.Count) return;

        rules.RemoveAt(index);
        LlmSettingsService.Instance.Save();
        Reload();
    }

    private void AddRuleByWords_Click(object sender, RoutedEventArgs e)
    {
        var s = LlmSettingsService.Instance.Current;
        string defaultProfileName = s.Profiles.Count > 0 ? s.Profiles[0].Name : "";
        string defaultProfileId   = s.Profiles.Count > 0 ? s.Profiles[0].Id   : "";
        s.AutoRewriteRulesByWords.Add(new AutoRewriteRuleByWords
        {
            MinWordCount = 100,
            ProfileName  = defaultProfileName,
            ProfileId    = defaultProfileId
        });
        LlmSettingsService.Instance.Save();
        Reload();
    }

    // ── Duration panel ──────────────────────────────────────────────────────

    private void BuildDurationPanel(LlmSettings s)
    {
        DurationRulesPanel.Children.Clear();
        var profileNames = s.Profiles.Select(p => p.Name).ToList();

        for (int i = 0; i < s.AutoRewriteRules.Count; i++)
            DurationRulesPanel.Children.Add(BuildDurationRuleCard(s.AutoRewriteRules[i], i, profileNames));

        var addBtn = new Button { Content = Loc.Get("Settings_AddRuleButton"), Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += AddRule_Click;
        DurationRulesPanel.Children.Add(addBtn);
    }

    private SettingsCard BuildDurationRuleCard(AutoRewriteRule rule, int index, List<string> profileNames)
    {
        var card = new SettingsCard
        {
            Header      = HeaderForRule(rule.ProfileName),
            Description = $"Recordings longer than {(int)rule.MinDurationSeconds}s"
        };

        var panel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var numberBox = new NumberBox
        {
            Minimum                = 0,
            Maximum                = 600,
            SmallChange            = 5,
            LargeChange            = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value                  = rule.MinDurationSeconds,
            MinWidth               = 100,
            Tag                    = index
        };
        numberBox.ValueChanged += DurationMinChanged;
        panel.Children.Add(numberBox);

        panel.Children.Add(BuildUnitLabel("s"));

        var combo = BuildProfileCombo(profileNames, rule.ProfileName, index);
        combo.SelectionChanged += DurationProfileChanged;
        panel.Children.Add(combo);

        var removeBtn = BuildRemoveButton(index);
        removeBtn.Click += DeleteRule_Click;
        panel.Children.Add(removeBtn);

        card.Content = panel;
        return card;
    }

    private void DurationMinChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (sender.Tag is not int index) return;
        if (double.IsNaN(args.NewValue)) return;

        var rules = LlmSettingsService.Instance.Current.AutoRewriteRules;
        if (index >= rules.Count) return;

        rules[index].MinDurationSeconds = (int)args.NewValue;
        LlmSettingsService.Instance.Save();

        if (sender.Parent is Panel p && p.Parent is SettingsCard card)
            card.Description = $"Recordings longer than {(int)args.NewValue}s";
    }

    private void DurationProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo || combo.Tag is not int index) return;
        if (combo.SelectedItem is not ComboBoxItem item) return;
        string? name = item.Content?.ToString();
        if (string.IsNullOrEmpty(name)) return;

        var s = LlmSettingsService.Instance.Current;
        if (index >= s.AutoRewriteRules.Count) return;

        var rule = s.AutoRewriteRules[index];
        rule.ProfileName = name;
        rule.ProfileId   = s.Profiles.Find(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        LlmSettingsService.Instance.Save();

        if (combo.Parent is Panel p && p.Parent is SettingsCard card)
            card.Header = HeaderForRule(name);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int index) return;
        var rules = LlmSettingsService.Instance.Current.AutoRewriteRules;
        if (index >= rules.Count) return;

        rules.RemoveAt(index);
        LlmSettingsService.Instance.Save();
        Reload();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var s = LlmSettingsService.Instance.Current;
        string defaultProfileName = s.Profiles.Count > 0 ? s.Profiles[0].Name : "";
        string defaultProfileId   = s.Profiles.Count > 0 ? s.Profiles[0].Id   : "";
        s.AutoRewriteRules.Add(new AutoRewriteRule
        {
            MinDurationSeconds = 60,
            ProfileName        = defaultProfileName,
            ProfileId          = defaultProfileId
        });
        LlmSettingsService.Instance.Save();
        Reload();
    }

    // ── Reset section ───────────────────────────────────────────────────────

    // Scope: both rule lists + the metric pivot. The default rules point at
    // profile names ("Lissage", "Affinage", "Arrangement") — MigrateProfileIds
    // re-pairs them against the live Profiles list on save. If those profile
    // names don't exist yet, the rules sit with an "(unassigned)" header
    // until the user reassigns or until matching profiles are created — that's
    // by design (see MigrateProfileIds for why we never silently drop rules).
    private async void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Settings_ResetRulesDialog_Title"),
            Content = Loc.Get("Settings_ResetRulesDialog_Content"),
            PrimaryButtonText = Loc.Get("Common_Reset"),
            CloseButtonText   = Loc.Get("Common_Cancel"),
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var defaults = new LlmSettings();
        var s = LlmSettingsService.Instance.Current;
        s.AutoRewriteRules        = defaults.AutoRewriteRules;
        s.AutoRewriteRulesByWords = defaults.AutoRewriteRulesByWords;
        s.RuleMetric              = defaults.RuleMetric;
        LlmSettingsMigrations.RepairProfileReferences(LlmSettingsService.Instance.Current);
        LlmSettingsService.Instance.Save();
        Reload();
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    // Header text for a rule card. When no profile is bound (e.g. an orphan
    // after profile rename or a fresh rule on a config without matching
    // profiles), show a neutral placeholder rather than an empty title bar
    // so the card still reads as a discrete entity.
    private static string HeaderForRule(string profileName) =>
        string.IsNullOrEmpty(profileName) ? "(unassigned)" : profileName;

    private static TextBlock BuildUnitLabel(string text) => new()
    {
        Text                = text,
        VerticalAlignment   = VerticalAlignment.Center,
        Style               = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        Foreground          = (Microsoft.UI.Xaml.Media.Brush)
                                Application.Current.Resources["TextFillColorSecondaryBrush"]
    };

    // ComboBox shaped like LlmShortcutSlotsSection: Items populated with
    // ComboBoxItem (not strings — the Items collection is a heterogeneous
    // bag, ComboBoxItem renders consistently and lets us read .Content in
    // the SelectionChanged handler without ambiguity). SelectedIndex set
    // before the SelectionChanged handler is wired up by the caller — but
    // the _loading flag guards against the spurious initial fire either way.
    private static ComboBox BuildProfileCombo(List<string> profileNames, string currentName, int index)
    {
        var combo = new ComboBox { MinWidth = 160, Tag = index };
        for (int i = 0; i < profileNames.Count; i++)
            combo.Items.Add(new ComboBoxItem { Content = profileNames[i] });

        int sel = profileNames.FindIndex(n =>
            string.Equals(n, currentName, StringComparison.Ordinal));
        if (sel >= 0) combo.SelectedIndex = sel;
        // sel < 0 (orphan: rule's ProfileName isn't in the Profiles list) —
        // leave the combo blank. The card header shows "(unassigned)" so
        // the orphan is visible, and opening the dropdown lets the user pick
        // a fresh profile.

        return combo;
    }

    private static Button BuildRemoveButton(int index)
    {
        var btn = new Button { Tag = index };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // Segoe Fluent Icons — Delete glyph U+E74D (same as the previous XAML).
        stack.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        stack.Children.Add(new TextBlock { Text = Loc.Get("Settings_RemoveLabel") });
        btn.Content = stack;
        return btn;
    }
}
