using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using WhispUI.Logging;

namespace WhispUI.Settings;

// ─── CorpusConsentDialog ──────────────────────────────────────────────────
//
// Opt-in consent for corpus logging. Shown the first time the user flips
// the toggle from off to on. Cancelling reverts the toggle.
//
// This is a consent prompt, not a warning: no "Don't show again", no
// severity icon. The user is explicitly authorizing data capture to their
// own filesystem.

internal static class CorpusConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string where = CorpusPaths.GetDirectoryPath();

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "WhispUI will append every transcription to a local JSONL " +
                "file — one folder per rewrite profile, one corpus.jsonl " +
                "inside each — so you can iterate on your rewrite prompts " +
                "offline."
        });

        var whatHeader = new TextBlock
        {
            Text = "What gets captured",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        };
        body.Children.Add(whatHeader);

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Raw transcription text, rewritten text, timing, model " +
                "and prompt identifiers. Nothing is sent over the network."
        });

        var whereHeader = new TextBlock
        {
            Text = "Where it's stored",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        };
        body.Children.Add(whereHeader);

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = where
        });

        var reminderHeader = new TextBlock
        {
            Text = "Keep in mind",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        };
        body.Children.Add(reminderHeader);

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Transcriptions may contain sensitive content. Review and " +
                "purge the files periodically, and exclude them from any " +
                "backup target you don't control."
        });

        var dialog = new ContentDialog
        {
            Title = "Enable corpus logging",
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
