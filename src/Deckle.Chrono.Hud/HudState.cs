namespace Deckle.Chrono.Hud;

// Visual states the HudChrono can render. Cross-assembly (consumed by the
// App-side HudWindow + PlaygroundWindow), so public — internal would force
// InternalsVisibleTo plumbing for every consumer module.
//
// Recording/Transcribing/Rewriting drive the Composition stroke; Charging
// is the cold-boot "parked clock" with neutral digits; Hidden tears the
// stroke down. Message is technically a HudWindow-level state (the chrono
// itself is hidden when the HUD is in Message mode) — it lives here so the
// hosting state machine can pass a single enum to ApplyState without an
// extra mapping table.
public enum HudState
{
    Hidden,
    Charging,
    Recording,
    Transcribing,
    Rewriting,
    Message,
}
