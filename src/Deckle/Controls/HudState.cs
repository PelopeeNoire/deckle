namespace Deckle.Controls;

// HudState moved to Deckle.Chrono.Hud (the chrono control is the one that
// drives those visual states ; cross-assembly use requires public). The
// types remaining here belong to HudMessage — the message kind enum and
// its payload record. Both stay assembly-private (App-only consumers :
// HudWindow, HudOverlayWindow, HudPalette, HudMessage).

internal enum MessageKind
{
    Success,
    Critical,
    Warning,
    Informational,
}

internal sealed record MessagePayload(
    MessageKind Kind,
    string Title,
    string Subtitle,
    TimeSpan Duration);
