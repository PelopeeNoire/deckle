using System;
using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using WhispUI.Logging;

namespace WhispUI.Composition;

// ─── Boot-time font cache warm-up ────────────────────────────────────────────
//
// Custom font glyph loading is lazy in DirectWrite : un FontFamily packagé
// (par exemple "ms-appx:///Assets/Fonts/BitcountSingle.ttf#Bitcount Single")
// n'est pas chargé en mémoire ni shapé tant qu'un GlyphRun n'est pas demandé.
// Pour le HUD, ce premier GlyphRun arrive au premier hotkey, sur le UI
// thread, alors que l'utilisateur attend l'apparition du chrono. Résultat :
// flash visible où le rectangle est peint mais les digits Bitcount sont
// encore vides le temps que DirectWrite charge le fichier .ttf et shape les
// caractères "0123456789.".
//
// Solution canonique : déclencher une première shaping passe au boot via
// Win2D. La construction d'un CanvasTextLayout sur du texte qui contient
// les caractères de référence force IDWriteFactory à charger la font face,
// shaper les glyphes et populer le cache DirectWrite. Le cache étant
// process-wide, tous les TextBlock { FontFamily = "...Bitcount..." } du
// HudChrono trouveront ensuite leurs glyphes prêts au premier render.
//
// Coût mesuré attendu : ~5-15 ms au boot (shared CanvasDevice creation +
// font load + shape de "00.00.00"). Front-load qui ne grossit pas le coût
// total — le shared device est créé de toute façon par HudComposition à
// la première stroke Win2D.
internal static class FontPrewarmer
{
    private static readonly LogService _log = LogService.Instance;

    // Caractères du chrono : 8 digits + 2 séparateurs. Suffisant pour que
    // DirectWrite shape l'intégralité de l'alphabet utilisé par HudChrono.
    private const string BitcountWarmupText = "0123456789.";

    // Taille de référence proche de l'utilisation runtime du chrono. La taille
    // exacte n'a pas d'impact sur le shaping, juste sur la taille du layout
    // box — on prend une valeur cohérente pour limiter le travail superflu.
    private const float BitcountWarmupFontSize = 32f;

    public static void WarmBitcount()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ms-appx URI identique à celui utilisé par HudChrono.xaml ligne 65 —
            // toute divergence ferait charger une autre font face et raterait
            // le warming. Si la font est déplacée ou renommée, mettre à jour
            // les deux endroits ensemble.
            using var fmt = new CanvasTextFormat
            {
                FontFamily = "ms-appx:///Assets/Fonts/BitcountSingle.ttf#Bitcount Single",
                FontSize   = BitcountWarmupFontSize,
            };

            using var layout = new CanvasTextLayout(
                CanvasDevice.GetSharedDevice(),
                BitcountWarmupText,
                fmt,
                requestedWidth:  200f,
                requestedHeight: 80f);

            // Toucher LayoutBounds garantit que le layout est résolu (le
            // ctor seul peut différer le shaping selon la version Win2D).
            _ = layout.LayoutBounds;

            _log.Verbose(LogSource.App,
                $"font preload Bitcount Single | elapsed_ms={sw.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            // Fail-soft : si le préchargement plante (URI introuvable, font
            // corrompue, Win2D device init en échec), on log et on continue.
            // Le fallback est exactement le comportement actuel : DirectWrite
            // chargera la font lazy à la première utilisation.
            _log.Warning(LogSource.App,
                $"font preload Bitcount Single failed | error={ex.GetType().Name}: {ex.Message}");
        }
    }
}
