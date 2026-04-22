// Stub for the HudChrono dependency on WhispUI.Settings.SettingsService.
// HudChrono reads one field — Current.Recording.MaxRecordingDurationSeconds
// — to cap the displayed elapsed time during Recording. The playground
// has no persistence layer, no JSON config, no multi-section AppSettings
// story; we just publish the minimum surface that compiles HudChrono
// unmodified and hands it a sensible default (1 h).
//
// This file's namespace is intentionally WhispUI.Settings (not
// HudPlayground.Stubs) so the linked HudChrono.xaml.cs reference
// `Settings.SettingsService.Instance.Current.Recording.MaxRecordingDurationSeconds`
// (resolved from HudChrono's namespace WhispUI.Controls) binds to this
// stub without any source edit.
//
// Shape mirrored from src/WhispUI/Settings/SettingsService.cs +
// AppSettings.cs, kept as tight as possible:
//   SettingsService.Instance               — static singleton
//   SettingsService.Current                — AppSettings
//   AppSettings.Recording                  — RecordingSettings
//   RecordingSettings.MaxRecordingDurationSeconds — int (seconds)
namespace WhispUI.Settings;

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();

    public AppSettings Current { get; } = new();
}

public sealed class AppSettings
{
    public RecordingSettings Recording { get; } = new();
}

public sealed class RecordingSettings
{
    // 1 hour cap — dev tool, doesn't matter. HudChrono's cap kicks in
    // only when elapsed > MaxRecordingDurationSeconds, which is already
    // well past the playground use case (we cycle Recording state via a
    // simulated RMS pump, not a real multi-hour session).
    public int MaxRecordingDurationSeconds { get; set; } = 60 * 60;
}
