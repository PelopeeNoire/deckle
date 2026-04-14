using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Settings.ViewModels;

namespace WhispUI.Settings.Llm;

// ─── Section Profiles de LlmPage ───────────────────────────────────────────
//
// Liste des profils de réécriture. Tout le contenu est déclaratif (XAML
// DataTemplate + ProfileViewModel). Le code-behind ne gère que :
//  - Reload() → repopule l'ObservableCollection depuis le POCO
//  - Click handlers (Save/Cancel/Delete/Add) via Tag={x:Bind}
//  - Model AutoSuggestBox handlers → push vers le VM + filtre la liste
//  - ProfilesChanged event pour notifier Rules et ManualShortcut

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
        var s = SettingsService.Instance.Current.Llm;
        for (int i = 0; i < s.Profiles.Count; i++)
        {
            var vm = new ProfileViewModel();
            vm.LoadFrom(i, s.Profiles[i], isNew: false);
            Profiles.Add(vm);
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ProfileViewModel vm)
        {
            vm.Save();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ProfileViewModel vm)
        {
            if (vm.IsNew)
            {
                var profiles = SettingsService.Instance.Current.Llm.Profiles;
                if (vm.ProfileIndex < profiles.Count)
                {
                    profiles.RemoveAt(vm.ProfileIndex);
                    SettingsService.Instance.Save();
                }
                Profiles.Remove(vm);
                ProfilesChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                vm.Cancel();
            }
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileViewModel vm)
            return;

        var dialog = new ContentDialog
        {
            Title = "Remove profile",
            Content = $"Remove \"{vm.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var profiles = SettingsService.Instance.Current.Llm.Profiles;
            if (vm.ProfileIndex < profiles.Count)
            {
                profiles.RemoveAt(vm.ProfileIndex);
                SettingsService.Instance.Save();
            }
            Reload();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── TextChanged → push value immediately for dirty detection ──────────
    //
    // x:Bind TwoWay on TextBox updates the source on LostFocus only.
    // These handlers push the value on every keystroke so IsDirty updates
    // immediately and the Save/Cancel bar appears without waiting for blur.

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
        var s = SettingsService.Instance.Current.Llm;
        s.Profiles.Add(new RewriteProfile
        {
            Name = "",
            Model = "",
            SystemPrompt = ""
        });

        int index = s.Profiles.Count - 1;
        var vm = new ProfileViewModel();
        vm.LoadFrom(index, s.Profiles[index], isNew: true);
        Profiles.Add(vm);

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }
}
