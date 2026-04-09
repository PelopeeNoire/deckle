using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WhispUI.Settings;

public sealed partial class WhisperPage : Page
{
    // _loading = true pendant l'hydratation initiale pour empêcher les handlers
    // ValueChanged/Toggled de ré-écrire dans SettingsService (et de déclencher
    // un Save inutile) pendant qu'on pose les valeurs depuis le JSON.
    private bool _loading;

    // Défauts résolus à partir des POCO — source de vérité unique.
    private static readonly PathsSettings _pathsDefaults = new();
    private static readonly TranscriptionSettings _transcriptionDefaults = new();
    private static readonly SpeechDetectionSettings _speechDefaults = new();
    private static readonly DecodingSettings _decodingDefaults = new();
    private static readonly ConfidenceSettings _confidenceDefaults = new();
    private static readonly OutputFilterSettings _outputDefaults = new();
    private static readonly ContextSettings _contextDefaults = new();

    public WhisperPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) =>
        {
            PopulateModelCombo();
            Hydrate();
            // Transcription
            WireHover(ModelCard, ModelReset);
            WireHover(UseGpuCard, UseGpuReset);
            WireHover(LanguageCard, LanguageReset);
            WireHover(InitialPromptCard, InitialPromptReset);
            PathsCard.PointerEntered += (_, _) => ModelsDirectoryReset.Opacity = 1;
            PathsCard.PointerExited += (_, _) => ModelsDirectoryReset.Opacity = 0;
            // VAD
            VadEnabledCard.PointerEntered += (_, _) => VadEnabledReset.Opacity = 1;
            VadEnabledCard.PointerExited += (_, _) => VadEnabledReset.Opacity = 0;
            WireHover(VadThresholdCard, VadThresholdReset);
            WireHover(VadMinSpeechCard, VadMinSpeechReset);
            WireHover(VadMinSilenceCard, VadMinSilenceReset);
            WireHover(VadMaxSpeechCard, VadMaxSpeechReset);
            WireHover(VadSpeechPadCard, VadSpeechPadReset);
            WireHover(VadOverlapCard, VadOverlapReset);
            // Décodage / Confiance / Filtres / Contexte
            WireHover(TemperatureCard, TemperatureReset);
            WireHover(TemperatureIncrementCard, TemperatureIncrementReset);
            WireHover(EntropyCard, EntropyReset);
            WireHover(LogprobCard, LogprobReset);
            WireHover(NoSpeechCard, NoSpeechReset);
            WireHover(SuppressNstCard, SuppressNstReset);
            WireHover(SuppressBlankCard, SuppressBlankReset);
            WireHover(UseContextCard, UseContextReset);
            WireHover(MaxTokensCard, MaxTokensReset);
            SuppressRegexCard.PointerEntered += (_, _) => SuppressRegexReset.Opacity = 1;
            SuppressRegexCard.PointerExited += (_, _) => SuppressRegexReset.Opacity = 0;
        };
    }

    // Reset button visible uniquement quand la SettingsCard parente est survolée.
    private static void WireHover(SettingsCard card, Button resetButton)
    {
        card.PointerEntered += (_, _) => resetButton.Opacity = 1;
        card.PointerExited += (_, _) => resetButton.Opacity = 0;
    }

    // Scanne le dossier des modèles et peuple le ComboBox avec les .bin trouvés,
    // hors modèle Silero (celui-ci n'est pas un modèle Whisper). Si le dossier
    // n'existe pas, on tombe sur un placeholder.
    private void PopulateModelCombo()
    {
        var items = new List<string>();
        try
        {
            string dir = SettingsService.Instance.ResolveModelsDirectory();
            if (Directory.Exists(dir))
            {
                items = Directory.EnumerateFiles(dir, "*.bin")
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && !n!.Contains("silero", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n!)
                    .OrderBy(n => n)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("SETTINGS", $"model scan failed: {ex.Message}");
        }

        // Garantir que le modèle courant est présent dans la liste même si le
        // fichier n'est pas (encore) sur disque — évite que l'UI efface la
        // valeur persistée au simple affichage de la page.
        string current = SettingsService.Instance.Current.Transcription.Model;
        if (!string.IsNullOrEmpty(current) && !items.Contains(current))
            items.Insert(0, current);

        ModelCombo.ItemsSource = items;
    }

    private void Hydrate()
    {
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;

            // Transcription
            ModelsDirectoryBox.Text = s.Paths.ModelsDirectory;
            ModelCombo.SelectedItem = s.Transcription.Model;
            UseGpuToggle.IsOn = s.Transcription.UseGpu;
            LanguageCombo.Text = s.Transcription.Language;
            InitialPromptBox.Text = s.Transcription.InitialPrompt;

            // VAD
            VadEnabledToggle.IsOn = s.SpeechDetection.Enabled;
            VadThresholdSlider.Value = s.SpeechDetection.Threshold;
            UpdateVadThresholdText(s.SpeechDetection.Threshold);
            VadMinSpeechBox.Value = s.SpeechDetection.MinSpeechDurationMs;
            VadMinSilenceBox.Value = s.SpeechDetection.MinSilenceDurationMs;
            VadMaxSpeechSlider.Value = s.SpeechDetection.MaxSpeechDurationSec;
            UpdateVadMaxSpeechText(s.SpeechDetection.MaxSpeechDurationSec);
            VadSpeechPadBox.Value = s.SpeechDetection.SpeechPadMs;
            VadOverlapSlider.Value = s.SpeechDetection.SamplesOverlap;
            UpdateVadOverlapText(s.SpeechDetection.SamplesOverlap);

            // Décodage
            TemperatureSlider.Value = s.Decoding.Temperature;
            UpdateTemperatureText(s.Decoding.Temperature);
            TemperatureIncrementSlider.Value = s.Decoding.TemperatureIncrement;
            UpdateTemperatureIncrementText(s.Decoding.TemperatureIncrement);
            UpdateTemperatureIncrementWarning(s.Decoding.TemperatureIncrement);

            // Seuils de confiance
            EntropySlider.Value = s.Confidence.EntropyThreshold;
            UpdateEntropyText(s.Confidence.EntropyThreshold);
            LogprobSlider.Value = s.Confidence.LogprobThreshold;
            UpdateLogprobText(s.Confidence.LogprobThreshold);
            NoSpeechSlider.Value = s.Confidence.NoSpeechThreshold;
            UpdateNoSpeechText(s.Confidence.NoSpeechThreshold);

            // Filtres de sortie
            SuppressNstToggle.IsOn = s.OutputFilters.SuppressNonSpeechTokens;
            SuppressBlankToggle.IsOn = s.OutputFilters.SuppressBlank;
            SuppressRegexBox.Text = s.OutputFilters.SuppressRegex;

            // Contexte et segmentation
            UseContextToggle.IsOn = s.Context.UseContext;
            MaxTokensBox.Value = s.Context.MaxTokens;
        }
        finally
        {
            _loading = false;
        }
    }

    // Format d'affichage de valeur slider — 1 décimale, séparateur "." pour
    // correspondre aux conventions techniques Whisper, pas à la locale OS.
    private static string Fmt(double v) =>
        v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FmtTwo(double v) =>
        v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FmtSeconds(double v) =>
        ((int)v).ToString(CultureInfo.InvariantCulture) + "s";

    // Signale un touchup d'un réglage lourd (Modèle, UseGpu). Affiche l'InfoBar
    // "Restart required" au sommet de la page. Persistant tant que la page est
    // montée (clear au re-nav). Le footer global Cancel/Restart later/now n'est
    // pas encore branché — ce sera un chantier SettingsWindow.
    private void MarkRestartPending()
    {
        if (_loading) return;
        RestartPendingInfoBar.IsOpen = true;
    }

    // ── Transcription ───────────────────────────────────────────────────────

    private void ModelsDirectoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Paths.ModelsDirectory = ModelsDirectoryBox.Text;
        SettingsService.Instance.Save();
    }

    private void ModelsDirectoryReset_Click(object sender, RoutedEventArgs e)
    {
        ModelsDirectoryBox.Text = _pathsDefaults.ModelsDirectory;
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedItem is not string model) return;
        SettingsService.Instance.Current.Transcription.Model = model;
        SettingsService.Instance.Save();
        MarkRestartPending();
    }

    private void ModelReset_Click(object sender, RoutedEventArgs e)
    {
        ModelCombo.SelectedItem = _transcriptionDefaults.Model;
    }

    private void UseGpuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Transcription.UseGpu = UseGpuToggle.IsOn;
        SettingsService.Instance.Save();
        MarkRestartPending();
    }

    private void UseGpuReset_Click(object sender, RoutedEventArgs e)
    {
        UseGpuToggle.IsOn = _transcriptionDefaults.UseGpu;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // ComboBox editable : SelectedItem peut être un ComboBoxItem (frappe
        // sur une entrée de la liste) ou null (saisie libre dans Text). Dans
        // les deux cas on lit Text qui reflète la valeur courante affichée.
        string text = LanguageCombo.Text ?? "";
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Content is string s)
            text = s;
        SettingsService.Instance.Current.Transcription.Language = text;
        SettingsService.Instance.Save();
    }

    private void LanguageReset_Click(object sender, RoutedEventArgs e)
    {
        LanguageCombo.Text = _transcriptionDefaults.Language;
    }

    private void InitialPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Transcription.InitialPrompt = InitialPromptBox.Text;
        SettingsService.Instance.Save();
    }

    private void InitialPromptReset_Click(object sender, RoutedEventArgs e)
    {
        InitialPromptBox.Text = _transcriptionDefaults.InitialPrompt;
    }

    // ── VAD ─────────────────────────────────────────────────────────────────

    private void VadEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.SpeechDetection.Enabled = VadEnabledToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void VadEnabledReset_Click(object sender, RoutedEventArgs e)
    {
        VadEnabledToggle.IsOn = _speechDefaults.Enabled;
    }

    private void VadThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadThresholdText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.SpeechDetection.Threshold = (float)e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateVadThresholdText(double v) => VadThresholdValue.Text = FmtTwo(v);

    private void VadThresholdReset_Click(object sender, RoutedEventArgs e)
    {
        VadThresholdSlider.Value = _speechDefaults.Threshold;
    }

    private void VadMinSpeechBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        SettingsService.Instance.Current.SpeechDetection.MinSpeechDurationMs = (int)args.NewValue;
        SettingsService.Instance.Save();
    }

    private void VadMinSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        VadMinSpeechBox.Value = _speechDefaults.MinSpeechDurationMs;
    }

    private void VadMinSilenceBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        SettingsService.Instance.Current.SpeechDetection.MinSilenceDurationMs = (int)args.NewValue;
        SettingsService.Instance.Save();
    }

    private void VadMinSilenceReset_Click(object sender, RoutedEventArgs e)
    {
        VadMinSilenceBox.Value = _speechDefaults.MinSilenceDurationMs;
    }

    private void VadMaxSpeechSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadMaxSpeechText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.SpeechDetection.MaxSpeechDurationSec = (float)e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateVadMaxSpeechText(double v) => VadMaxSpeechValue.Text = FmtSeconds(v);

    private void VadMaxSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        VadMaxSpeechSlider.Value = _speechDefaults.MaxSpeechDurationSec;
    }

    private void VadSpeechPadBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        SettingsService.Instance.Current.SpeechDetection.SpeechPadMs = (int)args.NewValue;
        SettingsService.Instance.Save();
    }

    private void VadSpeechPadReset_Click(object sender, RoutedEventArgs e)
    {
        VadSpeechPadBox.Value = _speechDefaults.SpeechPadMs;
    }

    private void VadOverlapSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadOverlapText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.SpeechDetection.SamplesOverlap = (float)e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateVadOverlapText(double v) => VadOverlapValue.Text = FmtTwo(v);

    private void VadOverlapReset_Click(object sender, RoutedEventArgs e)
    {
        VadOverlapSlider.Value = _speechDefaults.SamplesOverlap;
    }

    // ── Décodage ─────────────────────────────────────────────────────────────

    private void TemperatureSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateTemperatureText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.Decoding.Temperature = e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateTemperatureText(double v) => TemperatureValue.Text = Fmt(v);

    private void TemperatureReset_Click(object sender, RoutedEventArgs e)
    {
        TemperatureSlider.Value = _decodingDefaults.Temperature;
    }

    private void TemperatureIncrementSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateTemperatureIncrementText(e.NewValue);
        UpdateTemperatureIncrementWarning(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.Decoding.TemperatureIncrement = e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateTemperatureIncrementText(double v) => TemperatureIncrementValue.Text = Fmt(v);

    private void UpdateTemperatureIncrementWarning(double v) =>
        TemperatureIncrementWarning.IsOpen = v == 0.0;

    private void TemperatureIncrementReset_Click(object sender, RoutedEventArgs e)
    {
        TemperatureIncrementSlider.Value = _decodingDefaults.TemperatureIncrement;
    }

    // ── Seuils de confiance ─────────────────────────────────────────────────

    private void EntropySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateEntropyText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.Confidence.EntropyThreshold = e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateEntropyText(double v) => EntropyValue.Text = Fmt(v);

    private void EntropyReset_Click(object sender, RoutedEventArgs e)
    {
        EntropySlider.Value = _confidenceDefaults.EntropyThreshold;
    }

    private void LogprobSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateLogprobText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.Confidence.LogprobThreshold = e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateLogprobText(double v) => LogprobValue.Text = Fmt(v);

    private void LogprobReset_Click(object sender, RoutedEventArgs e)
    {
        LogprobSlider.Value = _confidenceDefaults.LogprobThreshold;
    }

    private void NoSpeechSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateNoSpeechText(e.NewValue);
        if (_loading) return;
        SettingsService.Instance.Current.Confidence.NoSpeechThreshold = e.NewValue;
        SettingsService.Instance.Save();
    }

    private void UpdateNoSpeechText(double v) => NoSpeechValue.Text = FmtTwo(v);

    private void NoSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        NoSpeechSlider.Value = _confidenceDefaults.NoSpeechThreshold;
    }

    // ── Filtres de sortie ───────────────────────────────────────────────────

    private void SuppressNstToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.OutputFilters.SuppressNonSpeechTokens = SuppressNstToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void SuppressNstReset_Click(object sender, RoutedEventArgs e)
    {
        SuppressNstToggle.IsOn = _outputDefaults.SuppressNonSpeechTokens;
    }

    private void SuppressBlankToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.OutputFilters.SuppressBlank = SuppressBlankToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void SuppressBlankReset_Click(object sender, RoutedEventArgs e)
    {
        SuppressBlankToggle.IsOn = _outputDefaults.SuppressBlank;
    }

    private void SuppressRegexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.OutputFilters.SuppressRegex = SuppressRegexBox.Text;
        SettingsService.Instance.Save();
    }

    private void SuppressRegexReset_Click(object sender, RoutedEventArgs e)
    {
        SuppressRegexBox.Text = _outputDefaults.SuppressRegex;
    }

    // ── Contexte et segmentation ────────────────────────────────────────────

    private void UseContextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Current.Context.UseContext = UseContextToggle.IsOn;
        SettingsService.Instance.Save();
    }

    private void UseContextReset_Click(object sender, RoutedEventArgs e)
    {
        UseContextToggle.IsOn = _contextDefaults.UseContext;
    }

    private void MaxTokensBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        SettingsService.Instance.Current.Context.MaxTokens = (int)args.NewValue;
        SettingsService.Instance.Save();
    }

    private void MaxTokensReset_Click(object sender, RoutedEventArgs e)
    {
        MaxTokensBox.Value = _contextDefaults.MaxTokens;
    }
}
