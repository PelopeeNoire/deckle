using Microsoft.UI.Dispatching;
using Deckle.Logging;

namespace Deckle.Shell;

// ─── DispatcherQueueExtensions ─────────────────────────────────────────────
//
// Wrapper autour de DispatcherQueue.TryEnqueue qui logue un Warning quand
// l'enqueue est rejeté (queue shut down). Sans ça, l'event UI est perdu en
// silence — typique au teardown de window où StatusChanged engine arrive
// alors que la dispatcher queue est déjà fermée.
//
// Garde anti-récursion : si LogWindow appelle TryEnqueueOrLog et que sa
// propre queue est fermée, le Warning loggé via LogService route à nouveau
// vers LogWindow → re-TryEnqueue → re-fail → boucle. Un flag thread-static
// court-circuite la deuxième tentative.
//
// Pourquoi pas un simple `if (!queue.TryEnqueue(...)) _log.Warning(...)`
// inline à chaque site ? Centraliser réduit la duplication (8 sites) et
// garantit que le pattern de garde anti-récursion est partout, sans risque
// d'oubli.

internal static class DispatcherQueueExtensions
{
    private static readonly LogService _log = LogService.Instance;

    [System.ThreadStatic]
    private static bool _logging;

    /// <summary>
    /// Enqueue le callback sur la dispatcher queue. Si l'enqueue échoue
    /// (queue fermée), logue un Warning avec la source et la description
    /// fournie par le caller, puis retourne false.
    /// </summary>
    /// <param name="queue">La dispatcher queue cible.</param>
    /// <param name="callback">Le delegate à exécuter sur le UI thread.</param>
    /// <param name="source">Constante LogSource pour identifier l'émetteur.</param>
    /// <param name="what">Description courte de l'event perdu (ex. "log entry", "recording state").</param>
    public static bool TryEnqueueOrLog(
        this DispatcherQueue queue,
        DispatcherQueueHandler callback,
        string source,
        string what)
    {
        bool ok = queue.TryEnqueue(callback);
        if (!ok && !_logging)
        {
            _logging = true;
            try
            {
                _log.Warning(source, $"DispatcherQueue.TryEnqueue rejected ({what}) — UI event dropped");
            }
            finally { _logging = false; }
        }
        return ok;
    }
}
