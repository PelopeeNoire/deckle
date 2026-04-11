using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhispUI.Settings.Llm;

// ─── Section Auto-rewrite rules de LlmPage ─────────────────────────────────
//
// Liste des règles "recording > Ns → profil X". Cards dynamiques construites
// en code-behind (chaque carte est un SettingsCard custom avec edit mode
// inline). Le Reload() est appelé par le host quand la liste des profils
// change côté LlmProfilesSection — les ComboBox de profil dans chaque carte
// doivent alors se re-remplir.
//
// Ordre d'affichage : ascendant par durée minimum. L'évaluation runtime côté
// WhispEngine parcourt à l'envers pour prendre la plus spécifique qui matche
// — ce point est géré côté engine, pas ici.

public sealed partial class LlmRulesSection : UserControl
{
    private bool _loading;
    private int _editingRuleIndex = -1;
    private bool _isNewRule;

    public LlmRulesSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        _loading = true;
        _editingRuleIndex = -1;
        _isNewRule = false;
        RebuildRules();
        _loading = false;
    }

    // Tri ascendant par durée : la règle "> 30s" apparaît au-dessus de "> 90s".
    // Appelé avant chaque rebuild (sauf en mode édition — sinon l'index stocké
    // pointerait vers le mauvais élément après le sort).
    private static void SortRulesByDurationAsc()
    {
        var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
        rules.Sort((a, b) => a.MinDurationSeconds.CompareTo(b.MinDurationSeconds));
    }

    private void RebuildRules()
    {
        RulesContainer.Children.Clear();
        var s = SettingsService.Instance.Current.Llm;

        if (_editingRuleIndex < 0)
            SortRulesByDurationAsc();

        for (int i = 0; i < s.AutoRewriteRules.Count; i++)
        {
            int index = i;
            var rule = s.AutoRewriteRules[i];
            bool isEditing = (index == _editingRuleIndex);

            if (isEditing)
                RulesContainer.Children.Add(BuildEditCard(index, rule));
            else
                RulesContainer.Children.Add(BuildReadOnlyCard(index, rule));
        }
    }

    private SettingsCard BuildEditCard(int index, AutoRewriteRule rule)
    {
        var s = SettingsService.Instance.Current.Llm;

        int savedDuration = rule.MinDurationSeconds;
        string savedProfile = rule.ProfileName;

        var durationBox = new NumberBox
        {
            Minimum = 0,
            Maximum = 600,
            SmallChange = 5,
            LargeChange = 30,
            Value = rule.MinDurationSeconds,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 100
        };

        var suffixLabel = new TextBlock
        {
            Text = "s",
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var profileCombo = new ComboBox { MinWidth = 160 };
        int selectedIdx = -1;
        for (int p = 0; p < s.Profiles.Count; p++)
        {
            profileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[p].Name });
            if (string.Equals(s.Profiles[p].Name, rule.ProfileName, StringComparison.OrdinalIgnoreCase))
                selectedIdx = p;
        }
        profileCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

        var saveBtn = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        saveBtn.Click += (_, _) =>
        {
            var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
            if (index < rules.Count)
            {
                rules[index].MinDurationSeconds = (int)durationBox.Value;
                if (profileCombo.SelectedItem is ComboBoxItem item)
                    rules[index].ProfileName = item.Content?.ToString() ?? "";
                SettingsService.Instance.Save();
            }
            _editingRuleIndex = -1;
            _isNewRule = false;
            _loading = true;
            RebuildRules();
            _loading = false;
        };

        var cancelBtn = new Button { Content = "Cancel" };
        cancelBtn.Click += (_, _) =>
        {
            if (_isNewRule)
            {
                var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
                if (index < rules.Count)
                {
                    rules.RemoveAt(index);
                    SettingsService.Instance.Save();
                }
            }
            else
            {
                var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
                if (index < rules.Count)
                {
                    rules[index].MinDurationSeconds = savedDuration;
                    rules[index].ProfileName = savedProfile;
                }
            }
            _editingRuleIndex = -1;
            _isNewRule = false;
            _loading = true;
            RebuildRules();
            _loading = false;
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(durationBox);
        row.Children.Add(suffixLabel);
        row.Children.Add(profileCombo);
        row.Children.Add(saveBtn);
        row.Children.Add(cancelBtn);

        return new SettingsCard
        {
            ContentAlignment = ContentAlignment.Vertical,
            Content = row
        };
    }

    private SettingsCard BuildReadOnlyCard(int index, AutoRewriteRule rule)
    {
        var s = SettingsService.Instance.Current.Llm;

        var profileCombo = new ComboBox { MinWidth = 160 };
        int selectedIdx = -1;
        for (int p = 0; p < s.Profiles.Count; p++)
        {
            profileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[p].Name });
            if (string.Equals(s.Profiles[p].Name, rule.ProfileName, StringComparison.OrdinalIgnoreCase))
                selectedIdx = p;
        }
        profileCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

        var card = new SettingsCard
        {
            Header = rule.ProfileName,
            Description = $"Recordings longer than {rule.MinDurationSeconds}s"
        };

        profileCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (profileCombo.SelectedItem is ComboBoxItem item)
            {
                var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
                if (index < rules.Count)
                {
                    rules[index].ProfileName = item.Content?.ToString() ?? "";
                    card.Header = rules[index].ProfileName;
                    SettingsService.Instance.Save();
                }
            }
        };

        var editBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE70F", FontSize = 14 },
                    new TextBlock { Text = "Edit" }
                }
            }
        };
        editBtn.Click += (_, _) =>
        {
            _editingRuleIndex = index;
            _isNewRule = false;
            _loading = true;
            RebuildRules();
            _loading = false;
        };

        var deleteBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                    new TextBlock { Text = "Remove" }
                }
            }
        };
        deleteBtn.Click += (_, _) =>
        {
            var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
            if (index < rules.Count)
            {
                rules.RemoveAt(index);
                SettingsService.Instance.Save();
                _loading = true;
                RebuildRules();
                _loading = false;
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(profileCombo);
        actions.Children.Add(editBtn);
        actions.Children.Add(deleteBtn);

        card.Content = actions;
        return card;
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
        _editingRuleIndex = s.AutoRewriteRules.Count - 1;
        _isNewRule = true;
        _loading = true;
        RebuildRules();
        _loading = false;
    }
}
