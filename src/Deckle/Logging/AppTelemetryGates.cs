using System;
using Deckle.Settings;

namespace Deckle.Logging;

// ── AppTelemetryGates ───────────────────────────────────────────────────────
//
// App-side bridge between Deckle.Logging's gates contract and the host's
// TelemetrySettings. Reads SettingsService on every property access — same
// posture as the legacy ReadSettingsToggle helper that lived in
// JsonlFileSink: a setting flipped through the Settings UI takes effect on
// the next emit, and a read happening before SettingsService is fully
// initialized falls through to "false" / null instead of throwing.
//
// Wired into Deckle.Logging once at startup via TelemetryGates.Configure
// in App.OnLaunched, before the first sink is attached.
internal sealed class AppTelemetryGates : ITelemetryGates
{
    public bool ApplicationLogToDisk => Read(s => s.ApplicationLogToDisk);
    public bool LatencyEnabled       => Read(s => s.LatencyEnabled);
    public bool CorpusEnabled        => Read(s => s.CorpusEnabled);
    public bool MicrophoneTelemetry  => Read(s => s.MicrophoneTelemetry);

    public string? StorageDirectoryOverride
    {
        get
        {
            try
            {
                string s = SettingsService.Instance.Current.Telemetry.StorageDirectory ?? "";
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool Read(Func<TelemetrySettings, bool> reader)
    {
        try
        {
            return reader(SettingsService.Instance.Current.Telemetry);
        }
        catch
        {
            return false;
        }
    }
}
