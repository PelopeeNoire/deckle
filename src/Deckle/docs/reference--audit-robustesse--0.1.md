# Audit robustesse & sécurité — WhispUI

Référence consolidée des fragilités identifiées sur l'app à la date
2026-04-26, suite à un crash réel survenu lors de l'ouverture de
Settings → Rewriting pendant qu'un benchmark GPU saturait Ollama.

Trois passes ont été menées en parallèle :

1. Diagnostic ciblé du crash Ollama et des chemins async exposés
2. Audit sécurité initial (passe légère, en attendant la passe finale)
3. Audit robustesse étendu (zones non couvertes par la première passe)

Le présent document liste tous les findings et indique leur statut
(corrigé / planifié / backlog) au fur et à mesure des sessions.

---

## 1. Cause du bug "Ollama not running" au premier rewrite après boot

### Symptôme observé

Au premier hotkey rewrite après un démarrage frais du PC + lancement
de WhispUI, l'utilisateur voit systématiquement le message HUD :

> **Rewriter unavailable** — *Ollama is not reachable. No rewrite this
> session.*

Les rewrites suivants dans la même session marchent typiquement.

### Chaîne d'appel exacte

1. `App.OnLaunched` → `_engine.Warmup()`
   ([App.xaml.cs:246](../App.xaml.cs)).
2. `WhispEngine.Warmup` lance un thread background. Étape 3 du warmup
   ping Ollama via `IsAvailableAsync()` avec un timeout de **3 s, un
   seul essai** ([WhispEngine.cs:683-695](../WhispEngine.cs)).
3. Si Ollama n'a pas encore fini d'écouter sur `localhost:11434` à
   T+~5-10 s du boot (cas typique : service Windows en cours de
   démarrage), le ping échoue → `_ollamaWarmupOk = 0`.
4. Au **premier** `StartRecording` (premier hotkey), le bloc
   `Interlocked.Exchange(ref _warmupFlagsConsumed, 1)` consomme le
   flag et émet le warning HUD
   ([WhispEngine.cs:743-755](../WhispEngine.cs)).
5. Hotkeys suivants : flag déjà consommé, plus de re-warning. Et
   `LlmService.Rewrite` refait un appel HTTP à chaque fois — qui
   réussit puisque Ollama est désormais bien démarré.

### Wording trompeur

Le message dit *"No rewrite this session"* mais en pratique les
rewrites suivants marchent souvent. C'est un bug de wording en plus
du timing.

### Mitigation retenue

Combo trois étapes :

- Retry interne dans `OllamaService.IsAvailableAsync` (3 essais
  espacés de 500 ms) — couvre la race startup au warmup.
- Live re-probe juste avant l'émission du warning au consume du flag —
  corrige le diagnostic si Ollama est devenu reachable entre warmup et
  premier hotkey.
- Reword du message — supprime *"No rewrite this session"*.

Statut : à implémenter (Paquet A).

---

## 2. Audit sécurité initial — verdict global

Code globalement sain. Pas de failles évidentes. Trois findings
mineurs à reprendre lors de la passe finale crypto/threat-modeling.

### Findings

| ID | Surface | Gravité | Description | Recommandation | Statut |
|----|---------|---------|-------------|----------------|--------|
| SEC-1 | Réseau | Moyen | Endpoint Ollama configurable via UI sans validation. Si l'utilisateur entre `http://attacker.com:11434`, MITM possible sur HTTP non chiffré. ([Settings/Llm/LlmGeneralSection.xaml.cs:62](../Settings/Llm/LlmGeneralSection.xaml.cs)) | Warning UI si l'endpoint n'est pas localhost. Optionnel : refuser ou demander HTTPS. | Backlog passe finale |
| SEC-2 | Logs | Bas | `corpus.jsonl` contient les transcriptions brutes (`RawSection.Text`) et les prompts Whisper. Déjà gated par consent dialog, mais le wording du dialog pourrait être plus explicite. ([Logging/TelemetryEvent.cs:105-108](../Logging/TelemetryEvent.cs)) | Renforcer la copy du `CorpusConsentDialog` : "This will save your transcripts to disk in plain text." | Backlog passe finale |
| SEC-3 | Filesystem | Bas | Les fichiers WAV du corpus audio ne sont pas wipés quand `RecordAudioCorpus` est désactivé après usage. ([Logging/WavCorpusWriter.cs](../Logging/WavCorpusWriter.cs)) | Action "Clear corpus cache" dans Settings → General. | Backlog passe finale |

