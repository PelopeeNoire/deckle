using Windows.UI;
using WhispUI.Controls;

namespace WhispUI.Composition;

// Semantic palette + Segoe Fluent glyphs for HUD messages.
//
// The Critical "full" hex matches SystemFillColorCriticalBrush exactly. The
// other "full" values are close to their SystemFill* counterparts but the
// Figma palette uses the explicit decay (Attenuated) variants which have no
// theme-resource equivalent — so the full set lives here as raw hex to keep
// both phases consistent under animation.
//
// Light mode only. Dark mode adds a separate ResolveDark(...) path in a later pass.
internal static class HudPalette
{
    internal readonly record struct Entry(Color Full, Color Attenuated, string Glyph);

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    internal static Entry Resolve(MessageKind kind) => kind switch
    {
        MessageKind.Success       => new(Hex(0x0F, 0x7B, 0x0F), Hex(0x34, 0x56, 0x34), "\uE65F"),
        MessageKind.Critical      => new(Hex(0xC4, 0x2B, 0x1C), Hex(0x8C, 0x59, 0x54), "\uEDAE"),
        MessageKind.Warning       => new(Hex(0x9D, 0x5D, 0x00), Hex(0x62, 0x52, 0x3B), "\uEDB1"),
        MessageKind.Informational => new(Hex(0x00, 0x5F, 0xB7), Hex(0x45, 0x5C, 0x72), "\uEDAD"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
