using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhispUI.Settings.ViewModels;

// ViewModel for a single auto-rewrite rule — used inside an ItemsRepeater
// DataTemplate in LlmRulesSection. Auto-saves on every change (no
// Save/Cancel — the two fields are simple enough for immediate persistence).
public partial class RuleViewModel : ObservableObject
{
    private bool _isSyncing;

    // Index in the AutoRewriteRules list for write-back.
    public int RuleIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial double MinDurationSeconds { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; }

    public string Description => $"Recordings longer than {(int)MinDurationSeconds}s";

    partial void OnMinDurationSecondsChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        PushToSettings();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_isSyncing) return;
        PushToSettings();
    }

    public RuleViewModel()
    {
        _isSyncing = true;
        MinDurationSeconds = 0;
        ProfileName = "";
        _isSyncing = false;
    }

    public void Load(int index, AutoRewriteRule rule)
    {
        _isSyncing = true;
        RuleIndex = index;
        MinDurationSeconds = rule.MinDurationSeconds;
        ProfileName = rule.ProfileName;
        _isSyncing = false;
    }

    private void PushToSettings()
    {
        var rules = SettingsService.Instance.Current.Llm.AutoRewriteRules;
        if (RuleIndex < rules.Count)
        {
            if (!double.IsNaN(MinDurationSeconds))
                rules[RuleIndex].MinDurationSeconds = (int)MinDurationSeconds;
            rules[RuleIndex].ProfileName = ProfileName;
            SettingsService.Instance.Save();
        }
    }
}
