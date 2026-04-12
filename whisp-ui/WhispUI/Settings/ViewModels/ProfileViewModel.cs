using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WhispUI.Settings.ViewModels;

// ViewModel for a single rewrite profile — used inside an ItemsRepeater
// DataTemplate in LlmProfilesSection. Handles editing state, dirty
// tracking, and Save/Cancel operations against the POCO.
//
// Temperature and CtxIndex are doubles (Slider.Value type).
// TopP and RepeatPenalty use NaN to represent "not set" (null in POCO).
public partial class ProfileViewModel : ObservableObject
{
    internal static readonly int[] CtxKSteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };
    private const double DefaultTemperature = 0.8;
    private const int DefaultNumCtxK = 2;

    private bool _isSyncing;

    // Saved state for dirty check.
    private string _savedName = "";
    private string _savedModel = "";
    private string _savedPrompt = "";
    private double _savedTemperature;
    private double _savedCtxIndex;
    private double? _savedTopP;
    private double? _savedRepeatPenalty;

    // Index in the Profiles list for Save operations.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    public partial bool IsDirty { get; set; }

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

    public Visibility ActionsVisibility =>
        IsDirty || IsNew ? Visibility.Visible : Visibility.Collapsed;

    // ── OnChanged → dirty check ──────────────────────────────────────────────

    partial void OnNameChanged(string value) => UpdateDirty();
    partial void OnModelChanged(string value) => UpdateDirty();
    partial void OnSystemPromptChanged(string value) => UpdateDirty();
    partial void OnTemperatureChanged(double value) => UpdateDirty();
    partial void OnCtxIndexChanged(double value) => UpdateDirty();
    partial void OnTopPChanged(double value) => UpdateDirty();
    partial void OnRepeatPenaltyChanged(double value) => UpdateDirty();

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

    // ── Load / Save / Cancel ─────────────────────────────────────────────────

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

        SnapshotSaved();
        _isSyncing = false;
        IsDirty = false;
    }

    public void Save()
    {
        var profiles = SettingsService.Instance.Current.Llm.Profiles;
        if (ProfileIndex < profiles.Count)
        {
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

        _isSyncing = true;
        Name = Name.Trim();
        Model = Model.Trim();
        _isSyncing = false;
        SnapshotSaved();
        IsNew = false;
        IsDirty = false;
    }

    public void Cancel()
    {
        _isSyncing = true;
        Name = _savedName;
        Model = _savedModel;
        SystemPrompt = _savedPrompt;
        Temperature = _savedTemperature;
        CtxIndex = _savedCtxIndex;
        TopP = _savedTopP ?? double.NaN;
        RepeatPenalty = _savedRepeatPenalty ?? double.NaN;
        _isSyncing = false;
        IsNew = false;
        IsDirty = false;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void SnapshotSaved()
    {
        _savedName = Name;
        _savedModel = Model;
        _savedPrompt = SystemPrompt;
        _savedTemperature = Temperature;
        _savedCtxIndex = CtxIndex;
        _savedTopP = double.IsNaN(TopP) ? null : (double?)TopP;
        _savedRepeatPenalty = double.IsNaN(RepeatPenalty) ? null : (double?)RepeatPenalty;
    }

    private void UpdateDirty()
    {
        if (_isSyncing) return;
        double? topP = double.IsNaN(TopP) ? null : (double?)TopP;
        double? repeat = double.IsNaN(RepeatPenalty) ? null : (double?)RepeatPenalty;
        IsDirty = Name != _savedName
                || Model != _savedModel
                || SystemPrompt != _savedPrompt
                || Math.Abs(Temperature - _savedTemperature) > 1e-6
                || Math.Abs(CtxIndex - _savedCtxIndex) > 0.5
                || topP != _savedTopP
                || repeat != _savedRepeatPenalty;
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