### Surfaces auditées propres (RAS)

- **Settings JSON deserialization** : `System.Text.Json` strict, pas
  de gadget chain, migration legacy via `JsonNode.Parse` + mutations
  explicites avant désérialisation typée.
- **Regex** : un seul usage (`CorpusPaths.Slugify`), pattern simple
  sans backtracking, pas de ReDoS.
- **Path traversal** : `Path.Combine` systématique, `Sanitize` sur les
  segments user-controlled.
- **Process.Start** : aucun usage avec arguments user-controlled.
- **DllImport** : DLLs natives chargées depuis le dossier app
  (`PreserveNewest` dans csproj), pas de PATH hijacking possible.
- **Unsafe blocks** : `AllowUnsafeBlocks=true` dans csproj mais aucun
  bloc `unsafe { }` dans le code (réservé pour usage futur).
- **NuGet pins** : versions exactes (`WindowsAppSDK 1.8.260317003`,
  `CommunityToolkit.Mvvm 8.4.2`, etc.), pas de floating ranges.
- **Validation HTTP** : `EnsureSuccessStatusCode` systématique,
  réponses Ollama parsées avec `JsonDocument.Parse`, erreurs extraites
  via `ExtractErrorAsync`.
- **Clipboard** : texte en clair par design (impossible autrement pour
  une app de transcription), accepté.
- **RegisterHotKey** : sur message-only window, pas de surface de
  replay externe.

---

## 3. Audit robustesse étendu — 19 findings

Findings classés par catégorie. La colonne **Statut** indique :
✅ corrigé, 🔧 planifié dans une session, 📋 backlog.

### 3.1 Threading & marshaling UI

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| THR-1 | HudWindow.xaml.cs:342 (EnqueueUI helper) | Haute | `DispatcherQueue.TryEnqueue` ignore silencieusement `false`. Si la queue se ferme pendant un show/hide en vol, l'event est perdu sans trace. | Vérifier le retour, log Warning si false. | ✅ |
| THR-2 | LogWindow.xaml.cs:147,161 | Haute | Même pattern : `TryEnqueue` non vérifié dans `Write()` et `SetRecordingState()`. Events engine perdus en silence. | Idem THR-1. | ✅ |
| THR-3 | App.xaml.cs:200 (`_engine.AudioLevel += _hudWindow.OnAudioLevel`) | Moyenne | Direct subscription sans marshaling sur le thread audio (~20 Hz). Composition est thread-safe par contrat, mais le contrat n'est pas documenté à proximité. | Documenter l'invariant en commentaire. Ajouter try/catch défensif dans `OnAudioLevel`. | 📋 Backlog |
| THR-4 | HudOverlayWindow.xaml.cs:213 | Moyenne | `DispatcherQueue.CreateTimer()` sans fallback si la queue est fermée. | Vérifier le retour, log si null. | 📋 Backlog |

### 3.2 Lifetime & native handles

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| LIF-1 | WhispEngine.cs:718 (`StartRecording`) | Haute | Pas de check `_disposed` dans `StartRecording` / `Transcribe`. Si un hotkey arrive pendant `QuitApp`, accès à un contexte `whisper_free`'d → crash natif fatal. | `if (_disposed) return` au début. | ✅ (StartRecording, StopRecording, Warmup) |
| LIF-2 | App.xaml.cs:308-318 (`QuitApp`) | Moyenne | Ordre de Dispose : tray puis hotkey puis engine. Un Transcribe en vol peut appeler `RaiseStatus` après tray Dispose → NPE dans la callback. | Appeler `_engine.StopRecording()` **avant** tray/hotkey Dispose, attendre le worker. | ✅ (`Engine.Dispose` flippe `_state→Disposed`, set `_stopFlag`, `Thread.Join(30s)` du worker, `_transcribeLock` autour de `whisper_free` — le worker exit cleanly avant la suite du QuitApp) |
| LIF-3 | HudOverlayWindow.xaml.cs:47 | Basse | `_subclassDelegate` field d'instance OK, mais pas d'unsubscribe explicite dans `Closed`. Si la window était reused (pas le cas actuel), leak. | Vérifier ; ajouter `RemoveWindowSubclass` si besoin futur. | 📋 Backlog |

