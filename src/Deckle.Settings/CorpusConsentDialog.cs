using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Deckle.Localization;
using Deckle.Logging;

namespace Deckle.Settings;

// ─── CorpusConsentDialog ──────────────────────────────────────────────────
//
// Opt-in consent for corpus logging. Shown the first time the user flips
// the toggle from off to on. Cancelling reverts the toggle.
//
// This is a consent prompt, not a warning: no "Don't show again", no
// severity icon. The user is explicitly authorizing data capture to their
// own filesystem.
//
// Wording: all user-facing strings are loaded from Resources.resw via
// Loc.Get. The corpus.jsonl path (`where`) stays hardcoded — it's a
// filesystem path, not copy. See docs/reference--localization--1.0.md.

internal static class CorpusConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string where = CorpusPaths.GetDirectoryPath();

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("CorpusConsent_Body_Intro")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhatHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("CorpusConsent_Body_What")
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
            Text = Loc.Get("CorpusConsent_Body_Remind")
        });

        var dialog = new ContentDialog
        {
            Title = Loc.Get("CorpusConsent_Title"),
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
