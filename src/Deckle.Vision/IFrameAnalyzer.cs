namespace Deckle.Vision;

// Extension slot for richer frame interpretation than the plain
// gamma-correct averaging FrameSampler does today. Nothing implements
// this interface yet — it's posed here so the future scene-aware
// pipeline (saliency, day/night detection, emitted-vs-reflected
// light bias) can land without a refactor of either the AmbientEngine
// or the FrameSampler.
//
// Why this lives in Deckle.Vision and not Deckle.Lighting.Ambient :
// the same interface should work for a future Deckle.Audio.Analysis
// equivalent (music → colour heuristics) ; the consumer (AmbientEngine)
// references the abstraction, the producer pluggability stays out of
// the consumer's tree.
//
// Implementation sketch when the time comes :
//   - One analyzer per concern (saliency, day/night). They consume
//     a SampledFrame (and possibly a window of recent frames for
//     temporal cues) and produce a FrameAnalysisHint.
//   - The AmbientEngine snapshots the union of hints once per tick
//     and passes them down to SampleZone, which uses them to bias
//     the mean — e.g. weight the centre cells higher when the
//     saliency map peaks there, or shift the average toward the
//     temperature of the dominant highlight cluster.
//   - All hint fields are optional ; an analyzer that can't produce
//     one (e.g. no audio analyzer registered) leaves it null and
//     SampleZone falls back to the plain mean.

public interface IFrameAnalyzer
{
    /// <summary>Inspects a sampled frame and returns optional hints
    /// the downstream consumer can use to bias its colour reduction.
    /// Implementations should be cheap — this runs on every tick of
    /// the capture pump (~15 Hz). Return <c>default</c> when the
    /// analyzer has nothing useful to say for this frame.</summary>
    FrameAnalysisHint Analyze(SampledFrame frame);
}
