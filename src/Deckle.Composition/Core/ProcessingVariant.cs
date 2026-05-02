namespace Deckle.Composition;

// Variant the processing stroke is rendering. The HUD picks one based
// on its state (Recording / Transcribing / Rewriting); the live stroke
// animates its own effect properties toward the matching variant values
// without rebuilding anything — except on Recording ↔ (Transcribing /
// Rewriting) crossings where the rotation-frozen vs spinning pipelines
// cannot share a SpriteVisual, and the host tears the stroke down and
// rebuilds a fresh one (see HudChrono.AttachProcessingVisual in the App
// shell).
//
// Public so cross-assembly consumers (HudComposition factory in App,
// future Ask-Ollama window) can pass the same discriminant without
// going through string parsing or per-window enum duplication.
public enum ProcessingVariant { Recording, Transcribing, Rewriting }
