using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhispUI.Logging;

namespace WhispUI.Settings;

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
// Copy stays direct: the JSONL row is dense enough to look intimidating
// and the feature is a calibration tool, not a permanent capture. Users
// usually flip it on for a few sessions, find their dBFS window, then
// turn it back off.

internal static class MicrophoneTelemetryConsentDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        string where = CorpusPaths.GetDirectoryPath();

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "WhispUI will log your microphone level distribution after " +
                "every recording, so you can calibrate the HUD level window " +
                "against the dBFS range your microphone actually produces."
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
                "Only level statistics — minimum, percentiles (p10, p25, p50, " +
                "p75, p90), maximum, and mean RMS — in dBFS. No audio. No " +
                "transcription. No words. Nothing is sent over the network."
        });

        body.Children.Add(new TextBlock
        {
            Text = "Where it's stored",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{where}\\microphone.jsonl"
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
                "One short JSON line per recording — small file. Useful when " +
                "calibrating, then turn it off. The same data also appears " +
                "in the LogWindow under [RECORD] for live inspection."
        });

        var dialog = new ContentDialog
        {
            Title = "Enable microphone telemetry",
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
