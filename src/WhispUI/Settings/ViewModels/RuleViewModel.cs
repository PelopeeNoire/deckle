using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhispUI.Settings.ViewModels;

// ViewModel for a single auto-rewrite rule — used inside an ItemsRepeater
// DataTemplate in LlmRulesSection. Auto-saves on every change (no
// Save/Cancel — the two fields are simple enough for immediate persistence).
//
// ProfileChoices is the per-item snapshot of the available profile names,
// bound directly by the ComboBox in the DataTemplate. Carrying it on the
// VM (instead of binding the ComboBox to a parent collection via
// {x:Bind}/Tag) sidesteps the ItemsRepeater virtualization race that was
// causing a cyclic +1 desync between Header and SelectedItem.
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

    public ObservableCollection<string> ProfileChoices { get; } = new();

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

        ProfileChoices.Clear();
        foreach (var p in SettingsService.Instance.Current.Llm.Profiles)
            ProfileChoices.Add(p.Name);

        ProfileName = rule.ProfileName;
        _isSyncing = false;
    }

    private void PushToSettings()
    {
        var llm = SettingsService.Instance.Current.Llm;
        var rules = llm.AutoRewriteRules;
        if (RuleIndex < rules.Count)
        {
            if (!double.IsNaN(MinDurationSeconds))
                rules[RuleIndex].MinDurationSeconds = (int)MinDurationSeconds;
            rules[RuleIndex].ProfileName = ProfileName;
            rules[RuleIndex].ProfileId = llm.Profiles.Find(p =>
                string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
            SettingsService.Instance.Save();
        }
    }
}
