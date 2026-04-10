using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinRT.Interop;

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

    // Ollama state — populated on page navigation.
    private OllamaService? _ollamaService;
    private List<OllamaModel> _ollamaModels = new();
    private bool _ollamaAvailable;

    // Discrete context size steps in K (power-of-2 scale).
    private static readonly int[] CtxKSteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };

    public LlmPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Hydrate();
        await RefreshOllamaStateAsync();
    }

    private async Task RefreshOllamaStateAsync()
    {
        _ollamaService = new OllamaService(() => SettingsService.Instance.Current.Llm.OllamaEndpoint);

        _ollamaAvailable = await _ollamaService.IsAvailableAsync();
        if (_ollamaAvailable)
        {
            try { _ollamaModels = await _ollamaService.ListModelsAsync(); }
            catch { _ollamaModels = new(); }
        }
        else
        {
            _ollamaModels = new();
        }

        // Update InfoBar
        OllamaStatusBar.Title = "Ollama is not reachable";
        OllamaStatusBar.Severity = InfoBarSeverity.Warning;
        string ep = SettingsService.Instance.Current.Llm.OllamaEndpoint;
        OllamaStatusBar.Message = $"Start Ollama or check the endpoint setting ({ep}).";
        OllamaStatusBar.IsOpen = !_ollamaAvailable;

        // Rebuild models list and profiles (model ComboBoxes)
        _loading = true;
        RebuildModels();
        RebuildProfiles();
        _loading = false;
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
            double? savedTemp = profile.Temperature;
            int? savedNumCtxK = profile.NumCtxK;
            double? savedTopP = profile.TopP;
            double? savedRepeatPenalty = profile.RepeatPenalty;

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

            // Model — ComboBox if Ollama available, TextBox fallback otherwise
            FrameworkElement modelControl;
            Func<string> getModelValue;

            if (_ollamaAvailable && _ollamaModels.Count > 0)
            {
                var modelCombo = new ComboBox
                {
                    Header = "Model",
                    MinWidth = 300,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = "Select a model..."
                };
                int selectedIdx = -1;
                for (int m = 0; m < _ollamaModels.Count; m++)
                {
                    modelCombo.Items.Add(new ComboBoxItem { Content = _ollamaModels[m].Name });
                    if (string.Equals(_ollamaModels[m].Name, profile.Model, StringComparison.OrdinalIgnoreCase))
                        selectedIdx = m;
                }
                // If current model not in list, add it as a manual entry
                if (selectedIdx < 0 && !string.IsNullOrWhiteSpace(profile.Model))
                {
                    modelCombo.Items.Add(new ComboBoxItem { Content = profile.Model });
                    selectedIdx = modelCombo.Items.Count - 1;
                }
                modelCombo.SelectedIndex = selectedIdx;
                modelControl = modelCombo;
                getModelValue = () =>
                    (modelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            }
            else
            {
                var modelBox = new TextBox
                {
                    Text = profile.Model,
                    PlaceholderText = "Ollama model name",
                    Header = "Model",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsEnabled = _ollamaAvailable || _ollamaService == null // enabled if not yet checked
                };
                modelControl = modelBox;
                getModelValue = () => ((TextBox)modelControl).Text.Trim();
            }

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

            // ── Advanced parameters ──
            var tempBox = new NumberBox
            {
                Header = "Temperature",
                Minimum = 0, Maximum = 2, SmallChange = 0.05, LargeChange = 0.1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                PlaceholderText = "Default (0.8)",
                Value = profile.Temperature ?? double.NaN,
                MinWidth = 160
            };

            // Context slider (log2 scale: 0→1K, 1→2K, ..., 8→256K)
            int ctxSliderIdx = profile.NumCtxK.HasValue
                ? Array.IndexOf(CtxKSteps, profile.NumCtxK.Value) is int idx and >= 0 ? idx : 5
                : -1; // -1 = default (not set)

            var ctxCheckBox = new CheckBox
            {
                Content = "Set context size",
                IsChecked = profile.NumCtxK.HasValue
            };

            var ctxSlider = new Slider
            {
                Minimum = 0, Maximum = CtxKSteps.Length - 1,
                StepFrequency = 1, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
                Value = ctxSliderIdx >= 0 ? ctxSliderIdx : 5, // default visual at 32K
                IsEnabled = profile.NumCtxK.HasValue,
                MinWidth = 200
            };

            var ctxLabel = new TextBlock
            {
                Text = profile.NumCtxK.HasValue ? $"{profile.NumCtxK.Value}K" : "Default (2K)",
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            };

            ctxSlider.ValueChanged += (_, _) =>
            {
                if (_loading) return;
                int stepIdx = (int)ctxSlider.Value;
                if (stepIdx >= 0 && stepIdx < CtxKSteps.Length)
                    ctxLabel.Text = $"{CtxKSteps[stepIdx]}K";
            };

            ctxCheckBox.Checked += (_, _) =>
            {
                if (_loading) return;
                ctxSlider.IsEnabled = true;
                int stepIdx = (int)ctxSlider.Value;
                if (stepIdx >= 0 && stepIdx < CtxKSteps.Length)
                    ctxLabel.Text = $"{CtxKSteps[stepIdx]}K";
            };

            ctxCheckBox.Unchecked += (_, _) =>
            {
                if (_loading) return;
                ctxSlider.IsEnabled = false;
                ctxLabel.Text = "Default (2K)";
            };

            var ctxRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            ctxRow.Children.Add(ctxCheckBox);
            ctxRow.Children.Add(ctxSlider);
            ctxRow.Children.Add(ctxLabel);

            var topPBox = new NumberBox
            {
                Header = "Top P",
                Minimum = 0, Maximum = 1, SmallChange = 0.05, LargeChange = 0.1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                PlaceholderText = "Default (0.9)",
                Value = profile.TopP ?? double.NaN,
                MinWidth = 160
            };

            var repeatBox = new NumberBox
            {
                Header = "Repeat penalty",
                Minimum = 0.5, Maximum = 2, SmallChange = 0.05, LargeChange = 0.1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                PlaceholderText = "Default (1.1)",
                Value = profile.RepeatPenalty ?? double.NaN,
                MinWidth = 160
            };

            // Advanced params in a sub-expander
            var paramsGrid = new StackPanel { Spacing = 12 };
            paramsGrid.Children.Add(tempBox);
            paramsGrid.Children.Add(ctxRow);
            paramsGrid.Children.Add(topPBox);
            paramsGrid.Children.Add(repeatBox);

            var paramsExpander = new Expander
            {
                Header = "Advanced parameters",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = paramsGrid
            };

            // ── Dirty check — includes all fields ──
            Func<double?> getTemp = () => double.IsNaN(tempBox.Value) ? null : (double?)tempBox.Value;
            Func<int?> getCtxK = () => ctxCheckBox.IsChecked == true
                ? CtxKSteps[(int)ctxSlider.Value]
                : null;
            Func<double?> getTopP = () => double.IsNaN(topPBox.Value) ? null : (double?)topPBox.Value;
            Func<double?> getRepeat = () => double.IsNaN(repeatBox.Value) ? null : (double?)repeatBox.Value;

            void CheckDirty()
            {
                if (_loading) return;
                bool dirty = nameBox.Text != savedName
                          || getModelValue() != savedModel
                          || promptBox.Text != savedPrompt
                          || getTemp() != savedTemp
                          || getCtxK() != savedNumCtxK
                          || getTopP() != savedTopP
                          || getRepeat() != savedRepeatPenalty;
                actionsPanel.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            }

            nameBox.TextChanged += (_, _) => CheckDirty();
            promptBox.TextChanged += (_, _) => CheckDirty();
            tempBox.ValueChanged += (_, _) => CheckDirty();
            ctxSlider.ValueChanged += (_, _) => CheckDirty();
            ctxCheckBox.Checked += (_, _) => CheckDirty();
            ctxCheckBox.Unchecked += (_, _) => CheckDirty();
            topPBox.ValueChanged += (_, _) => CheckDirty();
            repeatBox.ValueChanged += (_, _) => CheckDirty();

            if (modelControl is ComboBox mc)
                mc.SelectionChanged += (_, _) => CheckDirty();
            else if (modelControl is TextBox mt)
                mt.TextChanged += (_, _) => CheckDirty();

            // ── Save ──
            saveBtn.Click += (_, _) =>
            {
                var profiles = SettingsService.Instance.Current.Llm.Profiles;
                if (index < profiles.Count)
                {
                    profiles[index].Name = nameBox.Text.Trim();
                    profiles[index].Model = getModelValue();
                    profiles[index].SystemPrompt = promptBox.Text;
                    profiles[index].Temperature = getTemp();
                    profiles[index].NumCtxK = getCtxK();
                    profiles[index].TopP = getTopP();
                    profiles[index].RepeatPenalty = getRepeat();
                    SettingsService.Instance.Save();
                }
                _newProfileIndex = -1;
                _loading = true;
                RebuildProfiles();
                RebuildManualProfileCombo();
                RebuildRules();
                _loading = false;
            };

            // ── Cancel ──
            cancelBtn.Click += (_, _) =>
            {
                if (isNew)
                {
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
                    // Revert all fields
                    nameBox.Text = savedName;
                    promptBox.Text = savedPrompt;
                    tempBox.Value = savedTemp ?? double.NaN;
                    topPBox.Value = savedTopP ?? double.NaN;
                    repeatBox.Value = savedRepeatPenalty ?? double.NaN;
                    ctxCheckBox.IsChecked = savedNumCtxK.HasValue;
                    ctxSlider.IsEnabled = savedNumCtxK.HasValue;
                    if (savedNumCtxK.HasValue)
                    {
                        int si = Array.IndexOf(CtxKSteps, savedNumCtxK.Value);
                        ctxSlider.Value = si >= 0 ? si : 5;
                        ctxLabel.Text = $"{savedNumCtxK.Value}K";
                    }
                    else
                    {
                        ctxLabel.Text = "Default (2K)";
                    }
                    if (modelControl is ComboBox rmc)
                    {
                        for (int m = 0; m < rmc.Items.Count; m++)
                            if ((rmc.Items[m] as ComboBoxItem)?.Content?.ToString() == savedModel)
                            { rmc.SelectedIndex = m; break; }
                    }
                    else if (modelControl is TextBox rmt)
                        rmt.Text = savedModel;
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

            // ── Expander content ──
            var editStack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            editStack.Children.Add(nameBox);
            editStack.Children.Add(modelControl);
            editStack.Children.Add(promptBox);
            editStack.Children.Add(paramsExpander);
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

    // ── Custom models ──────────────────────────────────────────────────────

    private void RebuildModels()
    {
        ModelsContainer.Children.Clear();

        bool enabled = _ollamaAvailable;
        ImportGgufButton.IsEnabled = enabled;
        RefreshModelsButton.IsEnabled = enabled;

        if (!enabled) return;

        foreach (var model in _ollamaModels)
        {
            string sizeText = model.Size > 0
                ? $"{model.Size / (1024.0 * 1024 * 1024):F1} GB"
                : "";

            var card = new SettingsCard
            {
                Header = model.Name,
                Description = sizeText
            };

            var delBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                        new TextBlock { Text = "Delete" }
                    }
                }
            };
            string modelName = model.Name;
            delBtn.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Delete model",
                    Content = $"Delete \"{modelName}\" from Ollama? This cannot be undone.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        await _ollamaService!.DeleteModelAsync(modelName);
                        await RefreshOllamaStateAsync();
                    }
                    catch (Exception ex)
                    {
                        OllamaStatusBar.Title = "Error deleting model";
                        OllamaStatusBar.Message = ex.Message;
                        OllamaStatusBar.Severity = InfoBarSeverity.Error;
                        OllamaStatusBar.IsOpen = true;
                    }
                }
            };

            card.Content = delBtn;
            ModelsContainer.Children.Add(card);
        }

        if (_ollamaModels.Count == 0)
        {
            ModelsContainer.Children.Add(new TextBlock
            {
                Text = "No models found in Ollama. Pull a model or import a GGUF file.",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(1, 4, 0, 0)
            });
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOllamaStateAsync();
    }

    private async void ImportGguf_Click(object sender, RoutedEventArgs e)
    {
        if (_ollamaService == null || !_ollamaAvailable) return;

        // ── Build the import dialog content ──
        var nameBox = new TextBox
        {
            Header = "Model name",
            PlaceholderText = "my-model:latest",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var pathBox = new TextBox
        {
            Header = "GGUF file path",
            PlaceholderText = @"D:\models\model.gguf",
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var browseBtn = new Button { Content = "Browse..." };
        browseBtn.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            // Initialize with the window handle (required for WinUI 3 desktop)
            var hwnd = WindowNative.GetWindowHandle(App.SettingsWin!);
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".gguf");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                pathBox.Text = file.Path;
        };

        var pathRow = new StackPanel { Spacing = 8 };
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browseBtn);

        // Template preset + editable text
        var templateCombo = new ComboBox
        {
            Header = "Chat template",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var name in ChatTemplates.Names)
            templateCombo.Items.Add(new ComboBoxItem { Content = name });
        templateCombo.Items.Add(new ComboBoxItem { Content = "Custom" });
        templateCombo.SelectedIndex = 0; // Mistral v0.3 default

        var templateBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 200,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Text = ChatTemplates.Templates[ChatTemplates.Names[0]],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        bool _suppressTemplateSync = false;
        templateCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressTemplateSync) return;
            if (templateCombo.SelectedItem is ComboBoxItem item)
            {
                string selected = item.Content?.ToString() ?? "";
                if (ChatTemplates.Templates.TryGetValue(selected, out string? tmpl))
                {
                    _suppressTemplateSync = true;
                    templateBox.Text = tmpl;
                    _suppressTemplateSync = false;
                }
            }
        };
        templateBox.TextChanged += (_, _) =>
        {
            if (_suppressTemplateSync) return;
            // User edited the template manually — switch combo to "Custom"
            _suppressTemplateSync = true;
            templateCombo.SelectedIndex = templateCombo.Items.Count - 1;
            _suppressTemplateSync = false;
        };

        var systemPromptBox = new TextBox
        {
            Header = "System prompt (optional)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 150,
            PlaceholderText = "Optional — can also be set per-profile",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Parameters
        var tempNb = new NumberBox
        {
            Header = "Temperature",
            Minimum = 0, Maximum = 2, SmallChange = 0.05,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            PlaceholderText = "Default (0.8)",
            Value = double.NaN, MinWidth = 140
        };

        var ctxCombo = new ComboBox { Header = "Context size" };
        ctxCombo.Items.Add(new ComboBoxItem { Content = "Default" });
        foreach (int k in CtxKSteps)
            ctxCombo.Items.Add(new ComboBoxItem { Content = $"{k}K" });
        ctxCombo.SelectedIndex = 0;

        var paramsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        paramsRow.Children.Add(tempNb);
        paramsRow.Children.Add(ctxCombo);

        var progressRing = new ProgressRing
        {
            IsActive = false,
            Width = 20, Height = 20,
            Visibility = Visibility.Collapsed
        };

        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsOpen = false,
            IsClosable = true
        };

        // Layout
        var content = new StackPanel { Spacing = 12, MinWidth = 450 };
        content.Children.Add(nameBox);
        content.Children.Add(pathRow);
        content.Children.Add(templateCombo);
        content.Children.Add(templateBox);
        content.Children.Add(systemPromptBox);
        content.Children.Add(paramsRow);
        content.Children.Add(progressRing);
        content.Children.Add(errorBar);

        var dialog = new ContentDialog
        {
            Title = "Import GGUF model",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            // Prevent dialog from closing while creating
            var deferral = args.GetDeferral();

            string modelName = nameBox.Text.Trim();
            string ggufPath = pathBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(ggufPath))
            {
                errorBar.Title = "Missing fields";
                errorBar.Message = "Model name and GGUF file path are required.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                deferral.Complete();
                return;
            }

            // Ollama model names: alphanumeric, dots, dashes, underscores,
            // with an optional :tag suffix following the same rules.
            if (!System.Text.RegularExpressions.Regex.IsMatch(modelName,
                    @"^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[a-zA-Z0-9._-]+)?$"))
            {
                errorBar.Title = "Invalid model name";
                errorBar.Message = "Use only letters, digits, dots, dashes, and underscores. "
                                 + "Optional :tag suffix (e.g. my-model:latest).";
                errorBar.IsOpen = true;
                args.Cancel = true;
                deferral.Complete();
                return;
            }

            if (!System.IO.File.Exists(ggufPath))
            {
                errorBar.Title = "File not found";
                errorBar.Message = $"GGUF file not found: {ggufPath}";
                errorBar.IsOpen = true;
                args.Cancel = true;
                deferral.Complete();
                return;
            }

            // Build Modelfile
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"FROM {ggufPath}");
            sb.AppendLine($"TEMPLATE \"\"\"{templateBox.Text}\"\"\"");
            if (!string.IsNullOrWhiteSpace(systemPromptBox.Text))
                sb.AppendLine($"SYSTEM \"\"\"{systemPromptBox.Text}\"\"\"");
            if (!double.IsNaN(tempNb.Value))
                sb.AppendLine($"PARAMETER temperature {tempNb.Value}");
            if (ctxCombo.SelectedIndex > 0) // 0 = Default
            {
                int ctxK = CtxKSteps[ctxCombo.SelectedIndex - 1];
                sb.AppendLine($"PARAMETER num_ctx {ctxK * 1024}");
            }

            progressRing.IsActive = true;
            progressRing.Visibility = Visibility.Visible;
            errorBar.IsOpen = false;

            try
            {
                await _ollamaService!.CreateModelAsync(modelName, sb.ToString());
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                deferral.Complete();
                // Refresh after dialog closes
                await RefreshOllamaStateAsync();
            }
            catch (Exception ex)
            {
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                errorBar.Title = "Creation failed";
                errorBar.Message = ex.Message;
                errorBar.IsOpen = true;
                args.Cancel = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    // ── Reset all ────────────────────────────────────────────────────────────

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Current.Llm = new LlmSettings();
        SettingsService.Instance.Save();
        Hydrate();
    }
}
