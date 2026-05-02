using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Localization;
using Deckle.Logging;

namespace Deckle.Settings;

// ─── AudioCorpusConsentDialog ──────────────────────────────────────────────
//
// Opt-in consent for the WAV-audio side of corpus logging. Audio capture is
// a stronger privacy posture than text (voice is biometric-adjacent), so it
// has its own dialog rather than being folded into the text-corpus prompt —
// the user consents to each data class explicitly.
//
// Cancelling reverts the toggle. Same pattern as CorpusConsentDialog, just
// different copy.
//
// Wording: all strings via Loc.Get / Resources.resw. The where path stays
// hardcoded — it's a filesystem path with a literal <profile> placeholder
// for the user, not copy that should ever be translated.

internal static class AudioCorpusConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string textRoot = CorpusPaths.GetDirectoryPath();
        string where = Path.Combine(textRoot, "<profile>", "audio");

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("AudioCorpusConsent_Body_Intro")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhatHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("AudioCorpusConsent_Body_What")
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
            Text = Loc.Get("AudioCorpusConsent_Body_Remind")
        });

        var dialog = new ContentDialog
        {
            Title = Loc.Get("AudioCorpusConsent_Title"),
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
