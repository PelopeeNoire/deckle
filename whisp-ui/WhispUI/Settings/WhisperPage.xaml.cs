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
        App.Log?.Log("[WHISPERPAGE] ctor start");
        try
        {
            InitializeComponent();
            App.Log?.LogVerbose("[WHISPERPAGE] InitializeComponent OK");
            // Bug WinUI 3 release : impossible de poser Minimum > defaultValue
            // en XAML pour VadMaxSpeechSlider sans crash du parser. On le fait
            // ici maintenant que le Slider est construit et en-dehors du
            // chemin LoadComponent. Maximum=60 vient déjà du XAML.
            VadMaxSpeechSlider.Minimum = 5;
            VadMaxSpeechSlider.Value   = 5;
            App.Log?.LogVerbose("[WHISPERPAGE] VadMaxSpeechSlider Min/Value posés en code");
        }
        catch (Exception ex)
        {
            App.Log?.LogError($"[WHISPERPAGE] InitializeComponent THREW {ex.GetType().Name}: {ex.Message}");
            App.Log?.LogError(ex.StackTrace ?? "(no stack)");
            DebugLog.Write("WHISPERPAGE", $"InitializeComponent THREW: {ex}");
            throw;
        }
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) =>
        {
            App.Log?.LogVerbose("[WHISPERPAGE] Loaded fired");
            try
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
                App.Log?.LogStep("[WHISPERPAGE] Loaded complet — page prête");
            }
            catch (Exception ex)
            {
                App.Log?.LogError($"[WHISPERPAGE] Loaded THREW {ex.GetType().Name}: {ex.Message}");
                App.Log?.LogError(ex.StackTrace ?? "(no stack)");
                DebugLog.Write("WHISPERPAGE", $"Loaded THREW: {ex}");
            }
        };
    }

    // Helper commun pour instrumenter chaque handler utilisateur :
    //   - log Verbose de l'action tentée (avec la valeur)
    //   - exécute l'action
    //   - log Error si exception (l'UI a déjà changé, mais SettingsService.Save
    //     peut lever sur I/O — on loggue la trace sans masquer l'exception)
    // Retourne false si une exception a été capturée, pour que l'appelant puisse
    // éventuellement rollback sa valeur UI.
    private bool TryApply(string action, System.Action body)
    {
        App.Log?.LogVerbose($"[WHISPERPAGE] {action}");
        try
        {
            body();
            return true;
        }
        catch (Exception ex)
        {
            App.Log?.LogError($"[WHISPERPAGE] {action} THREW {ex.GetType().Name}: {ex.Message}");
            App.Log?.LogError(ex.StackTrace ?? "(no stack)");
            DebugLog.Write("WHISPERPAGE", $"{action} THREW: {ex}");
            return false;
        }
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
        TryApply($"Paths.ModelsDirectory ← \"{ModelsDirectoryBox.Text}\"", () =>
        {
            SettingsService.Instance.Current.Paths.ModelsDirectory = ModelsDirectoryBox.Text;
            SettingsService.Instance.Save();
        });
    }

    private void ModelsDirectoryReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Paths.ModelsDirectory → \"{_pathsDefaults.ModelsDirectory}\"");
        ModelsDirectoryBox.Text = _pathsDefaults.ModelsDirectory;
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedItem is not string model) return;
        TryApply($"Transcription.Model ← \"{model}\"", () =>
        {
            SettingsService.Instance.Current.Transcription.Model = model;
            SettingsService.Instance.Save();
            MarkRestartPending();
        });
    }

    private void ModelReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Transcription.Model → \"{_transcriptionDefaults.Model}\"");
        ModelCombo.SelectedItem = _transcriptionDefaults.Model;
    }

    private void UseGpuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TryApply($"Transcription.UseGpu ← {UseGpuToggle.IsOn}", () =>
        {
            SettingsService.Instance.Current.Transcription.UseGpu = UseGpuToggle.IsOn;
            SettingsService.Instance.Save();
            MarkRestartPending();
        });
    }

    private void UseGpuReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Transcription.UseGpu → {_transcriptionDefaults.UseGpu}");
        UseGpuToggle.IsOn = _transcriptionDefaults.UseGpu;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string text = LanguageCombo.Text ?? "";
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Content is string s)
            text = s;
        TryApply($"Transcription.Language ← \"{text}\"", () =>
        {
            SettingsService.Instance.Current.Transcription.Language = text;
            SettingsService.Instance.Save();
        });
    }

    private void LanguageReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Transcription.Language → \"{_transcriptionDefaults.Language}\"");
        LanguageCombo.Text = _transcriptionDefaults.Language;
    }

    private void InitialPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        TryApply($"Transcription.InitialPrompt ← ({InitialPromptBox.Text.Length} chars)", () =>
        {
            SettingsService.Instance.Current.Transcription.InitialPrompt = InitialPromptBox.Text;
            SettingsService.Instance.Save();
        });
    }

    private void InitialPromptReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log("[WHISPERPAGE] Reset Transcription.InitialPrompt");
        InitialPromptBox.Text = _transcriptionDefaults.InitialPrompt;
    }

    // ── VAD ─────────────────────────────────────────────────────────────────

    private void VadEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TryApply($"SpeechDetection.Enabled ← {VadEnabledToggle.IsOn}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.Enabled = VadEnabledToggle.IsOn;
            SettingsService.Instance.Save();
        });
    }

    private void VadEnabledReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.Enabled → {_speechDefaults.Enabled}");
        VadEnabledToggle.IsOn = _speechDefaults.Enabled;
    }

    private void VadThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadThresholdText(e.NewValue);
        if (_loading) return;
        TryApply($"SpeechDetection.Threshold ← {e.NewValue:0.00}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.Threshold = (float)e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateVadThresholdText(double v) => VadThresholdValue.Text = FmtTwo(v);

    private void VadThresholdReset_Click(object sender, RoutedEventArgs e)
    {
        VadThresholdSlider.Value = _speechDefaults.Threshold;
    }

    private void VadMinSpeechBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        TryApply($"SpeechDetection.MinSpeechDurationMs ← {(int)args.NewValue}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.MinSpeechDurationMs = (int)args.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void VadMinSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.MinSpeechDurationMs → {_speechDefaults.MinSpeechDurationMs}");
        VadMinSpeechBox.Value = _speechDefaults.MinSpeechDurationMs;
    }

    private void VadMinSilenceBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        TryApply($"SpeechDetection.MinSilenceDurationMs ← {(int)args.NewValue}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.MinSilenceDurationMs = (int)args.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void VadMinSilenceReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.MinSilenceDurationMs → {_speechDefaults.MinSilenceDurationMs}");
        VadMinSilenceBox.Value = _speechDefaults.MinSilenceDurationMs;
    }

    private void VadMaxSpeechSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadMaxSpeechText(e.NewValue);
        if (_loading) return;
        TryApply($"SpeechDetection.MaxSpeechDurationSec ← {(int)e.NewValue}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.MaxSpeechDurationSec = (float)e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateVadMaxSpeechText(double v) => VadMaxSpeechValue.Text = FmtSeconds(v);

    private void VadMaxSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.MaxSpeechDurationSec → {_speechDefaults.MaxSpeechDurationSec}");
        VadMaxSpeechSlider.Value = _speechDefaults.MaxSpeechDurationSec;
    }

    private void VadSpeechPadBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        TryApply($"SpeechDetection.SpeechPadMs ← {(int)args.NewValue}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.SpeechPadMs = (int)args.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void VadSpeechPadReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.SpeechPadMs → {_speechDefaults.SpeechPadMs}");
        VadSpeechPadBox.Value = _speechDefaults.SpeechPadMs;
    }

    private void VadOverlapSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVadOverlapText(e.NewValue);
        if (_loading) return;
        TryApply($"SpeechDetection.SamplesOverlap ← {e.NewValue:0.00}", () =>
        {
            SettingsService.Instance.Current.SpeechDetection.SamplesOverlap = (float)e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateVadOverlapText(double v) => VadOverlapValue.Text = FmtTwo(v);

    private void VadOverlapReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset SpeechDetection.SamplesOverlap → {_speechDefaults.SamplesOverlap}");
        VadOverlapSlider.Value = _speechDefaults.SamplesOverlap;
    }

    // ── Décodage ─────────────────────────────────────────────────────────────

    private void TemperatureSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateTemperatureText(e.NewValue);
        if (_loading) return;
        TryApply($"Decoding.Temperature ← {e.NewValue:0.0}", () =>
        {
            SettingsService.Instance.Current.Decoding.Temperature = e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateTemperatureText(double v) => TemperatureValue.Text = Fmt(v);

    private void TemperatureReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Decoding.Temperature → {_decodingDefaults.Temperature}");
        TemperatureSlider.Value = _decodingDefaults.Temperature;
    }

    private void TemperatureIncrementSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateTemperatureIncrementText(e.NewValue);
        UpdateTemperatureIncrementWarning(e.NewValue);
        if (_loading) return;
        TryApply($"Decoding.TemperatureIncrement ← {e.NewValue:0.0}", () =>
        {
            SettingsService.Instance.Current.Decoding.TemperatureIncrement = e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateTemperatureIncrementText(double v) => TemperatureIncrementValue.Text = Fmt(v);

    private void UpdateTemperatureIncrementWarning(double v) =>
        TemperatureIncrementWarning.IsOpen = v == 0.0;

    private void TemperatureIncrementReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Decoding.TemperatureIncrement → {_decodingDefaults.TemperatureIncrement}");
        TemperatureIncrementSlider.Value = _decodingDefaults.TemperatureIncrement;
    }

    // ── Seuils de confiance ─────────────────────────────────────────────────

    private void EntropySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateEntropyText(e.NewValue);
        if (_loading) return;
        TryApply($"Confidence.EntropyThreshold ← {e.NewValue:0.0}", () =>
        {
            SettingsService.Instance.Current.Confidence.EntropyThreshold = e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateEntropyText(double v) => EntropyValue.Text = Fmt(v);

    private void EntropyReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Confidence.EntropyThreshold → {_confidenceDefaults.EntropyThreshold}");
        EntropySlider.Value = _confidenceDefaults.EntropyThreshold;
    }

    private void LogprobSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateLogprobText(e.NewValue);
        if (_loading) return;
        TryApply($"Confidence.LogprobThreshold ← {e.NewValue:0.0}", () =>
        {
            SettingsService.Instance.Current.Confidence.LogprobThreshold = e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateLogprobText(double v) => LogprobValue.Text = Fmt(v);

    private void LogprobReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Confidence.LogprobThreshold → {_confidenceDefaults.LogprobThreshold}");
        LogprobSlider.Value = _confidenceDefaults.LogprobThreshold;
    }

    private void NoSpeechSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateNoSpeechText(e.NewValue);
        if (_loading) return;
        TryApply($"Confidence.NoSpeechThreshold ← {e.NewValue:0.00}", () =>
        {
            SettingsService.Instance.Current.Confidence.NoSpeechThreshold = e.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void UpdateNoSpeechText(double v) => NoSpeechValue.Text = FmtTwo(v);

    private void NoSpeechReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Confidence.NoSpeechThreshold → {_confidenceDefaults.NoSpeechThreshold}");
        NoSpeechSlider.Value = _confidenceDefaults.NoSpeechThreshold;
    }

    // ── Filtres de sortie ───────────────────────────────────────────────────

    private void SuppressNstToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TryApply($"OutputFilters.SuppressNonSpeechTokens ← {SuppressNstToggle.IsOn}", () =>
        {
            SettingsService.Instance.Current.OutputFilters.SuppressNonSpeechTokens = SuppressNstToggle.IsOn;
            SettingsService.Instance.Save();
        });
    }

    private void SuppressNstReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset OutputFilters.SuppressNonSpeechTokens → {_outputDefaults.SuppressNonSpeechTokens}");
        SuppressNstToggle.IsOn = _outputDefaults.SuppressNonSpeechTokens;
    }

    private void SuppressBlankToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TryApply($"OutputFilters.SuppressBlank ← {SuppressBlankToggle.IsOn}", () =>
        {
            SettingsService.Instance.Current.OutputFilters.SuppressBlank = SuppressBlankToggle.IsOn;
            SettingsService.Instance.Save();
        });
    }

    private void SuppressBlankReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset OutputFilters.SuppressBlank → {_outputDefaults.SuppressBlank}");
        SuppressBlankToggle.IsOn = _outputDefaults.SuppressBlank;
    }

    private void SuppressRegexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        TryApply($"OutputFilters.SuppressRegex ← \"{SuppressRegexBox.Text}\"", () =>
        {
            SettingsService.Instance.Current.OutputFilters.SuppressRegex = SuppressRegexBox.Text;
            SettingsService.Instance.Save();
        });
    }

    private void SuppressRegexReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log("[WHISPERPAGE] Reset OutputFilters.SuppressRegex");
        SuppressRegexBox.Text = _outputDefaults.SuppressRegex;
    }

    // ── Contexte et segmentation ────────────────────────────────────────────

    private void UseContextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TryApply($"Context.UseContext ← {UseContextToggle.IsOn}", () =>
        {
            SettingsService.Instance.Current.Context.UseContext = UseContextToggle.IsOn;
            SettingsService.Instance.Save();
        });
    }

    private void UseContextReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Context.UseContext → {_contextDefaults.UseContext}");
        UseContextToggle.IsOn = _contextDefaults.UseContext;
    }

    private void MaxTokensBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        TryApply($"Context.MaxTokens ← {(int)args.NewValue}", () =>
        {
            SettingsService.Instance.Current.Context.MaxTokens = (int)args.NewValue;
            SettingsService.Instance.Save();
        });
    }

    private void MaxTokensReset_Click(object sender, RoutedEventArgs e)
    {
        App.Log?.Log($"[WHISPERPAGE] Reset Context.MaxTokens → {_contextDefaults.MaxTokens}");
        MaxTokensBox.Value = _contextDefaults.MaxTokens;
    }
}
