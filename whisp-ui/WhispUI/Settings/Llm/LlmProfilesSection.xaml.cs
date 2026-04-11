using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace WhispUI.Settings.Llm;

// ─── Section Profiles de LlmPage ───────────────────────────────────────────
//
// Liste des profils de réécriture (nom + modèle Ollama + system prompt + params).
// Cards dynamiques avec edit inline via SettingsExpander + dirty check sur les
// champs pour afficher Save/Cancel uniquement quand il y a des changements.
//
// Dépend de LlmOllamaContext pour savoir si Ollama est dispo (sinon fallback
// TextBox pour le champ modèle, au lieu de la ComboBox). S'abonne à
// StateChanged pour reconstruire les cards quand la liste de modèles change.
//
// Émet ProfilesChanged quand la liste est mutée (add/save/remove) — le host
// relaie l'événement vers LlmRulesSection et LlmManualShortcutSection pour
// qu'elles rafraîchissent leurs ComboBox de profil.

public sealed partial class LlmProfilesSection : UserControl
{
    private bool _loading;
    private int _newProfileIndex = -1;
    private LlmOllamaContext? _context;

    // Échelons de taille de contexte en K (puissances de 2).
    private static readonly int[] CtxKSteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };

    // Defaults affichés quand le profil n'a pas encore de valeur custom. Si
    // l'utilisateur ne touche pas au slider, c'est ce qu'on sauve au Save —
    // ce qui a le même effet côté Ollama que le comportement null précédent.
    private const double DefaultTemperature = 0.8;
    private const int    DefaultNumCtxK     = 2;

    public event EventHandler? ProfilesChanged;

    public LlmProfilesSection()
    {
        InitializeComponent();
    }

    internal void Initialize(LlmOllamaContext context)
    {
        if (_context != null)
            _context.StateChanged -= OnContextStateChanged;

        _context = context;
        _context.StateChanged += OnContextStateChanged;
    }

    private void OnContextStateChanged(object? sender, EventArgs e) => Reload();

    public void Reload()
    {
        _loading = true;
        _newProfileIndex = -1;
        RebuildProfiles();
        _loading = false;
    }

    private void RebuildProfiles()
    {
        ProfilesContainer.Children.Clear();
        var s = SettingsService.Instance.Current.Llm;

        for (int i = 0; i < s.Profiles.Count; i++)
        {
            ProfilesContainer.Children.Add(BuildProfileExpander(i, s.Profiles[i]));
        }
    }

    // Résumé affiché dans la Description de l'expander (ligne sous le nom).
    // Donne en un coup d'œil le modèle, la taille de contexte et la température
    // — les deux paramètres qui comptent le plus dans la façon dont le profil
    // se comporte au runtime.
    private static string BuildProfileSummary(RewriteProfile p)
    {
        string model = string.IsNullOrWhiteSpace(p.Model) ? "(no model)" : p.Model;
        int ctxK = p.NumCtxK ?? DefaultNumCtxK;
        double temp = p.Temperature ?? DefaultTemperature;
        return $"{model}  ·  {ctxK}K ctx  ·  temp {temp:F2}";
    }

    private SettingsExpander BuildProfileExpander(int index, RewriteProfile profile)
    {
        bool isNew = (index == _newProfileIndex);

        // ── Saved state for Save/Cancel ──
        string savedName = profile.Name;
        string savedModel = profile.Model;
        string savedPrompt = profile.SystemPrompt;
        double savedTemp = profile.Temperature ?? DefaultTemperature;
        int savedNumCtxK = profile.NumCtxK ?? DefaultNumCtxK;
        double? savedTopP = profile.TopP;
        double? savedRepeatPenalty = profile.RepeatPenalty;

        // ── Actions panel ──
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

        bool ollamaAvailable = _context?.Available ?? false;
        var ollamaModels = _context?.Models ?? Array.Empty<OllamaModel>();

        if (ollamaAvailable && ollamaModels.Count > 0)
        {
            var modelCombo = new ComboBox
            {
                Header = "Model",
                MinWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Select a model..."
            };
            int selectedIdx = -1;
            for (int m = 0; m < ollamaModels.Count; m++)
            {
                modelCombo.Items.Add(new ComboBoxItem { Content = ollamaModels[m].Name });
                if (string.Equals(ollamaModels[m].Name, profile.Model, StringComparison.OrdinalIgnoreCase))
                    selectedIdx = m;
            }
            // Si le modèle courant n'est plus dans la liste Ollama (renommé
            // / supprimé), on l'ajoute quand même pour ne pas écraser la
            // valeur à la prochaine Save.
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
                IsEnabled = ollamaAvailable || _context?.Service == null
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

        // ── Sliders principaux : Temperature + Context size ───────────────
        // Sortis de "Advanced parameters" pour être visibles en permanence
        // parce que ce sont les deux paramètres qui changent concrètement le
        // comportement perçu au runtime. Top P et Repeat penalty restent en
        // Advanced — rarement touchés en pratique.

        var tempSlider = new Slider
        {
            Header = "Temperature",
            Minimum = 0.0, Maximum = 2.0,
            StepFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.1,
            Value = savedTemp,
            MinWidth = 280,
            ThumbToolTipValueConverter = new TempTooltipConverter()
        };
        var tempLabel = new TextBlock
        {
            Text = savedTemp.ToString("F2"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        };
        tempSlider.ValueChanged += (_, _) => tempLabel.Text = tempSlider.Value.ToString("F2");

        var tempRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        tempRow.Children.Add(tempSlider);
        tempRow.Children.Add(tempLabel);

        int ctxSliderIdx = Array.IndexOf(CtxKSteps, savedNumCtxK);
        if (ctxSliderIdx < 0) ctxSliderIdx = Array.IndexOf(CtxKSteps, DefaultNumCtxK);

        var ctxSlider = new Slider
        {
            Header = "Context size",
            Minimum = 0, Maximum = CtxKSteps.Length - 1,
            StepFrequency = 1,
            SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
            Value = ctxSliderIdx,
            MinWidth = 280,
            // Converter crucial — sans lui, le tooltip du thumb affiche l'index
            // brut (0..8) au lieu de la valeur réelle (1K..256K). Bug "tooltip
            // affiche 5" rapporté précédemment.
            ThumbToolTipValueConverter = new CtxTooltipConverter()
        };
        var ctxLabel = new TextBlock
        {
            Text = $"{CtxKSteps[ctxSliderIdx]}K",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
        };
        ctxSlider.ValueChanged += (_, _) =>
        {
            int si = (int)ctxSlider.Value;
            if (si >= 0 && si < CtxKSteps.Length)
                ctxLabel.Text = $"{CtxKSteps[si]}K";
        };

        var ctxRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ctxRow.Children.Add(ctxSlider);
        ctxRow.Children.Add(ctxLabel);

        // ── Advanced : Top P + Repeat penalty ─────────────────────────────
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

        var advancedStack = new StackPanel { Spacing = 12 };
        advancedStack.Children.Add(topPBox);
        advancedStack.Children.Add(repeatBox);

        var advancedExpander = new Expander
        {
            Header = "Advanced parameters",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = advancedStack
        };

        // ── Dirty check ──
        double getTemp() => tempSlider.Value;
        int getCtxK() => CtxKSteps[(int)ctxSlider.Value];
        Func<double?> getTopP = () => double.IsNaN(topPBox.Value) ? null : (double?)topPBox.Value;
        Func<double?> getRepeat = () => double.IsNaN(repeatBox.Value) ? null : (double?)repeatBox.Value;

        void CheckDirty()
        {
            if (_loading) return;
            bool dirty = nameBox.Text != savedName
                      || getModelValue() != savedModel
                      || promptBox.Text != savedPrompt
                      || Math.Abs(getTemp() - savedTemp) > 1e-6
                      || getCtxK() != savedNumCtxK
                      || getTopP() != savedTopP
                      || getRepeat() != savedRepeatPenalty;
            actionsPanel.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        }

        nameBox.TextChanged += (_, _) => CheckDirty();
        promptBox.TextChanged += (_, _) => CheckDirty();
        tempSlider.ValueChanged += (_, _) => CheckDirty();
        ctxSlider.ValueChanged += (_, _) => CheckDirty();
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
            _loading = false;
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
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
                _loading = false;
                ProfilesChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                nameBox.Text = savedName;
                promptBox.Text = savedPrompt;
                tempSlider.Value = savedTemp;
                tempLabel.Text = savedTemp.ToString("F2");
                int si = Array.IndexOf(CtxKSteps, savedNumCtxK);
                ctxSlider.Value = si >= 0 ? si : Array.IndexOf(CtxKSteps, DefaultNumCtxK);
                ctxLabel.Text = $"{savedNumCtxK}K";
                topPBox.Value = savedTopP ?? double.NaN;
                repeatBox.Value = savedRepeatPenalty ?? double.NaN;
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
                Title = "Remove profile",
                Content = $"Remove \"{savedName}\"? This cannot be undone.",
                PrimaryButtonText = "Remove",
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
                    _loading = false;
                    ProfilesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        };

        // ── Expander content ──
        var editStack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        editStack.Children.Add(nameBox);
        editStack.Children.Add(modelControl);
        editStack.Children.Add(promptBox);
        editStack.Children.Add(tempRow);
        editStack.Children.Add(ctxRow);
        editStack.Children.Add(advancedExpander);
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
            Description = BuildProfileSummary(profile),
            Content = deleteBtn,
            IsExpanded = isNew
        };
        expander.Items.Add(editCard);

        return expander;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance.Current.Llm;
        s.Profiles.Add(new RewriteProfile
        {
            Name = "",
            Model = "",
            SystemPrompt = "",
            Temperature = DefaultTemperature,
            NumCtxK = DefaultNumCtxK
        });
        _newProfileIndex = s.Profiles.Count - 1;
        _loading = true;
        RebuildProfiles();
        _loading = false;
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Converters pour les tooltips de slider ─────────────────────────────

    // Tooltip ctx : Slider.Value est l'index dans CtxKSteps (0..8). Sans
    // converter, le thumb affiche "5" au lieu de "32K" — c'est le bug
    // "tooltip affiche 5" rapporté.
    private sealed class CtxTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            int idx = value is double d ? (int)d : 0;
            if (idx >= 0 && idx < CtxKSteps.Length) return $"{CtxKSteps[idx]}K";
            return value;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    // Tooltip temperature : format 2 décimales pour éviter "0.15000000001".
    private sealed class TempTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is double d ? d.ToString("F2") : (value?.ToString() ?? "");
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