### 3.3 Settings & hot-reload

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| SET-1 | SettingsService.cs:306-309 (Save) | Haute | Atomic write `.tmp` + `Move` mais zéro lock inter-process. Deux instances WhispUI lancées en parallèle (double-clic, login script) → écritures concurrentes, dernier gagne, perte de config silencieuse. | `Mutex(true, "Global\\WhispUISettingsLock")` avant write. | ✅ (mutex local-scope `WhispUI-Settings-Save`, timeout 2s, AbandonedMutex récupéré) |
| SET-2 | SettingsService.cs:44 (`Current` getter) | Moyenne | Hot-reload de settings : composants qui cachent une valeur (HttpClient base URL chez OllamaService — déjà OK via `Func<string>`) doivent re-lire à chaque usage. À auditer profil par profil. | Audit ciblé des consommateurs de settings sensibles. | 📋 Backlog |
| SET-3 | SettingsService.cs:122-189 (migrations) | Basse | Migrations JSON robustes mais zéro validation post-deserialization (ProfileIds resolvable, profiles non-null, etc.). | `AppSettings.Validate()` post-deserialization avec invariants. | 📋 Backlog |

### 3.4 Multi-window & multi-instance

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| MWI-1 | App.xaml.cs:128-135 (tray callbacks) | Haute | Callbacks `_logWindow.ShowAndActivate()` etc. sans null check ni try/catch. Si une window a crashé à la création, NPE silencieuse dans le callback. | Wrapper chaque callback (try/catch + null check). | 📋 Backlog |
| MWI-2 | LogWindow.xaml.cs:131-135 (Closing→Cancel) | Moyenne | `Hide()` non protégé par try/catch. Si une exception lève (Composition API fail), `args.Cancel` reste true mais window peut rester invisible/zombie. | Try/catch + log. | 📋 Backlog |
| MWI-3 | Settings/Llm/LlmModelsSection.xaml.cs:83-114 | Moyenne | Aucune cancellation si l'utilisateur ferme SettingsWindow pendant un `await DeleteModelAsync` en vol. Tâche continue, requête HTTP en vol, callback peut courir après dispose. | CTS lié à l'Unload de la section. | ✅ (CTS section + linked CTS au timeout 30s, recréé au Loaded) |

### 3.5 Async deadlocks & fire-and-forget

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| ASY-1 | HudWindow.xaml.cs:249 (`HideSync`) | Basse | `done.Wait()` sur le thread caller (transcribe). Si le dispatcher est saturé, deadlock théorique. | Timeout : `done.Wait(TimeSpan.FromSeconds(5))` + log + fallback. | ✅ (résolu en bonus avec THR-1 — timeout 5s + early-set sur enqueue raté) |
| ASY-2 | GgufImportDialog.cs:51-59 | Moyenne | `RunImportAsync().ContinueWith(...)` — fire-and-forget avec ContinueWith terminal, mais aucun cancellation token. Si le dialog est fermé pendant l'import, la tâche continue. | CTS lié au dialog, passé à TryImportAsync. | 📋 Backlog |
| ASY-3 | PlaygroundWindow.xaml.cs:413-427 | Basse | Timer ticks (`RmsTimerTick`, `_rebuildDebounce.Tick`) sans try/catch. Exception dans un tick tue silencieusement le timer. | Try/catch dans les ticks. | 📋 Backlog |
| ASY-4 | SettingsService.cs:63 (`_debounceTimer`) | Basse | Timer Infinite, Flush appelé en QuitApp. Si l'app sort pendant un Flush, le timer peut fire après l'exit (benin mais leak technique). | Disposer le timer en QuitApp avant `Environment.Exit(0)`. | 📋 Backlog |

### 3.6 Données externes & parsing fragile

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| PAR-1 | OllamaService.cs:64-67 (ListModels deserialize) | Moyenne | `JsonSerializer.Deserialize<OllamaTagsResponse>` sans try/catch. Si Ollama renvoie HTML 500 ou JSON tronqué, JsonException remonte. Désormais (post-Phase 1) le caller LlmPage l'attrape, mais c'est une garantie qu'on ne veut pas dépendre du caller. | Try/catch dans la méthode, retour vide ou rethrow avec contexte. | ✅ (List + Show, log Warning + preview 200 char + retour vide/null) |
| PAR-2 | OllamaService.cs:250-260 (`ExtractErrorAsync`) | Basse | Swallow toutes les exceptions, retourne null sans log. Zéro diagnostic si une réponse Ollama bizarre arrive. | Log la JsonException avant swallow. | 📋 Backlog |
| PAR-3 | WavCorpusWriter.cs:58-62 | Basse | `Write()` wrapped en try/catch retour null. Pas de log pour les I/O failures (disque plein, permission). | Log la exception (chemin, type) avant swallow. | 📋 Backlog |

