using WhispUI.Controls;

namespace WhispUI.Composition;

// Semantic palette + Segoe Fluent glyphs for HUD message badges.
//
// Each kind maps to a resource key local to HudMessage.xaml + its semantic
// glyph. The matching brush is declared inside HudMessage.xaml's
// <UserControl.Resources> with {ThemeResource SystemFillColor*}, so the
// colour tracks dark / light / high-contrast automatically.
//
// Why not look up SystemFillColor*Brush directly via Application.Current.
// Resources[key]? Because that code path does NOT walk ThemeDictionaries —
// the themed tokens only resolve through the XAML {ThemeResource} binding
// or a ResourceDictionary that was constructed with theme awareness.
// Looking them up by string at the Application root silently returned null,
// which the badge fallback painted as Colors.Gray — that's the "pastille
// grise" regression we saw on the Copied/Pasted/Info badges.
internal static class HudPalette
{
    internal readonly record struct Entry(string BrushKey, string Glyph);

    internal static Entry Resolve(MessageKind kind) => kind switch
    {
        MessageKind.Success       => new("BadgeSuccessBrush",  "\uE73E"), // CheckMark
        MessageKind.Critical      => new("BadgeCriticalBrush", "\uE783"), // Error
        MessageKind.Warning       => new("BadgeWarningBrush",  "\uE7BA"), // Warning
        MessageKind.Informational => new("BadgeInfoBrush",     "\uE946"), // Info
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
