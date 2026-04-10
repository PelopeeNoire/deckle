using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WhispUI.Settings;

public sealed partial class LlmPage : Page
{
    private bool _loading;

    // Defaults resolved from the POCO — single source of truth.
    private static readonly LlmSettings _defaults = new();

    // Rule editing state: index of the rule being edited, -1 = none.
    private int _editingRuleIndex = -1;
    private bool _isNewRule;

    // Profile editing state: index of a newly created profile, -1 = none.
    private int _newProfileIndex = -1;

    public LlmPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Hydrate();
    }

    // ── Hydration ────────────────────────────────────────────────────────────

    private void Hydrate()
    {
        _loading = true;
        _editingRuleIndex = -1;
        _isNewRule = false;
        _newProfileIndex = -1;
        var s = SettingsService.Instance.Current.Llm;

        EnabledToggle.IsOn = s.Enabled;
        EndpointBox.Text = s.OllamaEndpoint;

        RebuildManualProfileCombo();
        RebuildRules();
        RebuildProfiles();

        _loading = false;
    }

    // ── General handlers ─────────────────────────────────────────────────────

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
    }

    // ── Manual profile selector ──────────────────────────────────────────────

    private void RebuildManualProfileCombo()
    {
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
    }

    private void ManualProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ManualProfileCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsService.Instance.Current.Llm.ManualProfileName = item.Content?.ToString() ?? "";
        SettingsService.Instance.Save();
    }

    // ── Auto-rewrite rules ───────────────────────────────────────────────────

    private void RebuildRules()
    {
        RulesContainer.Children.Clear();
        var s = SettingsService.Instance.Current.Llm;

        for (int i = 0; i < s.AutoRewriteRules.Count; i++)
        {
            int index = i;
            var rule = s.AutoRewriteRules[i];
            bool isEditing = (index == _editingRuleIndex);

            if (isEditing)
            {
                // ── Edit / create mode ──
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
                        // New rule: cancel = delete
                        var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
                        if (index < rules.Count)
                        {
                            rules.RemoveAt(index);
                            SettingsService.Instance.Save();
                        }
                    }
                    else
                    {
                        // Existing rule: revert values
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

                var card = new SettingsCard
                {
                    ContentAlignment = ContentAlignment.Vertical,
                    Content = row
                };
                RulesContainer.Children.Add(card);
            }
            else
            {
                // ── Read-only mode ──
                string profileName = rule.ProfileName;

                // Profile ComboBox — auto-save on selection change
                var profileCombo = new ComboBox { MinWidth = 160 };
                int selectedIdx = -1;
                for (int p = 0; p < s.Profiles.Count; p++)
                {
                    profileCombo.Items.Add(new ComboBoxItem { Content = s.Profiles[p].Name });
                    if (string.Equals(s.Profiles[p].Name, rule.ProfileName, StringComparison.OrdinalIgnoreCase))
                        selectedIdx = p;
                }
                profileCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

                // Declare card first so handlers can reference it
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
                RulesContainer.Children.Add(card);
            }
        }
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
        // Don't save yet — user must confirm via Save button
        _editingRuleIndex = s.AutoRewriteRules.Count - 1;
        _isNewRule = true;
        _loading = true;
        RebuildRules();
        _loading = false;
    }

    // ── Profiles ─────────────────────────────────────────────────────────────

    private void RebuildProfiles()
    {
        ProfilesContainer.Children.Clear();
        var s = SettingsService.Instance.Current.Llm;

        for (int i = 0; i < s.Profiles.Count; i++)
        {
            int index = i;
            var profile = s.Profiles[i];
            bool isNew = (index == _newProfileIndex);

            // ── Saved state for Save/Cancel ──
            string savedName = profile.Name;
            string savedModel = profile.Model;
            string savedPrompt = profile.SystemPrompt;

            // ── Actions panel (shared across all fields) ──
            var saveBtn = new Button
            {
                Content = "Save",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            var cancelBtn = new Button { Content = "Cancel" };
            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                // Visible immediately for new profiles, collapsed otherwise
                Visibility = isNew ? Visibility.Visible : Visibility.Collapsed
            };
            actionsPanel.Children.Add(saveBtn);
            actionsPanel.Children.Add(cancelBtn);

            // ── Editable fields ──
            var nameBox = new TextBox
            {
                Text = profile.Name,
                PlaceholderText = "Profile name",
                Header = "Name",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var modelBox = new TextBox
            {
                Text = profile.Model,
                PlaceholderText = "Ollama model name",
                Header = "Model",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var promptBox = new TextBox
            {
                Text = profile.SystemPrompt,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 100,
                MaxHeight = 300,
                Header = "System prompt",
                PlaceholderText = "System prompt sent with every rewrite request...",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Shared dirty-check for all three fields
            void CheckDirty()
            {
                if (_loading) return;
                bool dirty = nameBox.Text != savedName
                          || modelBox.Text != savedModel
                          || promptBox.Text != savedPrompt;
                actionsPanel.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            }

            nameBox.TextChanged += (_, _) => CheckDirty();
            modelBox.TextChanged += (_, _) => CheckDirty();
            promptBox.TextChanged += (_, _) => CheckDirty();

            saveBtn.Click += (_, _) =>
            {
                var profiles = SettingsService.Instance.Current.Llm.Profiles;
                if (index < profiles.Count)
                {
                    profiles[index].Name = nameBox.Text.Trim();
                    profiles[index].Model = modelBox.Text.Trim();
                    profiles[index].SystemPrompt = promptBox.Text;
                    SettingsService.Instance.Save();
                }
                _newProfileIndex = -1;
                // Refresh all dependent UI
                _loading = true;
                RebuildProfiles();
                RebuildManualProfileCombo();
                RebuildRules();
                _loading = false;
            };

            cancelBtn.Click += (_, _) =>
            {
                if (isNew)
                {
                    // New profile: cancel = delete
                    var profiles = SettingsService.Instance.Current.Llm.Profiles;
                    if (index < profiles.Count)
                    {
                        profiles.RemoveAt(index);
                        SettingsService.Instance.Save();
                    }
                    _newProfileIndex = -1;
                    _loading = true;
                    RebuildProfiles();
                    RebuildManualProfileCombo();
                    RebuildRules();
                    _loading = false;
                }
                else
                {
                    // Existing profile: revert fields
                    nameBox.Text = savedName;
                    modelBox.Text = savedModel;
                    promptBox.Text = savedPrompt;
                    actionsPanel.Visibility = Visibility.Collapsed;
                }
            };

            // ── Delete button ──
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
            deleteBtn.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Delete profile",
                    Content = $"Delete \"{savedName}\"? This cannot be undone.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var profiles = SettingsService.Instance.Current.Llm.Profiles;
                    if (index < profiles.Count)
                    {
                        profiles.RemoveAt(index);
                        SettingsService.Instance.Save();
                        _loading = true;
                        RebuildProfiles();
                        RebuildManualProfileCombo();
                        RebuildRules();
                        _loading = false;
                    }
                }
            };

            // ── Expander content (inside, when expanded) ──
            var editStack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            editStack.Children.Add(nameBox);
            editStack.Children.Add(modelBox);
            editStack.Children.Add(promptBox);
            editStack.Children.Add(actionsPanel);

            var editCard = new SettingsCard
            {
                ContentAlignment = ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = editStack
            };

            var expander = new SettingsExpander
            {
                Header = string.IsNullOrWhiteSpace(profile.Name) ? "(new profile)" : profile.Name,
                Description = string.IsNullOrWhiteSpace(profile.Model) ? "" : profile.Model,
                Content = deleteBtn,
                IsExpanded = isNew
            };
            expander.Items.Add(editCard);

            ProfilesContainer.Children.Add(expander);
        }
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance.Current.Llm;
        s.Profiles.Add(new RewriteProfile
        {
            Name = "",
            Model = "",
            SystemPrompt = ""
        });
        // Don't save yet — user must confirm via Save button
        _newProfileIndex = s.Profiles.Count - 1;
        _loading = true;
        RebuildProfiles();
        RebuildManualProfileCombo();
        RebuildRules();
        _loading = false;
    }

    // ── Reset all ────────────────────────────────────────────────────────────

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Current.Llm = new LlmSettings();
        SettingsService.Instance.Save();
        Hydrate();
    }
}
