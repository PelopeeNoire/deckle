using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
// Copy is kept direct: the log is chatty, it grows fast in steady-state, and
// the feature exists to chase a specific problem — not to run permanently.

internal static class ApplicationLogConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string? path = CorpusPaths.GetDirectoryPath();
        string where = string.IsNullOrEmpty(path)
            ? "a \"benchmark\\telemetry\" subfolder next to the application"
            : path;

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "WhispUI will mirror every in-app log line to a local JSONL " +
                "file (app.jsonl) so you can diagnose a specific issue " +
                "across restarts."
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
                "Every log line from every subsystem — capture, VAD, Whisper, " +
                "rewriting, clipboard, paste, UI — at every verbosity level. " +
                "Log messages can include partial transcription text when a " +
                "pipeline step logs what it just produced. Nothing is sent " +
                "over the network."
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
                "The log is chatty — expect roughly 2 MB per 30 minutes of " +
                "active use. It rotates every 5000 lines (app-1.jsonl, " +
                "app-2.jsonl, …) with no cap on archives, so turn it off " +
                "once you have what you need and prune the archives by hand."
        });

        var dialog = new ContentDialog
        {
            Title = "Enable application log",
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
