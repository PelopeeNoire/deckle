using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhispUI.Settings.ViewModels;

// Twin of RuleViewModel keyed on word count rather than duration. Kept as a
// separate class (no common base) so the XAML DataTemplate can bind directly
// to the concrete property names the UI shows (MinWordCount vs
// MinDurationSeconds) without converters.
public partial class RuleByWordsViewModel : ObservableObject
{
    private bool _isSyncing;

    public int RuleIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial double MinWordCount { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; }

    public string Description => $"Recordings longer than {(int)MinWordCount} words";

    partial void OnMinWordCountChanged(double value)
    {
        if (_isSyncing || double.IsNaN(value)) return;
        PushToSettings();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_isSyncing) return;
        PushToSettings();
    }

    public RuleByWordsViewModel()
    {
        _isSyncing = true;
        MinWordCount = 0;
        ProfileName = "";
        _isSyncing = false;
    }

    public void Load(int index, AutoRewriteRuleByWords rule)
    {
        _isSyncing = true;
        RuleIndex = index;
        MinWordCount = rule.MinWordCount;
        ProfileName = rule.ProfileName;
        _isSyncing = false;
    }

    private void PushToSettings()
    {
        var llm = SettingsService.Instance.Current.Llm;
        var rules = llm.AutoRewriteRulesByWords;
        if (RuleIndex < rules.Count)
        {
            if (!double.IsNaN(MinWordCount))
                rules[RuleIndex].MinWordCount = (int)MinWordCount;
            rules[RuleIndex].ProfileName = ProfileName;
            rules[RuleIndex].ProfileId = llm.Profiles.Find(p =>
                string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
            SettingsService.Instance.Save();
        }
    }
}
