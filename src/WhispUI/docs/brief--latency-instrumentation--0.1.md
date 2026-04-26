# Brief — feat/latency-instrumentation

Document de passation pour la session qui mergera cette branche dans `main`.
Lecture directe sans aller-retour. Infos denses, risques explicites.

---

## 1. Identité

- **Branche** : `feat/latency-instrumentation`
- **Base** : `main` à `bf2be79` (fast-forward effectué en début de session)
- **État** : working tree avec diff non commité sur 6 fichiers (+ ce doc), `+406 / −55`. Aucun commit créé.
- **Origine** : branchée depuis main propre, aucun merge intermédiaire.

---

## 2. Résumé exécutif

Cette branche **n'ajoute aucun comportement runtime**. Elle étend la
télémétrie pour cartographier la latence du pipeline transcription stage
par stage, refactore minimalement `LlmService.Rewrite` pour exposer les
métriques Ollama au caller, et fixe deux règles UX/doc dans `CLAUDE.md`.
Tout est additif et observable, rien n'est conditionnel ni gated par
flag.

But en aval : permettre l'analyse statistique fine des latences de
chaque sous-étape (load modèle, capture, drain, VAD inference vs wall,
init Whisper, décodage, eval LLM…) avant d'attaquer des optimisations
ciblées. Sans cette branche, l'analyse stat agrège trop d'étapes sous
des étiquettes opaques (cf. anomalie VAD 5 %/audio constant sur
700 runs).

---

## 3. Inventaire fichiers

| Fichier | +/− | Type de change |
|---|---|---|
| `src/WhispUI/WhispEngine.cs` | +172 / −15 | 4 fields timer + reset + start/stop sites + 7 vars locales + log Verbose étendu + named-arg payload |
| `src/WhispUI/Llm/LlmService.cs` | +63 / −7 | refacto signature : `Rewrite` retourne `RewriteResult` (struct readonly) + helper `ExtractMetrics` |
| `src/WhispUI/Logging/TelemetryEvent.cs` | +68 / −18 | 7 nouveaux champs `LatencyPayload` + bloc doc inline complet |
| `src/WhispUI/Logging/TelemetryService.cs` | +14 / −5 | rendu compact `[LATENCY]` LogWindow étendu (audio/hotkey/vad/whisper/llm/outcome) |
| `src/WhispUI/CLAUDE.md` | +88 / −0 | sections "Règles UX non négociables", "Télémétrie — source unique", checklist nomenclature |
| `src/WhispUI/docs/reference--logging-inventory--0.1.md` | +56 / −10 | sections 3/7/10 mises à jour, gabarit standard `DONE timings` étendu, nouvelle ligne `DONE llm_metrics` |

---

## 4. Breaking changes — API et schéma

### 4.1 `LlmService.Rewrite` — signature changée

**Avant** : `public string? Rewrite(...)`

**Après** : `public RewriteResult Rewrite(...)` avec

```csharp
internal readonly record struct RewriteResult(
    string?      Text,
    long         TotalMs,
    long         OllamaLoadMs,
    long         PromptEvalMs,
    long         EvalMs,
    int          PromptTokens,
    int          EvalTokens);
```

**Caller à jour dans cette branche** : `WhispEngine.cs` ligne ~1892 (un
seul appelant). `RewriteResult` est `internal`, visibilité limitée à
l'assembly WhispUI — aucun consommateur externe.

**Vérif merge** : `grep "_llm.Rewrite\|llm.Rewrite\|LlmService"` dans
toute branche entrante. Si un autre caller existe (improbable), il
faudra adopter `result.Text` à la place du `string?` retourné, et lire
les 6 champs metrics si besoin.

### 4.2 `LatencyPayload` — 11 nouveaux champs au total

**Cette passe** (snake_case, JSONL, ordre du record) :

```
record_drain_ms       long
vad_inference_ms      long
ollama_load_ms        long
llm_prompt_eval_ms    long
llm_eval_ms           long
llm_prompt_tokens     int
llm_eval_tokens       int
```

**Passe précédente du même chantier** (présents en base de la branche) :

```
model_load_ms         long
hotkey_to_capture_ms  long
stop_to_pipeline_ms   long
whisper_init_ms       long
```

**Total payload après merge** : 24 champs vs 13 pré-chantier. Le
constructor utilise des **named arguments** dans `WhispEngine` — toute
autre branche qui construit un `LatencyPayload` doit faire pareil.

