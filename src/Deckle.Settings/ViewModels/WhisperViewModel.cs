using CommunityToolkit.Mvvm.ComponentModel;
using Deckle.Logging;
using Deckle.Whisp;

namespace Deckle.Settings.ViewModels;

// ViewModel for WhisperPage — bridges the 7 AppSettings sections
// (Paths, Transcription, SpeechDetection, Decoding, Confidence,
// OutputFilters, Context) to the XAML via x:Bind.
//
// Pattern: Load() pulls from the POCO, property changes push back via
// PushToSettings(). The _isSyncing flag prevents re-saving during Load().
//
// Model and Language are set from code-behind (combo handlers), not
// bound in XAML — same pattern as GeneralViewModel.AudioInputDeviceId.
//
// NumberBox-bound properties are double (NumberBox.Value type). NaN guard
// in OnXChanged and PushToSettings prevents saving when the user clears
// a field.
//
// Partial properties (not fields) for WinRT/AOT compatibility (MVVMTK0045).
public partial class WhisperViewModel : ObservableObject
{
    private static readonly LogService _log = LogService.Instance;
    private bool _isSyncing;

    // ── Paths ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string ModelsDirectory { get; set; }

    partial void OnModelsDirectoryChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Paths.ModelsDirectory ← \"{value}\"");
        PushToSettings();
    }

    // ── Transcription ────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial bool UseGpu { get; set; }

    [ObservableProperty]
    public partial string Language { get; set; }

    [ObservableProperty]
    public partial string InitialPrompt { get; set; }

    partial void OnModelChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Transcription.Model ← \"{value}\"");
        PushToSettings();
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Transcription.UseGpu ← {value}");
        PushToSettings();
    }

    partial void OnLanguageChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Transcription.Language ← \"{value}\"");
        PushToSettings();
    }

    partial void OnInitialPromptChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Transcription.InitialPrompt ← ({value?.Length ?? 0} chars)");
        PushToSettings();
    }

    // ── Speech Detection ─────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool VadEnabled { get; set; }

    [ObservableProperty]
    public partial double VadThreshold { get; set; }

    [ObservableProperty]
    public partial double VadMinSpeechDurationMs { get; set; }

    [ObservableProperty]
    public partial double VadMinSilenceDurationMs { get; set; }

    [ObservableProperty]
    public partial double VadMaxSpeechDurationSec { get; set; }

    [ObservableProperty]
    public partial double VadSpeechPadMs { get; set; }

    [ObservableProperty]
    public partial double VadSamplesOverlap { get; set; }

    partial void OnVadEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.Enabled ← {value}");
        PushToSettings();
    }

    partial void OnVadThresholdChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.Threshold ← {value:0.00}");
        PushToSettings();
    }

    partial void OnVadMinSpeechDurationMsChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.MinSpeechDurationMs ← {(int)value}");
        PushToSettings();
    }

    partial void OnVadMinSilenceDurationMsChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.MinSilenceDurationMs ← {(int)value}");
        PushToSettings();
    }

    partial void OnVadMaxSpeechDurationSecChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.MaxSpeechDurationSec ← {(int)value}");
        PushToSettings();
    }

    partial void OnVadSpeechPadMsChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.SpeechPadMs ← {(int)value}");
        PushToSettings();
    }

    partial void OnVadSamplesOverlapChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"SpeechDetection.SamplesOverlap ← {value:0.00}");
        PushToSettings();
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial double Temperature { get; set; }

    [ObservableProperty]
    public partial double TemperatureIncrement { get; set; }

    partial void OnTemperatureChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Decoding.Temperature ← {value:0.0}");
        PushToSettings();
    }

    partial void OnTemperatureIncrementChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Decoding.TemperatureIncrement ← {value:0.0}");
        PushToSettings();
    }

    // ── Confidence ───────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial double EntropyThreshold { get; set; }

    [ObservableProperty]
    public partial double LogprobThreshold { get; set; }

    [ObservableProperty]
    public partial double NoSpeechThreshold { get; set; }

    partial void OnEntropyThresholdChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Confidence.EntropyThreshold ← {value:0.0}");
        PushToSettings();
    }

    partial void OnLogprobThresholdChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Confidence.LogprobThreshold ← {value:0.00}");
        PushToSettings();
    }

    partial void OnNoSpeechThresholdChanged(double value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Confidence.NoSpeechThreshold ← {value:0.00}");
        PushToSettings();
    }

    // ── Output Filters ───────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool SuppressNonSpeechTokens { get; set; }

    [ObservableProperty]
    public partial bool SuppressBlank { get; set; }

    [ObservableProperty]
    public partial string SuppressRegex { get; set; }

    partial void OnSuppressNonSpeechTokensChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"OutputFilters.SuppressNonSpeechTokens ← {value}");
        PushToSettings();
    }

    partial void OnSuppressBlankChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"OutputFilters.SuppressBlank ← {value}");
        PushToSettings();
    }

    partial void OnSuppressRegexChanged(string value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"OutputFilters.SuppressRegex ← \"{value}\"");
        PushToSettings();
    }

    // ── Context ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool UseContext { get; set; }

    [ObservableProperty]
    public partial double MaxTokens { get; set; }

    partial void OnUseContextChanged(bool value)
    {
        if (_isSyncing) return;
        _log.Info(LogSource.SetWhisper, $"Context.UseContext ← {value}");
        PushToSettings();
    }

    partial void OnMaxTokensChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        _log.Info(LogSource.SetWhisper, $"Context.MaxTokens ← {(int)value}");
        PushToSettings();
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public WhisperViewModel()
    {
        _isSyncing = true;

        ModelsDirectory = "";
        Model = "ggml-large-v3.bin";
        UseGpu = true;
        Language = "fr";
        InitialPrompt = "Bonjour. Voici une transcription en français, avec une ponctuation soignée et des phrases complètes.";
        VadEnabled = true;
        VadThreshold = 0.5;
        VadMinSpeechDurationMs = 250;
        VadMinSilenceDurationMs = 500;
        VadMaxSpeechDurationSec = 30.0;
        VadSpeechPadMs = 200;
        VadSamplesOverlap = 0.1;
        Temperature = 0.0;
        TemperatureIncrement = 0.2;
        EntropyThreshold = 2.4;
        LogprobThreshold = -1.0;
        NoSpeechThreshold = 0.6;
        SuppressNonSpeechTokens = true;
        SuppressBlank = true;
        SuppressRegex = "";
        UseContext = true;
        MaxTokens = -1;

        // _isSyncing stays true — Load() will set it to false.
    }

    // ── Sync with SettingsService ────────────────────────────────────────────

    // After slice C2b, all Whisp settings live in modules/whisp/settings.json
    // and ModelsDirectory moved off Paths into WhispSettings — so a single
    // module read covers everything this VM exposes.
    public void Load()
    {
        _isSyncing = true;
        try
        {
            var s = WhispSettingsService.Instance.Current;
            ModelsDirectory = s.ModelsDirectory;
            Model = s.Transcription.Model;
            UseGpu = s.Transcription.UseGpu;
            Language = s.Transcription.Language;
            InitialPrompt = s.Transcription.InitialPrompt;
            VadEnabled = s.SpeechDetection.Enabled;
            VadThreshold = s.SpeechDetection.Threshold;
            VadMinSpeechDurationMs = s.SpeechDetection.MinSpeechDurationMs;
            VadMinSilenceDurationMs = s.SpeechDetection.MinSilenceDurationMs;
            VadMaxSpeechDurationSec = s.SpeechDetection.MaxSpeechDurationSec;
            VadSpeechPadMs = s.SpeechDetection.SpeechPadMs;
            VadSamplesOverlap = s.SpeechDetection.SamplesOverlap;
            Temperature = s.Decoding.Temperature;
            TemperatureIncrement = s.Decoding.TemperatureIncrement;
            EntropyThreshold = s.Confidence.EntropyThreshold;
            LogprobThreshold = s.Confidence.LogprobThreshold;
            NoSpeechThreshold = s.Confidence.NoSpeechThreshold;
            SuppressNonSpeechTokens = s.OutputFilters.SuppressNonSpeechTokens;
            SuppressBlank = s.OutputFilters.SuppressBlank;
            SuppressRegex = s.OutputFilters.SuppressRegex;
            UseContext = s.Context.UseContext;
            MaxTokens = s.Context.MaxTokens;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void PushToSettings()
    {
        var s = WhispSettingsService.Instance.Current;

        s.ModelsDirectory = ModelsDirectory;
        s.Transcription.Model = Model;
        s.Transcription.UseGpu = UseGpu;
        s.Transcription.Language = Language;
        s.Transcription.InitialPrompt = InitialPrompt;

        s.SpeechDetection.Enabled = VadEnabled;
        s.SpeechDetection.Threshold = (float)VadThreshold;
        if (!double.IsNaN(VadMinSpeechDurationMs))
            s.SpeechDetection.MinSpeechDurationMs = (int)VadMinSpeechDurationMs;
        if (!double.IsNaN(VadMinSilenceDurationMs))
            s.SpeechDetection.MinSilenceDurationMs = (int)VadMinSilenceDurationMs;
        s.SpeechDetection.MaxSpeechDurationSec = (float)VadMaxSpeechDurationSec;
        if (!double.IsNaN(VadSpeechPadMs))
            s.SpeechDetection.SpeechPadMs = (int)VadSpeechPadMs;
        s.SpeechDetection.SamplesOverlap = (float)VadSamplesOverlap;

        s.Decoding.Temperature = Temperature;
        s.Decoding.TemperatureIncrement = TemperatureIncrement;

        s.Confidence.EntropyThreshold = EntropyThreshold;
        s.Confidence.LogprobThreshold = LogprobThreshold;
        s.Confidence.NoSpeechThreshold = NoSpeechThreshold;

        s.OutputFilters.SuppressNonSpeechTokens = SuppressNonSpeechTokens;
        s.OutputFilters.SuppressBlank = SuppressBlank;
        s.OutputFilters.SuppressRegex = SuppressRegex;

        s.Context.UseContext = UseContext;
        if (!double.IsNaN(MaxTokens))
            s.Context.MaxTokens = (int)MaxTokens;

        WhispSettingsService.Instance.Save();
    }
}
