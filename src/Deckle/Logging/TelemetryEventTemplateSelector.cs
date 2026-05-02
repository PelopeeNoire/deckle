using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deckle.Logging;

// ── TelemetryEventTemplateSelector ──────────────────────────────────────────
//
// Picks the right DataTemplate for every row in LogWindow's list. Four
// kinds share the same collection:
//
//   Kind.Log         → one of six level-colored templates (Verbose/Info/…)
//   Kind.Latency     → compact [LATENCY] row
//   Kind.Corpus      → compact [CORPUS] row
//   Kind.Microphone  → routes through the Info template (the prebuilt
//                      Text already carries "[CAPTURE] Mic telemetry over
//                      …" so the row reads like any other [CAPTURE] line)
//
// Instantiated twice in XAML resources (NoWrapSelector / WrapSelector) so
// the Word-wrap toggle swaps the entire set. Every slot is required at
// XAML load time; Pick() dispatches by kind + level. Non-TelemetryEvent
// inputs fall back to the Info template (never exercised in practice —
// the list only ever contains TelemetryEvent instances).
public sealed class TelemetryEventTemplateSelector : DataTemplateSelector
{
    // Log-kind (6 severity levels).
    public DataTemplate? Verbose   { get; set; }
    public DataTemplate? Info      { get; set; }
    public DataTemplate? Success   { get; set; }
    public DataTemplate? Warning   { get; set; }
    public DataTemplate? Error     { get; set; }
    public DataTemplate? Narrative { get; set; }

    // Latency / Corpus kinds — one template each.
    public DataTemplate? Latency { get; set; }
    public DataTemplate? Corpus  { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => Pick(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => Pick(item);

    private DataTemplate Pick(object item)
    {
        if (item is TelemetryEvent e)
        {
            return e.Kind switch
            {
                TelemetryKind.Latency    => Latency!,
                TelemetryKind.Corpus     => Corpus!,
                TelemetryKind.Microphone => Info!,
                TelemetryKind.Log        => e.Level switch
                {
                    LogLevel.Verbose   => Verbose!,
                    LogLevel.Info      => Info!,
                    LogLevel.Success   => Success!,
                    LogLevel.Warning   => Warning!,
                    LogLevel.Error     => Error!,
                    LogLevel.Narrative => Narrative!,
                    _                  => Info!,
                },
                _ => Info!,
            };
        }
        return Info!;
    }
}