### 3.7 Edge cases UX

| ID | Fichier:Ligne | Gravité | Risque | Mitigation | Statut |
|----|---------------|---------|--------|------------|--------|
| UX-1 | WhispEngine.cs:119 (`_segments` list) | Moyenne | Liste unbounded — enregistrement très long (2h+) → OOM possible à la sérialisation finale. | Cap `_segments.Count > MAX_SEGMENTS` avec arrêt ou warning. | 📋 Backlog |
| UX-2 | App.xaml.cs:400-467 (`OnHotkey`) | Haute | Lecture de `_engine.IsRecording` ligne 440 = course avec le worker thread qui le modifie ligne 815. Le gate `if (_isRecording) return;` ligne 720 dans `StartRecording` rattrape le double-call mais le check côté `OnHotkey` est racy. | `Interlocked.CompareExchange` pour le gate, ou supprimer la lecture racy si le gate downstream suffit. | ✅ (state machine 6 états + `RequestToggle` API unifiée) |
| UX-3 | WhispEngine.cs:820-833 (pipeline crash) | Moyenne | Si une exception vient d'une delegate native (callback whisper.cpp `OnNewSegment`, `WhisperLogCallback`, SubclassProc), elle peut être levée hors du try/catch principal du pipeline. | Wrapper les delegates natifs dans leurs propres try/catch. | 📋 Backlog |

---

## 4. Top 5 priorités opérationnelles

À traiter en priorité dans la prochaine session de durcissement (extrait
ordonné de la table 3) :

1. **LIF-1** — `_disposed` check dans `WhispEngine.StartRecording` /
   `Transcribe` → évite crash natif au shutdown.
2. **THR-1, THR-2** — `DispatcherQueue.TryEnqueue` retours non vérifiés
   → évite perte silencieuse d'events UI.
3. **SET-1** — Mutex inter-process sur `SettingsService.Save` → évite
   corruption de config si double instance.
4. **PAR-1** — `JsonSerializer.Deserialize` non gardé dans
   `OllamaService` → évite que Ollama compromis casse le caller.
5. **MWI-3** — CTS lié à l'Unload sur `DeleteModelAsync` → évite
   tâche orpheline après fermeture de SettingsWindow.

---

## 5. Statut implémentation par session

### Session 2026-04-26 (audit initial + Paquets A et B)

**Déjà corrigé (Phases 1-3 du plan initial)** :

- LlmPage.xaml.cs : try/catch sur `OnNavigatedTo`, `ResetAll_Click`,
  lambdas events extraites en méthodes nommées
  (`OnEndpointChanged`, `OnRefreshRequested`, `OnProfilesChanged`),
  try/catch global sur `RefreshOllamaStateAsync`, CTS 5 s sur
  `ListModelsAsync`.
- OllamaService.cs : `CancellationToken ct = default` ajouté à
  `ListModelsAsync`, `ShowModelAsync`, `DeleteModelAsync`.
- LlmModelsSection.xaml.cs : CTS 30 s sur `DeleteModelAsync`.
- GgufImportDialog.cs : `.ContinueWith` sur le fire-and-forget pour
  logger les exceptions échappées.
- App.xaml.cs : `AppDomain.ProcessExit` log handler, try/catch sur
  `_hotkeyManager.Register()` avec UserFeedback Overlay HUD si
  conflit 1409.

**Paquet A — fix bug Ollama "not running" au premier hotkey** ✅ :

- `OllamaService.IsAvailableAsync(maxAttempts, retryDelay)` : retry
  opt-in (default = 1, warmup demande 3) — couvre la race startup.
- `WhispEngine.Warmup` ligne 689 : appelle avec `maxAttempts: 3`.
- `WhispEngine.StartRecording` ligne 743 : live re-probe sync (Task.Run +
  Wait borné 4s) avant d'émettre le warning HUD — corrige le diagnostic
  si Ollama est devenu reachable entre warmup et premier hotkey.
- Reword du message : "Ollama wasn't ready at startup. Start it and try
  again." (au lieu du trompeur "No rewrite this session").

**Paquet B — top 5 robustesse + 1 bonus** ✅ :

- LIF-1 : `_disposed` checks dans `WhispEngine.StartRecording`,
  `StopRecording`, `Warmup` (et thread interne).
