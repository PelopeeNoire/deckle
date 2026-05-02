using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Deckle.Localization;
using Deckle.Logging;
using Deckle.Settings;
using Deckle.Whisp.ViewModels;

namespace Deckle.Whisp;

public sealed partial class WhisperPage : Page
{
    private static readonly LogService _log = LogService.Instance;

    public WhisperViewModel ViewModel { get; } = new();

    // Guards the Language combo's SelectionChanged and the model
    // AutoSuggestBox's SuggestionChosen during initial sync — these handlers
    // set VM properties which would trigger PushToSettings() needlessly.
    private bool _initializing;

    // Full list of installed Whisper models on disk, scanned once per
    // page entry. Feeds the model AutoSuggestBox as a filterable source.
    private List<string> _modelNames = new();

    // Values at page load that require a restart. The footer only appears
    // when the current value differs from the snapshot.
    private string _startupModel = "";
    private bool _startupUseGpu;

    // Defaults resolved from POCOs — single source of truth for Reset.
    // ModelsDirectory's default lives on WhispSettings since slice C2b
    // (it migrated off PathsSettings into the Whisp module's POCO).
    private static readonly WhispSettings _whispDefaults = new();
    private static readonly TranscriptionSettings _transcriptionDefaults = new();
    private static readonly SpeechDetectionSettings _speechDefaults = new();
    private static readonly DecodingSettings _decodingDefaults = new();
    private static readonly ConfidenceSettings _confidenceDefaults = new();
    private static readonly OutputFilterSettings _outputDefaults = new();
    private static readonly ContextSettings _contextDefaults = new();

    public WhisperPage()
    {
        _log.Verbose(LogSource.SetWhisper, "ctor start");
        try
        {
            InitializeComponent();
            _log.Verbose(LogSource.SetWhisper, "init component complete");

            // WinUI 3 release bug: cannot set Minimum > defaultValue in XAML
            // without a parser crash under trimming. We set Minimum (and
            // Maximum for LogprobSlider) in code-behind. The x:Bind TwoWay
            // binding has already set Value from the VM constructor defaults
            // during InitializeComponent — those defaults are chosen to be
            // valid with Minimum=0 and default Maximum, so no clamping issue
            // except for LogprobSlider (VM default -1.0 gets clamped to 0,
            // then to -0.4 when Maximum is set; Load() corrects it).
            VadMaxSpeechSlider.Minimum = 5;
            EntropySlider.Minimum = 1.5;
            LogprobSlider.Minimum = -1.5;
            LogprobSlider.Maximum = -0.4;
            NoSpeechSlider.Minimum = 0.05;

            _log.Verbose(LogSource.SetWhisper, "bugged slider min/max set in code-behind");
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.SetWhisper, $"init component threw | error={ex.GetType().Name}: {ex.Message}");
            _log.Error(LogSource.SetWhisper, ex.StackTrace ?? "(no stack)");
            throw;
        }

        // Make the Page focusable so clicking on the background can
        // steal focus from the active control (triggering LostFocus →
        // binding update → auto-save).
        IsTabStop = true;

        NavigationCacheMode = NavigationCacheMode.Required;

