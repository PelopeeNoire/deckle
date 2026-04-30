using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Deckle.Composition;

namespace Deckle.Controls;

// Message card used for Pasted / Copied / Error / UserFeedback feedback.
//
// Fixed 272x78, fills the whole HUD window. The semantic badge is a 17x17
// disc (odd-side so the centre lands on a true pixel, not between four —
// anything smaller plus even means the inner glyph rasterises off-axis).
// The disc brush and the title / subtitle brushes are resolved from XAML
// ThemeResource bindings so dark / light / high-contrast swaps just work.
//
// Badge brushes live in the local UserControl.Resources, not in App.xaml —
// a code-side Application.Current.Resources[key] lookup does NOT walk
// ThemeDictionaries, so themed tokens like SystemFillColorAttention return
// null and the fallback (previously Colors.Gray) leaked through as the
// pastille grise we saw. Pulling them from this.Resources[key] does the
// right thing because the XAML {ThemeResource} bindings were evaluated
// inside the local RD.
public sealed partial class HudMessage : UserControl
{
    public HudMessage()
    {
        InitializeComponent();
    }

    // Pushes payload into the static layout. Called by HudWindow.SetState
    // when entering HudState.Message.
    internal void Show(MessagePayload payload)
    {
        var entry = HudPalette.Resolve(payload.Kind);

        MessageTitle.Text    = payload.Title    ?? string.Empty;
        MessageSubtitle.Text = payload.Subtitle ?? string.Empty;

        // Resources[key] hits the local <UserControl.Resources> dictionary
        // declared in HudMessage.xaml, where each BadgeBrush is a
        // SolidColorBrush whose Color is {ThemeResource SystemFillColor*}.
        // Theme changes on the UserControl re-evaluate that binding, so no
        // manual ActualThemeChanged handler is needed.
        if (Resources.TryGetValue(entry.BrushKey, out var obj) && obj is Brush brush)
            BadgeBackground.Background = brush;

        BadgeGlyph.Glyph = entry.Glyph;
    }
}