### 4.3 Sémantique `whisper_ms` — calcul changé

**Avant** : `whisperMs = transcribeMsTotal - vadMs`
**Après** : `whisperMs = max(0, transcribeMsTotal - vadMs - whisperInitMs)`

Conséquence sur les analyses longitudinales : `whisper_ms` post-branche
est mécaniquement **plus petit** que pré-branche pour des audios
identiques. Le delta migre dans `whisper_init_ms`. Garder en tête lors
d'agrégations qui mélangent records pré et post merge.

---

## 5. Compatibilité JSONL `latency.jsonl`

- **Tous les nouveaux champs sont additifs** : un consumer JSONL qui ne
  connaît que les anciens champs continue de fonctionner. Le champ
  `whisper_ms` n'a pas changé de nom ni de type, juste la sémantique du
  calcul.
- **Aucune migration de fichier** : `JsonlFileSink` continue d'écrire
  en append dans `<storage>/latency.jsonl`, anciennes lignes restent
  valides.
- **Benchmark Python** (`benchmark/`) : aucun script ne lit les
  champs `*_ms` du payload aujourd'hui (vérifié par grep). Pas de
  rebranchement nécessaire.

---

## 6. Règles UX/doc figées

Ajoutées dans `src/WhispUI/CLAUDE.md`. Toute autre branche qui touche au
clipboard ou au streaming LLM **doit** respecter :

1. **Clipboard 2 états max par transcription** — raw Whisper, puis
   rewrite. Jamais d'accumulation token-par-token. Si streaming LLM un
   jour : remplacer en place ou supprimer + ajouter, granularité phrase
   ou intervalle régulier, pas mot par mot.
2. **Pré-charge VAD au hotkey** — idée notée comme piste plausible
   (anomalie VAD 5 %/audio constant), pas implémentée. Toute branche
   qui implémente devra mesurer avant/après avec l'instrumentation
   présente.

Et la **checklist télémétrie** posée pour toute future addition de log :

1. Respecter la nomenclature de
   `reference--logging-inventory--0.1.md` (suffixe
   `_ms`/`_sec`/`_tok`/`_chars`, snake_case, ms entier).
2. Étendre le `record` payload approprié.
3. Mettre à jour le `text` précompilé dans `TelemetryService.<Kind>()`.
4. Passer `[à instrumenter]` → `[logué]` dans l'inventory.
5. Vérifier les consommateurs.

---

## 7. Risques de conflit avec d'autres branches

| Fichier | Risque | Pourquoi |
|---|---|---|
| `WhispEngine.cs` | **élevé** | fichier de 2 200 lignes, central, touché ici à 6 endroits (fields ~244, hook log VAD ~345, LoadModel ~590, StartRecording ~757, StopRecording ~899, Record ~1170 et ~1247, Transcribe construction payload ~1958). |
| `Logging/TelemetryEvent.cs` | **moyen** | si une autre branche ajoute aussi des champs au `LatencyPayload`, conflit textuel quasi certain. Résolution : merger les deux sets de champs, garder le bloc commentaire en tête, vérifier le named-arg constructor côté WhispEngine. |
| `Logging/TelemetryService.cs` | **moyen** | méthode `Latency()` touchée pour le rendu compact. Si une autre branche modifie aussi le format de la ligne `[LATENCY]`, merge manuel. |
| `Llm/LlmService.cs` | **moyen** | refacto signature `Rewrite`. Toute branche qui modifie cette méthode entrera en conflit. |
| `CLAUDE.md` (WhispUI) | **moyen** | +88 lignes en bloc avant la section "Journal d'implémentation". Conflit textuel possible si une autre branche modifie la même zone. |
| `docs/reference--logging-inventory--0.1.md` | **moyen** | sections 3, 7, 10 modifiées. |
| `Llm/LlmService.cs` (struct `RewriteResult`) | faible | la struct est nouvelle, pas de chevauchement à attendre. |
| `Logging/JsonlFileSink.cs` | nul | pas touché — sérialise via réflexion, prend automatiquement les nouveaux champs. |

### Ordre de merge recommandé

- **Si plusieurs branches touchent `LatencyPayload`** : merger
  celle-ci en **premier**, parce qu'elle pose les fondations
  télémétrie (struct `RewriteResult`, doc inventory, règles
  CLAUDE.md). Les autres branches latence-related s'aligneront sur
  la nomenclature posée ici.
