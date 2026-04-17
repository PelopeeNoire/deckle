using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhispUI.Composition;

namespace WhispUI.Controls;

// Message card used for Pasted / Copied / Error / UserFeedback feedback.
//
// Fixed 272x78, fills the whole HUD window. The semantic badge (16x16) is
// painted with a Win11 system fill brush resolved from Application.Resources
// at Show time — tracks the live theme. Title + subtitle stay in standard
// Win11 text brushes (theme-resource-bound). The rounded card surface is the
// MessageCard Border in XAML (LayerFillColorDefaultBrush + OverlayCornerRadius).
// No Composition stroke for messages.
public sealed partial class HudMessage : UserControl
{
    private string? _badgeBrushKey;

    public HudMessage()
    {
        InitializeComponent();

        // Re-resolve the badge brush on theme switch so the 16x16 badge
        // follows dark/light. The brush keys live in the system theme
        // dictionaries — assigning a Brush in code does not track theme
        // changes the way ThemeResource bindings do.
        MessageRoot.ActualThemeChanged += (_, _) =>
        {
            if (_badgeBrushKey is not null)
                BadgeBackground.Background = ResolveBadgeBrush(_badgeBrushKey);
        };
    }

    // Pushes payload into the static layout. Called by HudWindow.SetState
    // when entering HudState.Message.
    internal void Show(MessagePayload payload)
    {
        var entry = HudPalette.Resolve(payload.Kind);

        MessageTitle.Text    = payload.Title    ?? string.Empty;
        MessageSubtitle.Text = payload.Subtitle ?? string.Empty;

        _badgeBrushKey = entry.BrushKey;
        BadgeBackground.Background = ResolveBadgeBrush(entry.BrushKey);
        BadgeGlyph.Glyph           = entry.Glyph;
    }

    private static Brush ResolveBadgeBrush(string key) =>
        (Application.Current.Resources[key] as Brush)
        ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
