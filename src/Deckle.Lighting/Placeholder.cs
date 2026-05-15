namespace Deckle.Lighting;

// Placeholder — module bootstrap (J0b of feat/ambient-lighting).
//
// Deckle.Lighting hosts the LED-output domain : driver implementations
// (Philips Hue Entertainment v2 first, WLED and DMX later), the
// `ILightOutput` abstraction, and the zone/frame model that downstream
// consumers (Lighting.Ambient for screen-driven LEDs, future
// Lighting.Informative for Home Assistant data display) push colours
// through. This module knows nothing about why a colour was chosen ;
// it only knows how to deliver it to the lamps.
//
// Real content arrives in J2 (Hue Entertainment v2 driver implemented
// in-house — DTLS-PSK via P/Invoke OpenSSL/mbedTLS, see
// docs/research--hue-entertainment-v2--2026-05-15.md). The
// `ILightOutput` abstraction is designed in J2 to keep the door open
// for WLED and DMX drivers without later refactor.
internal static class Placeholder
{
}