- **Si une branche refactore `WhispEngine.cs` en profondeur**
  (refonte pipeline, threading, etc.) : merger l'autre **avant**, puis
  cette branche se rebase ou se résout textuellement. Le code latence
  est local à des sites bien identifiés (entrée/sortie de méthode,
  démarrage Stopwatch), facile à reposer après refacto.
- **Si une branche change `LlmService.Rewrite`** (streaming, prompts
  dynamiques, etc.) : merger les deux ensemble, parce que la signature
  change ici et la branche en concurrence va probablement aussi vouloir
  toucher la signature. Décider au cas par cas.

---

## 8. Build et runtime — état exact

### Build

- **Pas testé**. Rule du repo : Claude ne lance jamais `build-run.ps1`
  ni `MSBuild`. Le build a toujours été fait par Louis.
- Risque résiduel évalué bas mais non nul :
  - `RewriteResult` est `internal` mais dans le même assembly que
    `WhispEngine` qui le consomme — visibilité OK.
  - `default(RewriteResult)` retourne `Text=null`, le caller checke
    déjà `IsNullOrWhiteSpace(llmResult.Text)` avant d'utiliser le
    rewrite — branche profile-vide gérée.
  - Named arguments dans `new LatencyPayload(...)` — si un nom de champ
    est mal écrit, erreur de compil claire (CS1739 / CS7036). Pas de
    risque silencieux.
  - Tous les `Stopwatch?` sont nullable —
    `?.ElapsedMilliseconds ?? 0` au lieu de déréférencement direct.
- **Recommandation** : build immédiatement après merge. Si un problème
  survient, c'est sur la signature `RewriteResult` ou un named-arg du
  payload — facilement diagnostiquable.

### Runtime

- **Pas testé non plus**. Premiers runs validation post-merge :
  1. Premier hotkey post-démarrage : `model_load_ms` non-nul
     (~1-3 s sur Vulkan), `hotkey_to_capture_ms ≈ model_load_ms + ~50 ms`.
  2. Hotkeys suivants (modèle warm) : `model_load_ms = 0`,
     `hotkey_to_capture_ms < 100 ms`.
  3. Run avec profil rewrite : `ollama_load_ms` non-nul au premier
     appel post-cold-Ollama, puis 0 sur la même session
     (`keep_alive 5 min`). `llm_eval_tokens / llm_eval_ms` doit donner
     un tok/s plausible (5–50 tok/s selon hardware).
  4. `vad_inference_ms` : valeur < `vad_ms`, l'écart révèle l'overhead
     non-inférence (parsing logs, alloc).
  5. `record_drain_ms` ≥ 100 ms (`Thread.Sleep` guard hardcodé).

### Sinks

- `JsonlFileSink` : sérialise via `JsonSerializer` qui prend
  automatiquement les nouveaux champs `[JsonPropertyName]`. Aucune
  modif de plomberie.
- `LogWindow` : reçoit le `text` précompilé étendu via
  `TelemetryService.Latency()`. Format :
  `[LATENCY] audio=Xs hotkey=Xms vad=Xms whisper=Xms llm=Xms outcome=X`.

---

## 9. Plan de commit recommandé

Quand le merger sera prêt à committer cette branche, deux options :

**Option A — un seul commit cohérent** (recommandé) :

```
feat(telemetry): per-stage latency instrumentation + Ollama metrics

- LatencyPayload: +11 fields (model_load, hotkey_to_capture, record_drain,
  stop_to_pipeline, whisper_init, vad_inference, ollama_load, prompt_eval,
  eval, prompt_tokens, eval_tokens)
- LlmService.Rewrite returns RewriteResult struct with Ollama metrics
- WhispEngine: 4 stage timers (hotkey, record_drain, stop_to_pipeline,
  whisper_init) + recompute whisper_ms = total - vad - init
- TelemetryService: extend LATENCY compact line with hotkey/vad/whisper/llm
- CLAUDE.md: clipboard 2-state rule + telemetry centralization invariant +
  nomenclature checklist
- inventory doc: sections 3/7/10 updated, DONE timings + llm_metrics gabarit
```

**Option B — deux commits** :

1. `chore(telemetry): centralize hub + nomenclature doc` (CLAUDE.md,
   inventory, TelemetryService rendu)
2. `feat(telemetry): per-stage latency fields + LlmService refactor`
   (TelemetryEvent, LlmService, WhispEngine)

