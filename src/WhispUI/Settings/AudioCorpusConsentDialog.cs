using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ─── AudioCorpusConsentDialog ──────────────────────────────────────────────
//
// Opt-in consent for the WAV-audio side of corpus logging. Audio capture is
// a stronger privacy posture than text (voice is biometric-adjacent), so it
// has its own dialog rather than being folded into the text-corpus prompt —
// the user consents to each data class explicitly.
//
// Cancelling reverts the toggle. Same pattern as CorpusConsentDialog, just
// different copy.

internal static class AudioCorpusConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string? textRoot = CorpusLog.GetDirectoryPath();
        string where = string.IsNullOrEmpty(textRoot)
            ? "a \"corpus-audio\" subfolder of the corpus folder (auto-resolved next to the app)"
            : Path.Combine(textRoot, "corpus-audio");

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "WhispUI will save the raw microphone audio of every " +
                "transcription as a 16 kHz mono WAV file, alongside the " +
                "existing JSONL text corpus. Useful for replaying a past " +
                "session against a different Whisper model or prompt."
        });

        body.Children.Add(new TextBlock
        {
            Text = "What gets captured",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "The exact audio passed to whisper.cpp — including any " +
                "silence and background noise picked up by the microphone. " +
                "One WAV per transcription. Nothing is sent over the network."
        });

        body.Children.Add(new TextBlock
        {
            Text = "Where it's stored",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = where
        });

        body.Children.Add(new TextBlock
        {
            Text = "Keep in mind",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Voice recordings are biometric-adjacent and the files can " +
                "be replayed verbatim. Review and purge the folder " +
                "regularly, and exclude it from any backup target you " +
                "don't control."
        });

        var dialog = new ContentDialog
        {
            Title = "Enable audio corpus",
            Content = body,
            PrimaryButtonText = "Enable",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
