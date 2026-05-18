namespace Deckle.Vision;

// Optional hints an IFrameAnalyzer can publish about a SampledFrame.
// Every field is nullable so analyzers can fill only what they're
// designed to compute — a saliency-only analyzer leaves day-night
// alone, an audio-only analyzer leaves saliency alone. Consumers
// (today : nothing ; tomorrow : AmbientEngine.SampleZone) treat the
// missing fields as "no opinion" and fall back to their default
// behaviour.
//
// The record is intentionally minimal at this stage. New hints can
// be added without breaking existing analyzers : the default
// constructor zeros every field, so an analyzer that doesn't know
// about a new property still produces a valid (mostly-null) hint.

public readonly record struct FrameAnalysisHint
{
    /// <summary>Mean luminance of the frame in [0, 1]. Null when the
    /// analyzer hasn't computed luma.</summary>
    public double? MeanLuma { get; init; }

    /// <summary>Normalised position of the brightest cluster in the
    /// frame, in [0, 1]² grid space. Null when no saliency analysis
    /// ran. Use case : when a peak sits near the centre, weight the
    /// centre cells higher so a sudden bright element pulls the
    /// average toward it.</summary>
    public (double X, double Y)? PeakPosition { get; init; }

    /// <summary>Rough day / night estimate in [-1, 1] where +1 is
    /// "definitely daylight" and -1 is "definitely nighttime". Null
    /// when no scene heuristic ran. Use case : bias the lamp toward
    /// daylight-temperature white (~5600 K) when positive, toward
    /// warmer ambient temps when negative.</summary>
    public double? DayNightEstimate { get; init; }
}