Préférence : **Option A**, parce que les 6 fichiers forment un
changement cohérent et que séparer casse la traçabilité (la doc
référence les nouveaux champs définis dans le commit suivant).

---

## 10. Out of scope — pour info

- **StartupPayload structuré** : `App.OnLaunched` produit déjà des
  `Milestone()` (Verbose log) mais pas de payload JSONL structuré.
  Hors pipeline transcription.
- **WarmupPayload distinct** : le warmup est gated par `_isWarmup` qui
  masque toute la télémétrie. Pour observer le coût pur du warmup
  load, il faudrait soit un nouveau Kind soit un flag `is_warmup` sur
  `LatencyPayload`.
- **Désagrégation `paste_ms`** (HideSync / UIA / SendInput) : déjà à
  7 ms moyen, pas urgent.
- **Désagrégation `clipboard_ms`** : déjà à 1 ms moyen.
- **Streaming LLM** : pas implémenté. Si une autre branche le fait,
  **respecter la règle clipboard 2-états** posée dans `CLAUDE.md`.
- **Pré-charge VAD au hotkey** : idée notée, pas implémentée. À
  tenter avec mesure avant/après une fois cette instrumentation en
  place.

---

## Annexe — diagnostic latence ayant motivé la passe

Données : 727 records (`benchmark/telemetry/latency.jsonl` 85 +
`legacy/*.csv` 642), du 15 au 26 avril 2026.

### Breakdown global avec LLM (n=349, audio moyen 138 s, total moyen 22.7 s)

```
LLM     ████████████████████████████████████████████████  10.7 s  47.2 %
VAD     █████████████████████████████                      6.5 s  28.8 %
Whisper ████████████████████████                           5.4 s  24.0 %
Paste   ▏                                                  0.007 s
Clip    ▏                                                  0.001 s
```

### Anomalie VAD — scaling linéaire confirmé

```
audio    0- 10s (n=103): vad=  305 ms  vad/audio = 5.1 %
audio   10- 30s (n=194): vad=  988 ms  vad/audio = 5.2 %
audio   30- 60s (n=138): vad= 2211 ms  vad/audio = 5.1 %
audio   60-120s (n=117): vad= 4388 ms  vad/audio = 5.2 %
audio  120-300s (n=100): vad= 9313 ms  vad/audio = 4.9 %
audio  300-600s (n= 26): vad=19329 ms  vad/audio = 4.6 %
```

5 % du temps audio constant sur 700+ runs, GPU/CPU ne change rien
(vérifié runtime). L'instrumentation `vad_inference_ms` ajoutée par
cette branche permettra de séparer l'inférence pure du wall, première
étape pour comprendre.

### Scaling LLM par taille de texte

```
chars    0- 500  (n= 83): llm=  3 428 ms   ms/char = 14.8
chars  500-1000  (n= 80): llm=  6 080 ms   ms/char =  7.9
chars 1000-1500  (n= 62): llm=  8 704 ms   ms/char =  7.0
chars 1500-2000  (n= 40): llm= 11 226 ms   ms/char =  6.5
chars 2000-3000  (n= 40): llm= 15 950 ms   ms/char =  6.6
chars 3000- ∞    (n= 44): llm= 30 395 ms   ms/char =  5.7
```

Coût fixe ~1.5–2 s + ~6–7 ms/char en régime asymptotique. Avec les
nouveaux champs `ollama_load_ms`, `llm_prompt_eval_ms`, `llm_eval_ms`,
on peut maintenant distinguer le coût fixe (load + prompt eval) du
coût variable (eval).

### Comparaison époques — Whisper s'est amélioré récemment

```
csv-0421 (n=307, audio 137 s): vad=6457  whisper=5547  llm=10653
jsonl    (n= 33, audio 126 s): vad=6377  whisper=3927  llm=9654
```

Whisper post-refonte monobloc ~−29 % sur des audios comparables. VAD
n'a quasi pas bougé, anomalie persiste.

### Pistes d'optim hiérarchisées

1. Streaming LLM (gros levier perçu, sous contrainte clipboard
   2-états).
2. Enquête VAD (5 %/audio constant — pré-charge au hotkey, profiling
   whisper.cpp).
3. Beam size + quantization Whisper (autoresearch friendly).
4. Modèle LLM plus petit pour profil Nettoyage.

Toutes ces pistes nécessitent la télémétrie posée par cette branche
pour être évaluées rigoureusement.