        Loaded += (_, _) =>
        {
            _log.Verbose(LogSource.SetWhisper, "loaded fired");
            try
            {
                // Hover reveal for reset buttons — one-time setup.
                WireHover(ModelCard, ModelReset);
                WireHover(UseGpuCard, UseGpuReset);
                WireHover(LanguageCard, LanguageReset);
                InitialPromptCard.PointerEntered += (_, _) => InitialPromptReset.Opacity = 1;
                InitialPromptCard.PointerExited += (_, _) => InitialPromptReset.Opacity = 0;
                PathsCard.PointerEntered += (_, _) => ModelsDirectoryReset.Opacity = 1;
                PathsCard.PointerExited += (_, _) => ModelsDirectoryReset.Opacity = 0;
                VadEnabledCard.PointerEntered += (_, _) => VadEnabledReset.Opacity = 1;
                VadEnabledCard.PointerExited += (_, _) => VadEnabledReset.Opacity = 0;
                WireHover(VadThresholdCard, VadThresholdReset);
                WireHover(VadMinSpeechCard, VadMinSpeechReset);
                WireHover(VadMinSilenceCard, VadMinSilenceReset);
                WireHover(VadMaxSpeechCard, VadMaxSpeechReset);
                WireHover(VadSpeechPadCard, VadSpeechPadReset);
                WireHover(VadOverlapCard, VadOverlapReset);
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

                // React to VM property changes for side effects (restart
                // state, model folder re-scan).
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;

                _log.Success(LogSource.SetWhisper, "Whisper page ready");
                _log.Verbose(LogSource.SetWhisper, "loaded complete | state=page-ready");
            }
            catch (Exception ex)
            {
                _log.Error(LogSource.SetWhisper, $"loaded threw | error={ex.GetType().Name}: {ex.Message}");
                _log.Error(LogSource.SetWhisper, ex.StackTrace ?? "(no stack)");
            }
        };
    }

    // NavigationCacheMode.Required reuses the page instance. Loaded + hover
    // wiring only fire once (first navigation). On subsequent navigations we
    // reload settings from the POCO via the VM.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initializing = true;
        ViewModel.Load();
        RefreshModelNames();
        SyncLanguageCombo();
        _startupModel = ViewModel.Model;
        _startupUseGpu = ViewModel.UseGpu;
        _initializing = false;
    }

    // ── VM PropertyChanged side effects ─────────────────────────────────────
    //
    // During _initializing (Load + combo population), these are skipped.
    // After that, user interaction triggers:
    //   Model / UseGpu → restart footer
    //   ModelsDirectory → re-scan model combo

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_initializing) return;
        switch (e.PropertyName)
        {
            case nameof(WhisperViewModel.Model):
            case nameof(WhisperViewModel.UseGpu):
                UpdateRestartState();
                break;
            case nameof(WhisperViewModel.ModelsDirectory):
                _initializing = true;
                try { RefreshModelNames(); } finally { _initializing = false; }
                break;
        }
    }

    // ── Slider display text ─────────────────────────────────────────────────
    //
    // ValueChanged fires both from user interaction and from binding updates
    // (during Load). The handlers only update the display TextBlock — all
    // persistence flows through the VM via x:Bind TwoWay.

    private static string Fmt(double v) =>
        v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FmtTwo(double v) =>
        v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FmtSeconds(double v) =>
        ((int)v).ToString(CultureInfo.InvariantCulture) + "s";

    private void VadThresholdSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        VadThresholdValue.Text = FmtTwo(e.NewValue);

    private void VadMaxSpeechSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        VadMaxSpeechValue.Text = FmtSeconds(e.NewValue);

    private void VadOverlapSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        VadOverlapValue.Text = FmtTwo(e.NewValue);

    private void TemperatureSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        TemperatureValue.Text = Fmt(e.NewValue);

    private void TemperatureIncrementSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TemperatureIncrementValue.Text = Fmt(e.NewValue);
        TemperatureIncrementWarning.IsOpen = e.NewValue == 0.0;
    }

    private void EntropySlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        EntropyValue.Text = Fmt(e.NewValue);

    private void LogprobSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        LogprobValue.Text = FmtTwo(e.NewValue);

    private void NoSpeechSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        NoSpeechValue.Text = FmtTwo(e.NewValue);

    // ── Combo handlers (Model, Language) ────────────────────────────────────
    //
    // Model is an AutoSuggestBox (no dropdown chevron) populated dynamically
    // from disk: filtering by substring while typing, full list on focus
    // when empty. Language is an editable ComboBox with ComboBoxItem children.

    private static void WireHover(SettingsCard card, Button resetButton)
    {
        card.PointerEntered += (_, _) => resetButton.Opacity = 1;
        card.PointerExited += (_, _) => resetButton.Opacity = 0;
    }

    private void RefreshModelNames()
    {
        var items = new List<string>();
        try
        {
            string dir = WhispSettingsService.Instance.ResolveModelsDirectory();
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
            _log.Warning(LogSource.SetWhisper, $"model scan failed: {ex.Message}");
        }

        string current = ViewModel.Model;
        if (!string.IsNullOrEmpty(current) && !items.Contains(current))
            items.Insert(0, current);

        _modelNames = items;
        ModelSuggest.Text = current ?? "";
        ModelSuggest.ItemsSource = _modelNames;
    }

    private void SyncLanguageCombo()
    {
        string lang = ViewModel.Language;
        for (int i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item
                && item.Content is string s && s == lang)
            {
                LanguageCombo.SelectedIndex = i;
                return;
            }
        }
        // Custom language code not in the predefined list.
        LanguageCombo.SelectedIndex = -1;
        LanguageCombo.Text = lang;
    }

    private void ModelSuggest_GotFocus(object sender, RoutedEventArgs e)
    {
        // When the field gains focus, show the full list so the user can
        // browse all available models without typing. Matches the ComboBox
        // affordance the control replaces.
        ModelSuggest.ItemsSource = _modelNames;
        ModelSuggest.IsSuggestionListOpen = true;
    }

    private void ModelSuggest_TextChanged(AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        string query = sender.Text ?? "";
        if (string.IsNullOrEmpty(query))
        {
            sender.ItemsSource = _modelNames;
            return;
        }

        sender.ItemsSource = _modelNames
            .Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void ModelSuggest_SuggestionChosen(AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (_initializing || args.SelectedItem is not string model) return;
        ViewModel.Model = model;
    }

    private void ModelSuggest_LostFocus(object sender, RoutedEventArgs e)
    {
        // Free-form text is not a valid model — revert to the current VM
        // value so the field always shows an installed model name.
        if (!_modelNames.Contains(ModelSuggest.Text ?? ""))
            ModelSuggest.Text = ViewModel.Model ?? "";
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string text = LanguageCombo.Text ?? "";
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Content is string s)
            text = s;
        ViewModel.Language = text;
    }

    private void LanguageCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        string text = (LanguageCombo.Text ?? "").Trim();
        if (text != ViewModel.Language)
            ViewModel.Language = text;
    }

    // ── Click-to-unfocus ────────────────────────────────────────────────────
    //
    // Tapped on the background Grid steals focus from the active control.
    // This triggers LostFocus on TextBox / NumberBox / editable ComboBox,
    // which fires their binding update → auto-save. Interactive controls
    // (Button, Slider, etc.) handle Tapped internally, so this handler
    // only fires for non-interactive areas (descriptions, spacing, margins).

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't steal focus from a ComboBox — its Tapped bubbles up here
        // and re-focusing the page would close the dropdown before it opens.
        if (e.OriginalSource is DependencyObject obj && IsInsideComboBox(obj))
            return;
        this.Focus(FocusState.Programmatic);
    }

    private static bool IsInsideComboBox(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is ComboBox) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ── Reset handlers ──────────────────────────────────────────────────────
    //
    // Set the VM property (or combo for Model/Language) → OnXChanged fires →
    // PushToSettings. For combos, SelectionChanged fires → handler sets VM.

    private void ModelsDirectoryReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ModelsDirectory = _whispDefaults.ModelsDirectory;

    private void ModelReset_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Model = _transcriptionDefaults.Model;
        ModelSuggest.Text = _transcriptionDefaults.Model;
    }

    private void UseGpuReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.UseGpu = _transcriptionDefaults.UseGpu;

    private void LanguageReset_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        LanguageCombo.Text = _transcriptionDefaults.Language;
        _initializing = false;
        ViewModel.Language = _transcriptionDefaults.Language;
    }

    private void InitialPromptReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.InitialPrompt = _transcriptionDefaults.InitialPrompt;

    private void VadEnabledReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadEnabled = _speechDefaults.Enabled;

    private void VadThresholdReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadThreshold = _speechDefaults.Threshold;

    private void VadMinSpeechReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadMinSpeechDurationMs = _speechDefaults.MinSpeechDurationMs;

    private void VadMinSilenceReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadMinSilenceDurationMs = _speechDefaults.MinSilenceDurationMs;

    private void VadMaxSpeechReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadMaxSpeechDurationSec = _speechDefaults.MaxSpeechDurationSec;

    private void VadSpeechPadReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadSpeechPadMs = _speechDefaults.SpeechPadMs;

    private void VadOverlapReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.VadSamplesOverlap = _speechDefaults.SamplesOverlap;

    private void TemperatureReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Temperature = _decodingDefaults.Temperature;

    private void TemperatureIncrementReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.TemperatureIncrement = _decodingDefaults.TemperatureIncrement;

    private void EntropyReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.EntropyThreshold = _confidenceDefaults.EntropyThreshold;

    private void LogprobReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.LogprobThreshold = _confidenceDefaults.LogprobThreshold;

    private void NoSpeechReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NoSpeechThreshold = _confidenceDefaults.NoSpeechThreshold;

    private void SuppressNstReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SuppressNonSpeechTokens = _outputDefaults.SuppressNonSpeechTokens;

    private void SuppressBlankReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SuppressBlank = _outputDefaults.SuppressBlank;

    private void SuppressRegexReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SuppressRegex = _outputDefaults.SuppressRegex;

    private void UseContextReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.UseContext = _contextDefaults.UseContext;

    private void MaxTokensReset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MaxTokens = _contextDefaults.MaxTokens;

    // ── Restart state — highlight + footer ─────────────────────────────────
    //
    // Model and GPU require a restart. When their current value differs from
    // the startup snapshot, the footer appears (pattern Windows Terminal).

    private void UpdateRestartState()
    {
        bool dirty = ViewModel.Model != _startupModel
                  || ViewModel.UseGpu != _startupUseGpu;
        RestartFooter.Visibility = dirty
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartNow_Click(object sender, RoutedEventArgs e)
    {
        SettingsHost.RestartApp?.Invoke("Deckle.Whisp.WhisperPage, Deckle.Whisp");
    }

    private void RestartDiscard_Click(object sender, RoutedEventArgs e)
    {
        _log.Info(LogSource.SetWhisper, "Discard restart-requiring changes");

        _initializing = true;
        try
        {
            // Revert the VM properties to the startup snapshot.
            ViewModel.Model = _startupModel;
            ViewModel.UseGpu = _startupUseGpu;

            // Sync the AutoSuggestBox (not bound).
            ModelSuggest.Text = _startupModel;
        }
        finally
        {
            _initializing = false;
        }

        // The VM's OnChanged handlers are NOT suppressed by _initializing
        // (they check _isSyncing, not _initializing). But since we set the
        // VM properties directly, PushToSettings fires and saves. The
        // _initializing guard only prevents the combo handler from double-
        // writing. Model and UseGpu are now reverted — update footer.
        UpdateRestartState();
    }

    // ── Reset all ──────────────────────────────────────────────────────────

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Settings_ResetWhisperDialog_Title"),
            Content = Loc.Get("Settings_ResetWhisperDialog_Content"),
            PrimaryButtonText = Loc.Get("WhisperPageResetAllLabel.Text"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _log.Info(LogSource.SetWhisper, "Reset all Whisper settings to defaults");

        // After slice C2b, all Whisper settings (including ModelsDirectory)
        // live in a single WhispSettings POCO at modules/whisp/settings.json.
        // PathsSettings used to be reset alongside, but BackupDirectory is
        // unrelated to Whisper — leaving it untouched fixes a long-standing
        // pre-existing bug where "Reset Whisper" wiped the user's backup
        // directory override.
        WhispSettingsService.Instance.Replace(new WhispSettings());

        // Reload everything from the fresh POCO defaults.
        _initializing = true;
        ViewModel.Load();
        RefreshModelNames();
        SyncLanguageCombo();
        _initializing = false;
        UpdateRestartState();
    }
}
