using Windows.UI;

namespace Deckle.Vision;

// One snapshot from FrameSampler. Average is the arithmetic mean over
// the whole grid (the colour AmbientEngine pushes to the lamps). Grid
// is laid out row-major — Grid[row * Cols + col]. Both reflect the
// same source frame and are produced atomically inside FrameSampler.Process
// ; a new SampledFrame is allocated per processed frame and replaces
// LatestSample via a single volatile write. Consumers read it via
// volatile read and use it directly — the array is never mutated after
// publication.
public sealed record SampledFrame(Color Average, Color[] Grid, int Cols, int Rows);
