# Brief de session — Audit robustesse & sécurité

Fiche de session destinée à la session de merge final qui consolidera
plusieurs worktrees parallèles dans `main`. Décrit ce qui a été fait
ici, où regarder pour comprendre, ce qu'il faut surveiller au merge.

---

## Identité de la session

- **Date** : 2026-04-26
- **Worktree** : `D:\worktrees\transcription\clever-heisenberg-da5e9c\`
- **Branche** : `claude/clever-heisenberg-da5e9c`
- **Base de merge** : `main` au commit `bf2be79` (FF mergé en début
  de session, donc la branche est strictement dérivée de cet état)
- **Commits ajoutés par la session** : 1 (`feat(robustness): hardening
  sweep ...`)

## Synthèse en 5 lignes

1. Audit en 3 passes parallèles (crash Ollama, sécurité initiale,
   robustesse étendue) → 22 findings consolidés.
2. Doc référence vivante créée :
   [`reference--audit-robustesse--0.1.md`](reference--audit-robustesse--0.1.md).
3. **Paquet A** (fix bug "Ollama not running" au premier hotkey après
   boot) implémenté.
4. **Paquet B** (top 5 robustesse + 1 bonus ASY-1) implémenté.
5. 14 findings restent en backlog 📋 dans la doc référence, à reprendre
   au fil de l'eau ou en session dédiée.

## Changements par groupe logique

### Paquet A — Fix "Ollama not running" au premier hotkey

Cause exacte : le warmup engine (lancé en background à `App.OnLaunched`)
ping Ollama via `IsAvailableAsync` avec un seul essai timeout 3 s. Si
le service Ollama Windows n'a pas encore fini d'écouter sur 11434 à
T+~5 s du boot WhispUI, `_ollamaWarmupOk` est posé à 0. Au premier
hotkey, le bloc `Interlocked.Exchange(ref _warmupFlagsConsumed, 1)`
émet le warning HUD trompeur "No rewrite this session" — alors que les
rewrites suivants marchent typiquement.

Mitigation en 3 niveaux indépendants :

- `OllamaService.IsAvailableAsync(maxAttempts, retryDelay)` : retry
  opt-in, default 1 (rapide pour Settings UI), warmup demande 3.
- `WhispEngine.Warmup` ligne 689 : appelle avec `maxAttempts: 3` —
  couvre la race startup classique.
- `WhispEngine.StartRecording` ligne 743 : live re-probe (Task.Run +
  Wait borné 4 s) avant d'émettre le warning au premier hotkey,
  corrige le diagnostic si Ollama est devenu reachable entre warmup
  et hotkey.
- Reword du message HUD : "Ollama wasn't ready at startup. Start it
  and try again." (au lieu du faux "No rewrite this session").

### Paquet B — Top 5 robustesse + 1 bonus

Numérotation (LIF-1, THR-1, SET-1, etc.) référence la table de findings
dans `reference--audit-robustesse--0.1.md`.

- **LIF-1** — `_disposed` checks ajoutés en haut de
  `WhispEngine.StartRecording`, `StopRecording`, `Warmup` (avec re-check
  inside du thread). Évite le crash natif fatal si un hotkey arrive
  pendant `QuitApp` et accède à un contexte `whisper_free`'d.
- **THR-1 + THR-2** — nouveau fichier
  `Shell/DispatcherQueueExtensions.cs` exposant `TryEnqueueOrLog` avec
  garde anti-récursion thread-static (sinon : LogWindow.TryEnqueue
  fail → log → TryEnqueue fail → boucle). 6 sites migrés :
  - `LogWindow.xaml.cs` lignes 147, 153, 161 (Write, Clear,
    SetRecordingState)
  - `HudWindow.xaml.cs:342` (`EnqueueUI` helper)
  - `HudOverlayManager.cs:54, 102` (Enqueue, OnMainHudVisibilityChanged)
  - `HudWindow.xaml.cs:245` (HideSync) — voir ASY-1
  - Site `PlaygroundWindow.xaml.cs:235` non migré (dev tool, criticité
    basse, à reprendre si on touche le sous-système).
- **ASY-1 (bonus)** — `HudWindow.HideSync` durci en même temps que
  THR-1 : early-set sur enqueue raté pour éviter le `Wait` infini,
  timeout 5 s + log Warning si dépassé. Risque résiduel documenté
  dans le code (rendezvous Hide raté → race paste théorique, accepté
  en cas pathologique).
- **SET-1** — Mutex local-scope `WhispUI-Settings-Save` autour de
  `SettingsService.Flush`. Timeout `WaitOne` 2 s, gestion
  `AbandonedMutexException` (recovery propre si l'autre instance
  crashe en tenant le mutex), log explicite si skip ou recovery.
- **PAR-1** — try/catch `JsonException` dans
  `OllamaService.ListModelsAsync` et `ShowModelAsync`. Log Warning
  avec preview 200 chars + retour vide/null gracieux. Rend l'app
  indépendante du caller pour ne pas crasher si Ollama renvoie HTML
  d'erreur ou JSON tronqué.
- **MWI-3** — CTS de cycle de vie dans `LlmModelsSection`, recréé au
  `Loaded`, cancellé au `Unloaded`. `CancellationTokenSource.CreateLinkedTokenSource`
  combine avec le timeout 30 s sur `DeleteModelAsync`. Cancellation
  sur Unload silencieuse via `OperationCanceledException` filtré.

## Liste exhaustive des fichiers du commit

### Modifiés (10)

| Fichier | Rôle dans le commit |
|---------|---------------------|
| `src/WhispUI/App.xaml.cs` | (déjà fait avant cette session B) `AppDomain.ProcessExit` log handler + try/catch sur `_hotkeyManager.Register()` avec UserFeedback Overlay HUD si conflit 1409 |
| `src/WhispUI/WhispEngine.cs` | `_disposed` guards (StartRecording, StopRecording, Warmup) + warmup `maxAttempts: 3` + live re-probe + reword warning |
| `src/WhispUI/HudWindow.xaml.cs` | `EnqueueUI` migré vers TryEnqueueOrLog + `HideSync` durci (early-set + timeout 5 s) |
| `src/WhispUI/HudOverlayManager.cs` | 2 sites TryEnqueue migrés vers TryEnqueueOrLog + import `WhispUI.Shell` |
| `src/WhispUI/LogWindow.xaml.cs` | 3 sites TryEnqueue migrés vers TryEnqueueOrLog |
| `src/WhispUI/Llm/OllamaService.cs` | `IsAvailableAsync` retry opt-in + `CancellationToken` sur List/Show/Delete + try/catch JsonException sur Deserialize |
| `src/WhispUI/Settings/LlmPage.xaml.cs` | (déjà fait pré-session B) lambdas events extraites en méthodes nommées + try/catch sur OnNavigatedTo, ResetAll_Click, RefreshOllamaStateAsync + CTS 5 s sur ListModelsAsync |
| `src/WhispUI/Settings/Llm/LlmModelsSection.xaml.cs` | CTS section Loaded/Unloaded + linked CTS combinant timeout 30 s sur DeleteModelAsync + filtrage OperationCanceledException |
| `src/WhispUI/Settings/Llm/GgufImport/GgufImportDialog.cs` | (déjà fait pré-session B) `.ContinueWith` sur fire-and-forget `RunImportAsync` |
| `src/WhispUI/Settings/SettingsService.cs` | Mutex local-scope `WhispUI-Settings-Save` sur Flush, timeout 2 s, AbandonedMutex recovery |

### Nouveaux (3, dont ce brief)

| Fichier | Rôle |
|---------|------|
| `src/WhispUI/Shell/DispatcherQueueExtensions.cs` | Helper `TryEnqueueOrLog` avec garde anti-récursion thread-static |
| `src/WhispUI/docs/reference--audit-robustesse--0.1.md` | Doc référence vivante du chantier — 22 findings classés par catégorie + criticité, statut mis à jour à chaque session |
| `src/WhispUI/docs/brief--audit-robustesse--0.1.md` | Le présent brief |

**Total : 13 fichiers dans le commit.**

## Risques de conflit avec d'autres worktrees

Au moment du merge final, les fichiers suivants sont les plus
susceptibles d'avoir été touchés ailleurs en parallèle :

- **`App.xaml.cs`** — fichier carrefour (lifecycle, handlers globaux,
  startup). Tout chantier qui ajoute une window, un handler, ou
  modifie l'ordre OnLaunched touche ici. **Conflit probable.**
- **`WhispEngine.cs`** — gros fichier (~2200 lignes) modifié par
  plein de chantiers (audio, transcription, paste, threading). Mes
  ajouts sont localisés (haut de Warmup, StartRecording, StopRecording
  + bloc warmup-flags ligne 743). Conflit textuel possible mais sans
  enchevêtrement logique.
- **`Llm/OllamaService.cs`** — touché par d'éventuels chantiers
  rewrite/profiles. La signature de `IsAvailableAsync` a changé
  (paramètre opt-in `maxAttempts`) — **breaking si un autre worktree
  a ajouté un appel à cette méthode**. Backward-compatible côté
  appelant grâce à `= default` mais à vérifier.
- **`Llm/LlmService.cs`** — non touché ici (le service rewrite est
  déjà excellent), donc zéro conflit côté nous.
- **`Settings/SettingsService.cs`** — `Flush` a une nouvelle structure
  (try/finally autour du Mutex). Si un autre worktree a touché
  `Flush`, conflit textuel certain à arbitrer manuellement.

Les autres fichiers (HudWindow, LogWindow, HudOverlayManager,
LlmPage, LlmModelsSection, GgufImportDialog) sont moins probables
en zone de chantier parallèle.

**Stratégie recommandée au merge** : intégrer ce worktree **après**
les chantiers UI/HUD purement visuels (qui touchent typiquement
HudWindow et compagnie sur des aspects esthétiques sans toucher
EnqueueUI / HideSync), et **avant** ou **après** les chantiers
transcription pure selon leur taille. Si conflit majeur sur
WhispEngine, garder ce brief comme référence pour rétablir
manuellement les `_disposed` guards et le bloc warmup-flag.

## Tests runtime à passer post-merge

(Activer `applicationLogToDisk` dans Settings → General avant les
tests pour capturer les nouveaux logs Warning.)

1. **Bug Ollama au boot** — démarre PC, lance WhispUI tout de suite,
   attends ~5-10 s, déclenche hotkey rewrite. Attendu : pas de
   warning HUD "Ollama wasn't ready", rewrite OK. Si Ollama démarre
   vraiment lentement, le live re-probe rattrape.
2. **Ollama vraiment éteint** — `ollama stop`, lance WhispUI,
   hotkey. Attendu : warning HUD avec le nouveau message, recording
   continue, raw transcript collé.
3. **Quit pendant recording** — pendant un recording actif, clic
   tray Quit. Attendu : pas de crash natif (les `_disposed` guards
   court-circuitent les hotkeys post-Quit).
4. **Two instances** — double-clic rapide sur WhispUI.exe (deux
   instances). Modifie un setting dans l'une. Attendu : log Warning
   "another WhispUI instance holds the settings mutex" si conflit,
   pas de corruption silencieuse de `settings.json`.
5. **Ollama renvoie HTML** — temporairement, configure l'endpoint
   Ollama vers un serveur web banal qui répond HTML. Refresh la
   page Settings → Rewriting. Attendu : log Warning "ListModels:
   invalid JSON from Ollama", page reste utilisable.
6. **Close Settings pendant Delete** — clique Remove sur un modèle
   gros, ferme SettingsWindow avant que la suppression finisse.
   Attendu : pas de log d'erreur, suppression s'arrête proprement
   (CTS cancelled).
7. **Inspection logs** — après les tests, lire
   `benchmark/telemetry/app.jsonl`. Doit contenir les nouveaux
   patterns `[INIT] warmup flag | ...`, `[HUD] DispatcherQueue.TryEnqueue
   rejected (...)` (rare, signe que la dispatcher se ferme), `[SETTINGS]
   save skipped: ...` (si test 4 actif), `[LLM] ListModels: invalid
   JSON ...` (si test 5).

## Backlog hérité

14 findings restent en backlog 📋 dans
[`reference--audit-robustesse--0.1.md`](reference--audit-robustesse--0.1.md).
Aucun n'est urgent. À reprendre :

- soit dans une session dédiée robustesse plus tard
- soit au passage quand on touche le sous-système concerné

Catégories restantes : THR-3, THR-4, LIF-2, LIF-3, SET-2, SET-3,
MWI-1, MWI-2, ASY-2, ASY-3, ASY-4, PAR-2, PAR-3, UX-1, UX-2, UX-3.

3 findings sécurité (SEC-1, SEC-2, SEC-3) sont à traiter dans la
passe sécurité finale planifiée par Louis post-packaging.

## Prompt pour la session de merge final

Bloc à coller dans une session Claude Code dédiée au merge des
worktrees parallèles vers `main`. Self-contained.

> Tu intègres dans `main` le worktree
> `D:\worktrees\transcription\clever-heisenberg-da5e9c\` (branche
> `claude/clever-heisenberg-da5e9c`), un commit unique
> `feat(robustness): hardening sweep ...` qui durcit la robustesse de
> WhispUI sur 6 axes : fix bug Ollama "not running" au premier hotkey
> après boot + 5 défenses (lifecycle guards, dispatcher safety,
> settings mutex, JSON guards, section CTS) + 1 bonus (HudWindow
> HideSync deadlock guard).
>
> **Lecture obligatoire avant merge** :
>
> - `src/WhispUI/docs/brief--audit-robustesse--0.1.md` — fiche de
>   session avec liste des fichiers touchés, risques de conflit
>   identifiés, et tests runtime à passer.
> - `src/WhispUI/docs/reference--audit-robustesse--0.1.md` — référence
>   consolidée des 22 findings, statut implémentation, backlog restant.
>
> **Stratégie de merge** :
>
> 1. Vérifier que `Llm/OllamaService.cs::IsAvailableAsync` n'a pas un
>    appelant ajouté ailleurs sans le paramètre `maxAttempts` (la
>    signature a changé, default `1` rétro-compatible mais à confirmer
>    que le call site désiré reçoit bien le bon comportement).
> 2. Si conflit sur `App.xaml.cs` ou `WhispEngine.cs`, consulter le
>    brief pour comprendre la nature exacte des ajouts (les
>    `_disposed` guards et le live re-probe Ollama doivent
>    impérativement survivre au merge).
> 3. Si conflit sur `Settings/SettingsService.cs::Flush`, arbitrer
>    manuellement — la structure try/finally autour du Mutex
>    `WhispUI-Settings-Save` est nouvelle et critique.
> 4. Le nouveau fichier `Shell/DispatcherQueueExtensions.cs` ne devrait
>    pas conflicter — vérifier juste qu'aucun autre worktree n'a créé
>    un helper équivalent (auquel cas, harmoniser).
>
> **Validation post-merge** :
>
> Activer `applicationLogToDisk: true` dans `config/settings.json`
> avant les tests, puis exécuter les 7 scénarios de la section "Tests
> runtime à passer post-merge" du brief. Examiner `app.jsonl` pour
> confirmer la présence des nouveaux logs.
>
> **Backlog à propager** : la section "Backlog hérité" du brief liste
> 14 findings non traités. Les laisser dans la doc référence (qui est
> faite pour ça), aucune action de merge nécessaire.
>
> **Ne pas builder** côté Claude Code — règle CLAUDE.md WhispUI :
> Louis builde via `scripts/build-run.ps1`.

## Référence rapide

- Doc consolidée : [`reference--audit-robustesse--0.1.md`](reference--audit-robustesse--0.1.md)
- Plan de session original : `C:\Users\Louis\.claude\plans\dans-cette-nouvelle-conversation-logical-snail.md`
- Helper créé : `src/WhispUI/Shell/DispatcherQueueExtensions.cs`
