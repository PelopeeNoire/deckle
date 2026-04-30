using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Deckle.Localization;
using Deckle.Logging;

namespace Deckle.Settings;

// ─── MicrophoneTelemetryConsentDialog ──────────────────────────────────────
//
// Opt-in consent for the per-Recording microphone telemetry stream. Shown
// the first time the user flips the toggle from off to on. Cancelling
// reverts the toggle.
//
// Same pattern as ApplicationLogConsentDialog / CorpusConsentDialog — no
// "Don't show again", no severity icon. The user is explicitly authorizing
// a diagnostic capture to their own filesystem.
//
// Wording: all strings via Loc.Get / Resources.resw. The where path stays
// hardcoded with the literal microphone.jsonl filename — both are
// filesystem identifiers, not copy.

internal static class MicrophoneTelemetryConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string where = CorpusPaths.GetDirectoryPath();

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("MicrophoneTelemetryConsent_Body_Intro")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhatHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("MicrophoneTelemetryConsent_Body_What")
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_WhereHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{where}\\microphone.jsonl"
        });

        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Common_Consent_RemindHeader"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Get("MicrophoneTelemetryConsent_Body_Remind")
        });

        var dialog = new ContentDialog
        {
            Title = Loc.Get("MicrophoneTelemetryConsent_Title"),
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
