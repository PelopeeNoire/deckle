namespace Deckle.Lighting.Ambient;

// Which border of the screen a light reflects, in the simplified
// "ambilight" model carried by V1. Each light is assigned exactly one
// zone ; the engine then computes one colour per zone (the mean of the
// pixels inside the zone's border rectangle) and pushes that colour to
// every light assigned to it.
//
// Why a discrete enum vs. free (X, Y) coordinates. Free positioning is
// flexible but the placement UX (drag a marker to "above the screen")
// fights the user's mental model — most ambient setups have lights on
// a single edge each, not in arbitrary spots. The enum maps directly
// to that mental model : "this lamp is on the top, that one on the
// left". HyperHDR's reference model uses a richer "N LEDs equidistant
// on one edge with a depth fraction" scheme ; we collapse that to the
// degenerate case of 1 light per edge, which covers Louis's 3-lamp
// setup and the typical Hue ambilight layout. Sub-edge positioning
// (two lamps on the same edge sampling different fractions) is
// out of scope V1 — when it lands, we'll either add a per-light X
// offset on this enum or fall back to (X, Y) coords.
public enum LightZone
{
    /// <summary>The light is not driven by the ambient pipeline.
    /// Selected lights stay at whatever colour the bridge last had ;
    /// the engine never touches them. Useful when the user has more
    /// Hue lights in the group than they want to colour-cycle.</summary>
    None,

    /// <summary>The light reflects the top edge of the screen — the
    /// engine averages pixels in the band [0,1]×[0, BorderDepth].</summary>
    Top,

    /// <summary>The light reflects the bottom edge — the engine
    /// averages pixels in the band [0,1]×[1-BorderDepth, 1].</summary>
    Bottom,

    /// <summary>The light reflects the left edge — the engine averages
    /// pixels in the band [0, BorderDepth]×[0,1].</summary>
    Left,

    /// <summary>The light reflects the right edge — the engine averages
    /// pixels in the band [1-BorderDepth, 1]×[0,1].</summary>
    Right,
}
