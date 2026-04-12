using System;
using System.Collections.ObjectModel;
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
//  - ProfilesChanged event pour notifier Rules et ManualShortcut

public sealed partial class LlmProfilesSection : UserControl
{
    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();

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
