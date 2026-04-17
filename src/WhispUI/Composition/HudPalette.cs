using WhispUI.Controls;

namespace WhispUI.Composition;

// Semantic palette + Segoe Fluent glyphs for HUD message badges.
//
// Each kind maps to a Win11 system fill brush key + its semantic glyph.
// The brush is resolved at runtime via Application.Current.Resources so it
// tracks the live theme (dark/light/high-contrast) automatically — no
// hardcoded hex.
internal static class HudPalette
{
    internal readonly record struct Entry(string BrushKey, string Glyph);

    internal static Entry Resolve(MessageKind kind) => kind switch
    {
        MessageKind.Success       => new("SystemFillColorSuccessBrush",   "\uE73E"), // CheckMark
        MessageKind.Critical      => new("SystemFillColorCriticalBrush",  "\uE783"), // Error
        MessageKind.Warning       => new("SystemFillColorCautionBrush",   "\uE7BA"), // Warning
        MessageKind.Informational => new("SystemFillColorAttentionBrush", "\uE946"), // Info
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
