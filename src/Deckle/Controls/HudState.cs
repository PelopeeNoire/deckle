namespace Deckle.Controls;

internal enum HudState
{
    Hidden,
    Charging,
    Recording,
    Transcribing,
    Rewriting,
    Message,
}

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
