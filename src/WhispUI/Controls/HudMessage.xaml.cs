using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WhispUI.Composition;

namespace WhispUI.Controls;

// Message card used for Pasted / Copied / Error / UserFeedback feedback.
//
// Card layout fixed at 272x78. The semantic badge (16x16) carries the full
// hue; the title + subtitle stay in standard Win11 text brushes. The
// composite halo+drop shadow lives in Composition (HudComposition) and is
// attached to ShadowHost — sized to the full UserControl bounds so the
// bleed extends into the transparent margin around the card during the
// Full phase of the hybrid resize.
public sealed partial class HudMessage : UserControl
{
    private const float CardWidth  = 272f;
    private const float CardHeight =  78f;
    // Default outer dims when the layout pass has not yet run. Match the
    // window's HUD_WIDTH_MESSAGE / HUD_HEIGHT_MESSAGE so the shadow starts
    // centered on the first attach.
    private const float DefaultOuterWidth  = 400f;
    private const float DefaultOuterHeight = 160f;

    private Visual? _shadowVisual;
    private CompositionPropertySet? _shadowAnim;

    public HudMessage()
    {
        InitializeComponent();

        // Window resizes mid-message (400x160 Full → 272x78 retracted) drive
        // an OuterRoot SizeChanged. Recompute the shadow Offset so the visual
        // stays centered on the card; otherwise the Offset baked at attach time
        // (computed for 400x160) would push it outside the smaller window.
        OuterRoot.SizeChanged += (_, _) => CenterShadow();
    }

    // Push payload into the static layout, attach the composite shadow, and
    // kick the Saturation animation. Called by HudWindow.SetState when
    // entering HudState.Message.
    public void Show(MessagePayload payload)
    {
        var entry = HudPalette.Resolve(payload.Kind);

        MessageTitle.Text    = payload.Title    ?? string.Empty;
        MessageSubtitle.Text = payload.Subtitle ?? string.Empty;

        BadgeBackground.Background = new SolidColorBrush(entry.Full);
        BadgeGlyph.Glyph           = entry.Glyph;

        AttachShadow(entry.Full, entry.Attenuated);
        AnimateToAttenuated();
    }

    private void AttachShadow(Color full, Color attenuated)
    {
        DetachShadow();

        var hostVisual = ElementCompositionPreview.GetElementVisual(ShadowHost);
        var compositor = hostVisual.Compositor;

        var (visual, props) = HudComposition.CreateMessageShadow(
            compositor, new Vector2(CardWidth, CardHeight), full, attenuated);

        _shadowVisual = visual;
        _shadowAnim   = props;
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, visual);

        // Initial position. Subsequent window resizes (Full → retracted) are
        // handled by the OuterRoot.SizeChanged subscription in the constructor.
        CenterShadow();
    }

    private void CenterShadow()
    {
        if (_shadowVisual is null) return;
        float outerW = (float)OuterRoot.ActualWidth;
        float outerH = (float)OuterRoot.ActualHeight;
        if (outerW <= 0f) outerW = DefaultOuterWidth;
        if (outerH <= 0f) outerH = DefaultOuterHeight;
        _shadowVisual.Offset = new Vector3(
            (outerW - CardWidth)  / 2f,
            (outerH - CardHeight) / 2f,
            0f);
    }

    private void AnimateToAttenuated()
    {
        if (_shadowAnim is null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        HudComposition.AnimateShadowToAttenuated(
            compositor, _shadowAnim, TimeSpan.FromMilliseconds(650));
    }

    private void DetachShadow()
    {
        if (_shadowVisual is null) return;
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, null);
        _shadowVisual = null;
        _shadowAnim   = null;
    }
}
