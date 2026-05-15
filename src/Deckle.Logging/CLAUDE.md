# CLAUDE.md — Deckle.Logging

Hub télémétrie unique de l'app. Toute observation runtime — événements applicatifs, latence transcription, télémétrie micro, corpus benchmark — passe par `TelemetryService.Instance`. Les sinks sont interchangeables et inscrits par l'app au boot : `JsonlFileSink` pour la persistance disque dans `<storage>/{app,latency,microphone,corpus}.jsonl`, `LogWindow` pour le display live, `HudFeedbackSink` pour pousser les `UserFeedback` Critical ou Warning sur la HUD. Cette architecture est l'invariant central du module — tout chemin parallèle (`Console.WriteLine`, `Debug.WriteLine`, `File.AppendAllText`, `*Logger.cs` dupliqué) dans le code métier est une régression et doit être refusé en review.

L'exception unique et subordonnée est le helper `DebugLog` file-based pour crash natif non rattrapable, instrumentation temporaire jamais commitée en l'état. Toute autre apparition d'écriture file ou console parallèle est à corriger.

## Quatre canaux

`TelemetryService.Log(source, message, level, feedback)` porte les événements applicatifs ordinaires. `TelemetryService.Latency(payload)` émet un row `LatencyPayload` structuré par transcription terminée (durées de chaque étape, tokens, outcomes). `TelemetryService.Corpus(payload)` émet le texte brut Whisper pour benchmark, gated par le setting « Log corpus » (off par défaut, raison RGPD). `TelemetryService.Microphone(payload)` émet le résumé RMS par recording, gated par le setting « Log microphone » (off par défaut). Chaque appel construit un `TelemetryEvent` (timestamp + kind + session + payload + texte précompilé pour le rendu LogWindow) et le dispatche aux `ITelemetrySink` enregistrés.

Le facade `LogService.Instance` est une convenience wrapper au-dessus de `TelemetryService.Log(...)`. Les méthodes `Info`, `Verbose`, `Warning`, `Error`, `Success`, `Narrative` produisent toutes des `TelemetryEvent` du canal Log avec le niveau correspondant. C'est l'API par défaut pour le code applicatif — `TelemetryService.Instance` direct est nécessaire seulement pour les canaux non-Log (Latency, Corpus, Microphone).

## Inventaire normatif et discipline

L'inventaire normatif des mesures par étape vit dans [docs/reference--logging-inventory--1.0.md](../../docs/reference--logging-inventory--1.0.md). Ce document est la **source de vérité** pour le vocabulaire d'unités (durées en `ms` entier ou `s` à une décimale, RMS linéaire `[0, 1]` à 4 décimales, niveau dBFS à 1 décimale, etc.), les niveaux de sévérité (Verbose, Info, Success, Warning, Error, Narrative — chacun avec sa visibilité dans la SelectorBar LogWindow), les gabarits standards par étape (`Info MODEL Loading model` court Capital + `Verbose MODEL load complete | load_ms=X | backend=Vulkan` détaillé machine-greppable), et la cartographie des erreurs avec leur sévérité cible et leur UserFeedback associé.

Avant d'ajouter ou de modifier un log, lire la section concernée du document. Toute divergence (unité non canonique, niveau de sévérité mal choisi, format mélangé Info + Verbose, message multi-ligne, mention de la source dans le message) est une régression. Le pattern existant dans le code voisin n'est pas un standard — c'est éventuellement de la dette à corriger, pas un modèle à imiter.

Quand on ajoute une étape mesurée, la procédure est. Respecter la nomenclature d'unités. Étendre le `record` payload approprié dans `Logging/TelemetryEvent.cs` avec un `JsonPropertyName` snake_case (le sink JSONL le sérialise automatiquement sans nouveau code). Mettre à jour le `text` précompilé dans `TelemetryService.<Kind>()` pour que la ligne LogWindow reflète la nouvelle info — au minimum un résumé compact, le détail complet vit dans le JSONL. Passer la mesure de `[à instrumenter]` à `[logué]` dans l'inventaire, section concernée, et mettre à jour le gabarit standard si la ligne Verbose change. Vérifier que les consommateurs existants ne cassent pas (recherche sur `payload.<Field>` ou `<PayloadType>(...)` dans tout le projet).

## Format par niveau

**Info et Success** sont des phrases Capital courtes lues comme des jalons dans la vue Activity (par exemple « Loading model », « Recording start », « Transcription complete (5 seg) »). Pas de `k=v`, pas d'unités techniques. Un détail court entre parenthèses reste admis quand il porte l'essentiel du jalon (backend, durée perçue, outcome). Toute Info ou Success qui porte des mesures techniques est doublée d'un **Verbose miroir** de format `<action ou état> | k1=v1 | k2=v2` (préfixe verbe en minuscule, mesures séparées par ` | `, une seule ligne, ne pas répéter le nom de la source dans le message).

**Warning et Error** sont des phrases Capital riches en prose (par exemple « Ollama busy — model X resident (2.1 GB). Waited 60 s so far… »). Pas de `k=v` dans la prose visible. Si des détails machine-greppables sont utiles pour le diag, ajouter un Verbose miroir parallèle.

**Narrative** est une phrase UX anglais complète adressée à l'utilisateur, ponctuée, qui raconte ce que fait l'app à l'étape en cours et pourquoi. Une narrative par grand jalon (model load, record start, VAD, transcribe, rewrite, clipboard, paste, done). Complète les Info ou Success — ne les remplace pas. Pas de mesure technique dans la narrative.

## Gates

Le module expose `TelemetryGates.Configure(IAppTelemetryGates impl)` que l'app appelle au boot pour brancher la lecture des settings utilisateur (toggles « Log latency », « Log microphone », « Log corpus », et chemin de stockage). Les sinks consultent `TelemetryGates.Current` avant d'écrire pour décider si une mesure gated atterrit sur disque. Sans `Configure`, la posture est fermée — tous les toggles à false, pas d'override de chemin. Conséquence : `Configure` est appelé en tout premier dans `App.OnLaunched` (après la migration de settings, avant l'inscription des sinks).
