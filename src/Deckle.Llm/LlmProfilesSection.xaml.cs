using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Localization;

namespace Deckle.Llm;

// ─── Section Profiles de LlmPage ───────────────────────────────────────────
//
// Liste des profils de réécriture. Tout le contenu est déclaratif (XAML
// DataTemplate + ProfileViewModel). Le code-behind ne gère que :
//  - Reload() → repopule l'ObservableCollection depuis le POCO
//  - Click handlers (Delete/Add) via Tag={x:Bind}
//  - Model AutoSuggestBox handlers → push vers le VM + filtre la liste
//  - ProfilesChanged event (fired on Name changes via PropertyChanged) pour
//    notifier Rules et ManualShortcut de rafraîchir leurs listes

public sealed partial class LlmProfilesSection : UserControl
{
    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();

    // Model names available from Ollama — source for the AutoSuggestBox
    // in each profile template. ObservableCollection so updates from
    // LlmPage.RefreshOllamaStateAsync are reflected on the next keystroke
    // or re-focus (ItemsSource is rebuilt from this list in FilterModels).
    public ObservableCollection<string> AvailableModelNames { get; } = new();

    internal void SetAvailableModelNames(IEnumerable<string> names)
    {
        AvailableModelNames.Clear();
        foreach (var n in names) AvailableModelNames.Add(n);
    }

    public event EventHandler? ProfilesChanged;

    public LlmProfilesSection()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        Profiles.Clear();
        var s = LlmSettingsService.Instance.Current;
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            var vm = new ProfileViewModel();
            vm.LoadFrom(i, s.Profiles[i], isNew: false);
            TrackProfile(vm);
            Profiles.Add(vm);
        }
    }

    // Fire ProfilesChanged when a profile's Name changes so Rules and
    // ManualShortcut refresh their dropdowns live. Auto-save on every
    // property change would be overkill — only Name is surfaced elsewhere.
    // No unsubscribe: VMs live as long as this section, and Reload() starts
    // from an empty collection.
    private void TrackProfile(ProfileViewModel vm)
    {
        vm.PropertyChanged += OnProfilePropertyChanged;
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileViewModel.Name))
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileViewModel vm)
            return;

        // Profiles and rules are independent: removing a profile leaves any
        // rule that referenced it intact, with a now-orphan ProfileName the
        // user can re-point or delete manually. Same for shortcut slots —
        // they keep their stale name until the user reassigns. This trade
        // protects rule thresholds (which are real work to redefine) at the
        // cost of a temporarily blank ComboBox SelectedItem until the user
        // notices and picks a replacement.
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Settings_RemoveProfileDialog_Title"),
            Content = Loc.Format("Settings_RemoveProfileDialog_Content_Format", vm.Name),
            PrimaryButtonText = Loc.Get("Common_Remove"),
            CloseButtonText = Loc.Get("Settings_RemoveProfileDialog_CloseButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var profiles = LlmSettingsService.Instance.Current.Profiles;
            if (vm.ProfileIndex < profiles.Count)
            {
                profiles.RemoveAt(vm.ProfileIndex);
                // Surviving rules whose ids drifted (e.g. the deleted
                // profile shared a name with another one — unlikely, but
                // harmless to re-pair) get reconciled against the remaining
                // Profiles list. Migrate never deletes a rule on its own.
                LlmSettingsMigrations.RepairProfileReferences(LlmSettingsService.Instance.Current);
                LlmSettingsService.Instance.Save();
            }
            Reload();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── TextChanged → push value immediately for live auto-save ────────────
    //
    // x:Bind TwoWay on TextBox updates the source on LostFocus only.
    // These handlers push the value on every keystroke so auto-save fires
    // during typing, matching the cadence of slider/NumberBox interactions.

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is ProfileViewModel vm)
            vm.Name = tb.Text;
    }

    private void SystemPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is ProfileViewModel vm)
            vm.SystemPrompt = tb.Text;
    }

    // ── Model picker (AutoSuggestBox) ──────────────────────────────────────
    //
    // AutoSuggestBox is the canonical Win11 control for "free text + filtered
    // suggestions" (Start Menu, Settings search, Explorer search). Replaces
    // a ComboBox IsEditable="True" whose dropdown-arrow mouse interaction
    // was unreliable in WinUI 3.
    //
    // Text is OneWay (VM → UI) so the stored model name shows regardless of
    // whether it is in the suggestion list. UI → VM flows through:
    //   - TextChanged (Reason == UserInput) as the user types — also filters
    //     the suggestion list live.
    //   - SuggestionChosen when the user picks an item from the dropdown.
    //   - QuerySubmitted when the user presses Enter on free text not in
    //     the list (Ollama offline, model renamed, etc.).
    //   - GotFocus opens the suggestion list so a mouse click on the chevron
    //     or the field exposes the available models without typing first.

    private void ModelSuggest_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox box) return;

        box.ItemsSource = FilterModels(box.Text);
        box.IsSuggestionListOpen = true;
    }

    private void ModelSuggest_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only react to user typing — ignore programmatic updates (x:Bind
        // OneWay push, SuggestionChosen echo) to avoid filtering the list
        // on values the user did not intend to search for.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        if (sender.Tag is ProfileViewModel vm)
            vm.Model = sender.Text ?? string.Empty;

        sender.ItemsSource = FilterModels(sender.Text);
    }

    private void ModelSuggest_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (sender.Tag is ProfileViewModel vm && args.SelectedItem is string selected)
            vm.Model = selected;
    }

    private void ModelSuggest_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (sender.Tag is not ProfileViewModel vm) return;

        var text = args.ChosenSuggestion is string s ? s : args.QueryText;
        vm.Model = text ?? string.Empty;
    }

    private List<string> FilterModels(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return AvailableModelNames.ToList();

        return AvailableModelNames
            .Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── Add ─────────────────────────────────────────────────────────────────

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var s = LlmSettingsService.Instance.Current;
        s.Profiles.Add(new RewriteProfile
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 12),
            Name = "",
            Model = "",
            SystemPrompt = ""
        });
        LlmSettingsService.Instance.Save();

        int index = s.Profiles.Count - 1;
        var vm = new ProfileViewModel();
        vm.LoadFrom(index, s.Profiles[index], isNew: true);
        TrackProfile(vm);
        Profiles.Add(vm);

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // Scope: the Profiles list only. Replaces user-authored profiles with
    // the three defaults (Lissage / Affinage / Arrangement) — pre-written
    // prompts tuned via autoresearch, Temperature 0.30, NumCtxK 8/16/16.
    // MigrateProfileIds re-pairs slot and rule references so anything
    // still pointing at "Lissage" / "Affinage" / "Arrangement" by name
    // picks up the fresh ids. ProfilesChanged triggers the host to
    // reload Rules + ShortcutSlots.
    private async void ResetSection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Settings_ResetProfilesDialog_Title"),
            Content = Loc.Get("Settings_ResetProfilesDialog_Content"),
            PrimaryButtonText = Loc.Get("Common_Reset"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var defaults = new LlmSettings();
        LlmSettingsService.Instance.Current.Profiles = defaults.Profiles;
        LlmSettingsMigrations.RepairProfileReferences(LlmSettingsService.Instance.Current);
        LlmSettingsService.Instance.Save();
        Reload();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }
}
