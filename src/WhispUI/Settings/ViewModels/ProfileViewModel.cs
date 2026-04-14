using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Data;

namespace WhispUI.Settings.ViewModels;

// ViewModel for a single rewrite profile — used inside an ItemsRepeater
// DataTemplate in LlmProfilesSection. Auto-saves on every change, aligning
// with RuleViewModel and WhisperViewModel. No Save/Cancel surface.
//
// Temperature and CtxIndex are doubles (Slider.Value type).
// TopP and RepeatPenalty use NaN to represent "not set" (null in POCO).
public partial class ProfileViewModel : ObservableObject
{
    internal static readonly int[] CtxKSteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };
    private const double DefaultTemperature = 0.5;
    private const int DefaultNumCtxK = 2;

    private bool _isSyncing;

    // Index in the Profiles list for write-back.
    public int ProfileIndex { get; set; }

    // ── Editable properties ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial string SystemPrompt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(TempDisplay))]
    public partial double Temperature { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CtxDisplay))]
    public partial double CtxIndex { get; set; }

    [ObservableProperty]
    public partial double TopP { get; set; }

    [ObservableProperty]
    public partial double RepeatPenalty { get; set; }

    // ── State ────────────────────────────────────────────────────────────────

    // Drives IsExpanded on the SettingsExpander so a freshly added profile
    // opens automatically. Never reset — cheap signal, no behavioral impact
    // once true.
    [ObservableProperty]
    public partial bool IsNew { get; set; }

    // ── Computed display properties ──────────────────────────────────────────

    public string Summary
    {
        get
        {
            string model = string.IsNullOrWhiteSpace(Model) ? "(no model)" : Model;
            int idx = Math.Clamp((int)CtxIndex, 0, CtxKSteps.Length - 1);
            return $"{model}  ·  {CtxKSteps[idx]}K ctx  ·  temp {Temperature:F2}";
        }
    }

    public string TempDisplay => Temperature.ToString("F2");

    public string CtxDisplay
    {
        get
        {
            int idx = Math.Clamp((int)CtxIndex, 0, CtxKSteps.Length - 1);
            return $"{CtxKSteps[idx]}K";
        }
    }

    // ── OnChanged → auto-save ────────────────────────────────────────────────

    partial void OnNameChanged(string value) { if (!_isSyncing) PushToSettings(); }
    partial void OnModelChanged(string value) { if (!_isSyncing) PushToSettings(); }
    partial void OnSystemPromptChanged(string value) { if (!_isSyncing) PushToSettings(); }
    partial void OnTemperatureChanged(double value) { if (!_isSyncing) PushToSettings(); }
    partial void OnCtxIndexChanged(double value) { if (!_isSyncing) PushToSettings(); }
    partial void OnTopPChanged(double value) { if (!_isSyncing) PushToSettings(); }
    partial void OnRepeatPenaltyChanged(double value) { if (!_isSyncing) PushToSettings(); }

    // ── Constructor ──────────────────────────────────────────────────────────

    public ProfileViewModel()
    {
        _isSyncing = true;
        Name = "";
        Model = "";
        SystemPrompt = "";
        Temperature = DefaultTemperature;
        CtxIndex = Array.IndexOf(CtxKSteps, DefaultNumCtxK);
        TopP = double.NaN;
        RepeatPenalty = double.NaN;
        _isSyncing = false;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    public void LoadFrom(int index, RewriteProfile profile, bool isNew)
    {
        _isSyncing = true;
        ProfileIndex = index;
        IsNew = isNew;

        Name = profile.Name;
        Model = profile.Model;
        SystemPrompt = profile.SystemPrompt;
        Temperature = profile.Temperature ?? DefaultTemperature;

        int ctxIdx = Array.IndexOf(CtxKSteps, profile.NumCtxK ?? DefaultNumCtxK);
        CtxIndex = ctxIdx >= 0 ? ctxIdx : Array.IndexOf(CtxKSteps, DefaultNumCtxK);

        TopP = profile.TopP ?? double.NaN;
        RepeatPenalty = profile.RepeatPenalty ?? double.NaN;

        _isSyncing = false;
    }

    // ── Write-back ───────────────────────────────────────────────────────────

    // Trim of Name and Model applied at write time only — keeps the live
    // VM value stable during typing (trimming the bound property would
    // fight the cursor). The POCO stays clean.
    private void PushToSettings()
    {
        var profiles = SettingsService.Instance.Current.Llm.Profiles;
        if (ProfileIndex >= profiles.Count) return;

        var p = profiles[ProfileIndex];
        p.Name = Name.Trim();
        p.Model = Model.Trim();
        p.SystemPrompt = SystemPrompt;
        p.Temperature = Temperature;
        p.NumCtxK = CtxKSteps[Math.Clamp((int)CtxIndex, 0, CtxKSteps.Length - 1)];
        p.TopP = double.IsNaN(TopP) ? null : (double?)TopP;
        p.RepeatPenalty = double.IsNaN(RepeatPenalty) ? null : (double?)RepeatPenalty;
        SettingsService.Instance.Save();
    }
}

// ── Converters for slider tooltips ──────────────────────────────────────────
//
// Defined here (not as inner classes) so they can be instantiated in XAML
// as StaticResources inside the DataTemplate.

// Context slider value (0..8 index) → display string (1K..256K).
public sealed class CtxTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int idx = value is double d ? (int)d : 0;
        if (idx >= 0 && idx < ProfileViewModel.CtxKSteps.Length)
            return $"{ProfileViewModel.CtxKSteps[idx]}K";
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

// Temperature slider value → 2-decimal string (avoids "0.15000000001").
public sealed class TempTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double d ? d.ToString("F2") : (value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
