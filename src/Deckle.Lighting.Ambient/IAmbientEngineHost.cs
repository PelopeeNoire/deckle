namespace Deckle.Lighting.Ambient;

// Bridge that will let the future AmbientEngine read its dependencies
// without touching the host App's SettingsService. Same pattern as
// IWhispEngineHost: a typed interface implemented by the host project,
// instantiated and passed to the engine constructor so the engine stays
// free of any reference to the App project or the root settings shape.
//
// Empty at J0c — the engine doesn't exist yet (lands in J3 as the
// minimal end-to-end pipeline). Once it does, this interface will
// expose the AmbientSettings snapshot, possibly the screen-capture
// monitor selection if it gets globalised, and any cross-module hook
// the engine needs (e.g. a SaveSettings callback if auto-calibration
// of bridge state is added).
public interface IAmbientEngineHost
{
}
