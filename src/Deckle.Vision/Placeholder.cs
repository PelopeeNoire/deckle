namespace Deckle.Vision;

// Placeholder — module bootstrap (J0b of feat/ambient-lighting).
//
// Deckle.Vision hosts screen capture (Windows.Graphics.Capture) and the
// image-analysis primitives that consume the captured frames: downsample,
// black-border detection, dominant colour extraction, and later
// scene-classification (CLIP) or OCR/UI awareness. Reusable beyond
// ambient lighting (future AskHud, OCR-driven flows). Flat namespace —
// no internal sub-namespaces by design, aligned with the project's
// existing module shape.
//
// Real content arrives in J1 (ScreenCaptureService — bare capture loop)
// and J4 (image analysis: BlackBorderDetector, FrameDownsampler,
// DominantColorExtractor). This file is here only so the csproj
// compiles before any real type lives in the module — Microsoft.NET.Sdk
// tolerates an empty source list, but keeping a placeholder makes the
// module's purpose discoverable from a single read.
internal static class Placeholder
{
}
