using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Localization;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ─── ApplicationLogConsentDialog ──────────────────────────────────────────
//
// Opt-in consent for writing the full application log to disk. Shown the
// first time the user flips the toggle from off to on. Cancelling reverts
// the toggle.
//
// Same pattern as CorpusConsentDialog and AudioCorpusConsentDialog — no
// "Don't show again", no severity icon. The user is explicitly authorizing
// a diagnostic capture to their own filesystem.
//
// Wording: all strings via Loc.Get / Resources.resw. The where path stays
// hardcoded — it's a filesystem path, not copy.

internal static class ApplicationLogConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string where = CorpusPaths.GetDirectoryPath();

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("ApplicationLogConsent_Body_Intro")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhatHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("ApplicationLogConsent_Body_What")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhereHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = where
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_RemindHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("ApplicationLogConsent_Body_Remind")
        });

        var dialog = new ContentDialog
        {
            Title = Loc.Get("ApplicationLogConsent_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Common_Enable"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
