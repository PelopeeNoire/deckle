using System;
using System.Collections.Generic;
using Deckle.Llm;

namespace Deckle.Llm;

// ─── État Ollama partagé entre les sous-sections de LlmPage ─────────────────
//
// Instancié par le host (LlmPage), rempli par RefreshOllamaStateAsync,
// passé aux sections qui en dépendent (Profiles, Models) via Initialize().
//
// Les sections s'abonnent à StateChanged pour se rebuilder quand la liste de
// modèles ou la disponibilité change. Les sections qui ne dépendent pas
// d'Ollama (General, ManualShortcut, Rules) ne touchent pas au contexte.
//
// Ce contexte n'est PAS un ViewModel — il n'observe pas de propriétés,
// il ne notifie pas de champ par champ. Il sert uniquement à partager l'état
// runtime (service, modèles, disponibilité) et à coordonner les rafraîchissements
// via un seul événement grossier.

internal sealed class LlmOllamaContext
{
    public OllamaService? Service { get; set; }

    public IReadOnlyList<OllamaModel> Models { get; set; } = Array.Empty<OllamaModel>();

    public bool Available { get; set; }

    public event EventHandler? StateChanged;

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