- THR-1+2 : extension `DispatcherQueue.TryEnqueueOrLog` créée dans
  `Shell/DispatcherQueueExtensions.cs` (avec garde anti-récursion
  thread-static), 6 sites migrés (LogWindow ×3, HudWindow EnqueueUI,
  HudOverlayManager ×2). `PlaygroundWindow:235` laissé (dev tool).
- ASY-1 (bonus) : `HudWindow.HideSync` durci — TryEnqueueOrLog,
  early-set sur enqueue raté pour éviter Wait infini, timeout 5s sur
  Wait + log Warning si dépassé.
- SET-1 : Mutex local-scope `WhispUI-Settings-Save` autour de
  `SettingsService.Flush`. Timeout 2s, gestion `AbandonedMutexException`,
  log explicite si skip ou recovery.
- PAR-1 : try/catch `JsonException` autour des `JsonSerializer.Deserialize`
  dans `OllamaService.ListModelsAsync` et `ShowModelAsync`. Log Warning
  avec preview 200 char + retour vide/null gracieux.
- MWI-3 : CTS de cycle de vie de section dans `LlmModelsSection`,
  recréé au `Loaded`, cancellé au `Unloaded`. Linked CTS pour combiner
  avec timeout 30s. `OperationCanceledException` sur Unload silencieuse.

### Session 2026-04-27 — Passe robustesse pipeline hotkey

**UX-2 + LIF-2 — state machine 6 états sur la pipeline d'enregistrement** ✅ :

- `volatile bool _isRecording` / `_stopRecording` remplacés par
  `int _state` manipulé via `Interlocked.CompareExchange`. Énum
  `PipelineState { Idle, Starting, Recording, Stopping, Transcribing,
  Disposed }`. Toute transition illégale est silencieusement refusée.
- API publique unifiée `WhispEngine.RequestToggle(manualProfileName,
  shouldPaste, requireProfile)` retourne `ToggleResult { Started,
  Stopped, IgnoredBusy, IgnoredNoProfile, IgnoredDisposed }`. App.OnHotkey
  ne lit plus l'état du moteur pour décider — c'est le moteur qui CAS la
  transition et rapporte le verdict (élimine la course historique).
- `StartRecording` / `StopRecording` publics supprimés. Tout passe par
  `RequestToggle` ; les transitions sont :
  - Hotkey thread : Idle→Starting→Recording (ou rollback Idle).
  - Worker thread : Stopping→Transcribing→Idle dans le `finally`.
  - Cap durée : Recording→Stopping (CAS interne dans `Record()`).
  - `Dispose` : *→Disposed unconditional, gagne sur tout.
- Boucle `Record()` migrée de `while(!_stopRecording)` à
  `while (Volatile.Read(ref _stopFlag) == 0)` ; `_stopFlag` est posé
  par `RequestToggle` (Stop) ou par le cap durée.
- `_transcribeLock` autour de `whisper_full` — sérialise les appels
  natifs sur `_ctx` même si Warmup() reste en parallèle (whisper.cpp
  pas thread-safe sur un même contexte = segfault non rattrapable).
- `_pipelineActive` supprimé : `_state != Idle` le remplace dans
  `UnloadModel`. `RaiseStatus("Ready")` dans `UnloadModel` re-gaté sur
  `_state == Idle` (no clobber pendant un nouveau Start).
- `WorkerRun.finally` est désormais le seul site qui émet "Ready" sur
  le succès path (commentaire-verrou à proximité). `RaiseStatus("Ready")`
  redondant supprimé du early-return audio-vide de `Transcribe`.
- `Dispose` (LIF-2) : flippe `_state→Disposed`, set `_stopFlag`, fait
  `_worker.Join(30_000)` avec log Warning si timeout, puis acquiert
  `_transcribeLock` avant `whisper_free(_ctx)`. Le worker exit cleanly
  avant que le contexte natif ne soit libéré, plus de race
  RaiseStatus-après-tray-Dispose ni de double-free.

Plan détaillé :
[`C:\Users\Louis\.claude\plans\j-ai-un-petit-probl-me-quiet-pebble.md`](../../../C:%5CUsers%5CLouis%5C.claude%5Cplans%5Cj-ai-un-petit-probl-me-quiet-pebble.md).

### Sessions suivantes

Tous les findings marqués 📋 backlog sont à reprendre :
- Soit lors d'une session dédiée robustesse
- Soit au passage si on touche le sous-système concerné

Et la passe sécurité finale (SEC-1, SEC-2, SEC-3 + crypto + threat
modeling formel) est planifiée pour plus tard, post-packaging.
