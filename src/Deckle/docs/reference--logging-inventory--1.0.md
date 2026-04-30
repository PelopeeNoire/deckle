# Inventaire logging — Deckle

## Contexte

Référence unique pour décider **quoi logger, à quel niveau, avec quel format**
à chaque étape du pipeline Deckle. Sert deux objectifs :

- Normaliser les logs existants (disparités : certaines zones très bavardes,
  d'autres muettes).
- Câbler les bons signaux UX (`UserFeedback`) pour prévenir l'utilisateur
  des problèmes rattrapables sans qu'il ait à ouvrir la LogWindow.

Document vivant. Version `1.0` : inventaire normatif appliqué au code,
les sources et payloads décrits ci-dessous reflètent le runtime courant.
Les décisions prises ici cadrent toute future étape mesurée — étendre un
payload existant ou en ajouter un suit la procédure section "Quand on
ajoute une étape mesurée" ci-dessous.

---

## Vocabulaire de mesures

Chaque mesure a un format canonique. Toute apparition dans un log doit suivre
la même unité, la même précision, le même suffixe, pour qu'un humain qui
grep `rms=` trouve exactement la même chose partout.

### Temps

| Mesure | Unité | Précision | Exemple | Source |
|---|---|---|---|---|
| Durée courte | `ms` | entier | `load_ms=420` | Stopwatch |
| Durée longue | `s` | 1 décimale | `audio_sec=12.3` | calcul échantillons / 16000 |
| Timing segment | `s` | 1 décimale | `t0=1.2 t1=3.4 dur=2.2` | whisper callback |

### Audio

| Mesure | Unité | Précision | Exemple | Notes |
|---|---|---|---|---|
| RMS linéaire | `[0, 1]` | 4 décimales | `rms=0.0123` | sqrt(Σv²/n), v = pcm16/32768 |
| Niveau dBFS | `dBFS` | 1 décimale | `dbfs=-38.2` | `20 * log10(rms)` |
| Fréquence | `kHz` | entier | `16 kHz` | toujours 16 dans Deckle |
| Canaux | — | — | `mono` | toujours mono |
| Échantillons | `samples` | entier | `samples=204800` | int PCM16 compte |
| Taille buffer | `bytes` | entier | `bytes=16000` | octets bruts |

### Texte

| Mesure | Unité | Exemple |
|---|---|---|
| Longueur caractères | `chars` | `text_chars=142` |
| Longueur mots | `words` | `text_words=28` |
| Longueur tokens | `tok` | `prompt_tok=512` |

### Compute

| Mesure | Unité | Précision | Exemple |
|---|---|---|---|
| Nombre de segments | — | entier | `n_seg=7` |
| Tokens par seconde | `tok/s` | 1 décimale | `tok_s=42.3` |
| Pourcentage | `%` | 1 décimale | `reduction=62.4%` |
| Confiance | `[0, 1]` | 2 décimales | `p̄=0.87 min=0.42` |
| Probabilité | `%` | entier | `nsp=12%` |

### Retours d'appel

| Mesure | Format | Exemple |
|---|---|---|
| Code natif | `name={int}` | `result=0`, `mmsys=4` |
| Outcome enum | `outcome={value}` | `outcome=Pasted` |
| Pointeur natif | `hex` | `ctx=0x7ff8a3c01400` |

---

## Conventions

### Niveaux de sévérité

| Niveau | Usage | Visibilité dans LogWindow |
|---|---|---|
| `Verbose` | détail technique (`k=v`), plomberie bas niveau, per-buffer, confiance segment, metrics LLM, timings détaillés | All uniquement |
| `Info` | grand jalon du pipeline en phrase courte Capital (Loading model, Recording start, Transcribing, Rewriting, Copied to clipboard, Pasted) | Activity + All |
| `Success` | grand jalon vérifié et terminé (Model loaded, Warmup complete, Transcription complete, Rewrite complete, Done) | Activity + All |
| `Warning` | problème non-fatal, dégradation, fallback | Activity + Alerts + All |
| `Error` | défaillance rattrapable ou non | Activity + Alerts + All |
| `Narrative` | phrase UX en anglais, explique **quoi + pourquoi** ce que fait l'app à l'étape en cours | Narrative + All |

### Filtre SelectorBar — progressif

| Vue | Contient |
|---|---|
| `Narrative` | Narrative uniquement — narration UX de bout en bout |
| `All` | tout (Verbose, Info, Success, Warning, Error, Narrative, Latency, Corpus) |
| `Activity` | Info + Success + Warning + Error — grandes étapes + problèmes, aucun détail technique |
| `Alerts` | Warning + Error — seulement les problèmes |

Latency et Corpus sont des lignes structurelles (JSONL) et n'apparaissent
qu'en `All` — jamais dans Activity. Elles ne sont pas des étapes au sens
du pipeline, ce sont des exports.

**Règle de sévérité** — quand on hésite :

- Est-ce que l'utilisateur doit agir ? → `Error` + `UserFeedback` Critical
- Est-ce que le résultat est dégradé mais exploitable ? → `Warning` + `UserFeedback` Warning
- Est-ce que tout va bien mais on note pour référence ? → `Info`
- Est-ce que c'est du bruit pour diagnostic expert ? → `Verbose`
- Est-ce que c'est l'app qui raconte son histoire à l'utilisateur ? → `Narrative`

### Format par niveau — deux registres distincts

**Info / Success** — phrase Capital courte, lue comme un jalon dans Activity.
Pas de `k=v`, pas d'unités techniques. Un détail court entre parenthèses
reste admis quand il porte l'essentiel du jalon (backend, durée perçue,
outcome). Exemples :

```
Info     MODEL       Loading model
Success  MODEL       Model loaded (Vulkan)
Info     RECORD      Recording start
Info     RECORD      Recording complete (12.3 s)
Info     TRANSCRIBE  Transcribing
Success  TRANSCRIBE  Transcription complete (5 seg)
Info     LLM         Rewriting (Short)
Success  LLM         Rewrite complete
Info     CLIPBOARD   Copied to clipboard
Info     PASTE       Pasted
Success  DONE        Done (Pasted)
```

**Warning / Error** — phrase Capital riche. Quand l'alerte nécessite des
détails (endpoint, code d'erreur, durée), les exprimer en prose (`Ollama
busy — model X resident (2.1 GB). Waited 60s so far…`). Pas de `k=v`
dans la prose Warning/Error visible, même si un double `Verbose` peut
exposer les champs machine-greppables en parallèle.

**Verbose** — détail technique machine-greppable, format :

```
<action ou état> | <mesure1>=<val1> | <mesure2>=<val2> ...
```

- Préfixe court (verbe ou état) en tête, mesures séparées par ` | `
- Premier mot en minuscule, une seule ligne
- Ne jamais répéter l'étape dans le message : la source (`RECORD`, `LLM`…)
  la porte déjà
- Une Info/Success est **toujours** accompagnée d'un Verbose miroir quand
  des mesures techniques existent. Jamais de doublon visible dans Activity.

Exemples miroir :

```
Info     MODEL       Loading model
Verbose  MODEL       load start | file=ggml-large-v3.bin | file_mb=2951.7 | use_gpu=1
Success  MODEL       Model loaded (Vulkan)
Verbose  MODEL       load complete | load_ms=420 | backend=Vulkan
```

**Narrative** — phrase UX complète adressée à l'utilisateur. Capital,
ponctuée. Dit ce que fait l'app **et pourquoi** à l'étape en cours.
Complète les Info/Success (ne les remplace pas). Une narrative par grand
jalon : model load, record start, VAD, transcribe, rewrite, clipboard,
paste, done.

**Notation d'assignation d'identifiant** (`Paths.ModelsDirectory ← "…"`,
`SpeechDetection.Enabled ← true`) — exception : la casse du membre C# est
préservée, c'est du code reporté, pas de la prose. Reste Info (change de
setting = jalon utilisateur).

**Texte brut** (segment transcrit, contenu clipboard) — conserve sa casse
native, ne subit pas la règle Capital. C'est du contenu, pas un message.

### Narrative

Narrative est une phrase UX anglais complète, adressée à l'utilisateur, sans
mesure technique. Pas le même fichier qu'un `Info` — chaque étape émet
**soit** un Info technique, **soit** un Narrative, jamais les deux à la place
l'un de l'autre.

Règle d'écriture Narrative — voir vague 5 (passe `design:ux-copy`) pour la
polish définitive. Forme provisoire actuelle à conserver.

### Sources (`LogSource`)

Vocabulaire fermé, déjà défini dans `src/Deckle/Logging/LogSource.cs`.
Ne pas en ajouter sans raison. Mapping étape → source :

| Étape | Source |
|---|---|
| Hotkey global | `HOTKEY` |
| Tray | `TRAY` |
| Chargement modèle | `MODEL` |
| Warmup | `INIT` |
| Enregistrement | `RECORD` |
| VAD | `WHISPER` (interne whisper.cpp) + consolidation via `TRANSCRIBE` |
| Transcription | `TRANSCRIBE` |
| Callback segment | `CALLBACK` |
| LLM rewrite | `LLM` |
| Clipboard | `CLIPBOARD` |
| Paste | `PASTE` |
| Recap final | `DONE` |
| App lifecycle | `APP`, `STATUS`, `CRASH` |
| Settings | `SETTINGS`, `SET.*` |

---

## Inventaire par étape

Pour chaque étape : fichier principal, mesures disponibles (marquage
`[logué]` / `[dispo non logué]` / `[à instrumenter]`), gabarit d'événement
Info standard, erreurs et leur sévérité cible.

### 1. Input utilisateur (hotkey / tray)

**Fichiers** : `src/Deckle/Shell/HotkeyManager.cs`, `src/Deckle/Shell/TrayIconManager.cs`,
`src/Deckle/App.xaml.cs:OnHotkey`

**Mesures disponibles**

- `[logué]` scancode → VK résolu (Verbose au register)
- `[logué]` hotkey ID déclenché (Success au trigger)
- `[logué]` layout change + re-register (Verbose / Warning si échec)
- `[dispo non logué]` nom de la source (hotkey / tray click) — confondus actuellement
- `[à instrumenter]` temps entre trigger et début recording effectif (latence perçue)

**Gabarit Info standard**

```
Success HOTKEY  trigger | id={idName} | source={hotkey|tray}
```

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback | Notes |
|---|---|---|---|
| Pas de profile bindé | Warning | non (rare, config) | déjà Warning au 364 App.xaml.cs |
| MapVirtualKeyExW returns 0 | Warning | non | layout exotique, skip |
| RegisterHotKey err 1409 (collision) | Error | **Critical — nouvelle** | "Another app holds this shortcut" |
| Re-register layout fail | Warning | non | transparent |

### 2. Chargement modèle

**Fichier** : `src/Deckle/WhispEngine.cs:446-496` (LoadModel), `500-545` (lifecycle idle)

**Mesures disponibles**

- `[logué]` path, file_mb (file size)
- `[logué]` use_gpu flag (1)
- `[logué]` ctx pointer (Verbose)
- `[logué]` load_ms (Success final)
- `[à instrumenter]` backend effectif (Vulkan / CUDA / CPU) — parsable depuis ggml logs
- `[à instrumenter]` VRAM utilisée — émis par ggml en Verbose, non extrait
- `[dispo non logué]` model filename vs display name (ex. `ggml-large-v3.bin`)

**Gabarit standard**

```
Info      MODEL  Loading model
Narrative MODEL  Loading the Whisper model into GPU memory — a {X} MB speech recognizer…
Verbose   MODEL  load start | file={basename} | file_mb={X:F1} | use_gpu=1
Success   MODEL  Model loaded ({backend})
Verbose   MODEL  load complete | load_ms={X} | backend={Vulkan|CUDA|CPU}
Success   MODEL  Model unloaded
Verbose   MODEL  model unloaded | idle_s={X} | state=vram-freed
```

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback |
|---|---|---|
| File not found on disk | Error | **Critical déjà** "Whisper model not found" |
| whisper_init returns 0 | Error | **Critical déjà** "Failed to load model" |
| GPU init fails (pas de fallback) | Error | **Critical — à ajouter** "GPU init failed. Check settings." |

### 3. Warmup startup

**Fichier** : `src/Deckle/WhispEngine.cs:555-603` (Warmup)

**Mesures disponibles**

- `[logué]` warmup_ms total (Verbose)
- `[logué runtime, pas warmup]` `model_load_ms` capturé par `LoadModel()` et exposé sur le prochain `LatencyPayload`. Le warmup paye souvent ce coût en premier (modèle pas encore en VRAM), donc le 1er hotkey post-warmup voit `model_load_ms=0`. Pour observer le coût pur du warmup load, regarder le Verbose `MODEL load complete | load_ms={X}` émis pendant `Warmup()`.
- `[à instrumenter]` warmup breakdown distinct : vad_ms / whisper_ms du run silencieux (aujourd'hui gated par `_isWarmup`, aucun payload n'est émis)
- `[à instrumenter]` résultat flag : model_ok / ollama_ok (vague 3)
- `[à instrumenter]` texte produit sur WAV de référence (vague 3) — pour vérifier plausibilité

**Gabarit standard**

```
Info     INIT  Warmup start
Success  INIT  Warmup complete
Verbose  INIT  warmup complete | total_ms={X} | mic_ok={bool} | model_ok={bool} | ollama_ok={bool}
```

**Erreurs et sévérités**

Warmup est **silencieux par nature**. Aucun UserFeedback pendant le warmup.
Les flags sont stockés et consultés au premier hotkey :

| Flag false | Sévérité au premier hotkey | UserFeedback |
|---|---|---|
| `model_ok=false` | Error | Critical "Model not ready. Check settings." |
| `mic_ok=false` | Error | Critical "No microphone detected. Check settings." |
| `ollama_ok=false` | Warning | Warning "Rewriter unavailable. Recording still works." |

### 4. Enregistrement audio

**Fichier** : `src/Deckle/WhispEngine.cs:620-905` (Record), `715-747` (mic probe),
`911-930` (tail RMS analysis), `939-963` (EmitAudioLevels)

**Mesures disponibles**

- `[logué]` mic probe result (MMSYSERR code, UserFeedback)
- `[logué]` recording started message (Info)
- `[logué]` empty buffer received (Warning)
- `[logué]` lag buffer race (Warning)
- `[logué]` per-50ms RMS envoyé au HUD (pas loggué, juste event)
- `[logué]` capture complete : totalSec, bytes (Info)
- `[logué]` tail RMS / dBFS au Stop (Info)
- `[à instrumenter]` RMS moyen sur l'enregistrement entier
- `[à instrumenter]` RMS crête sur l'enregistrement entier
- `[à instrumenter]` dBFS moyen (pour détection son bas)
- `[à instrumenter]` ratio silence / parole (estimé avant VAD, fenêtre glissante ?)
- `[à instrumenter]` nombre de buffers reçus vs attendus
- `[à instrumenter]` device name effectif utilisé

**Gabarit standard**

```
Info      RECORD  Recording start
Verbose   RECORD  capture start | sample_rate=16 kHz | channels=mono
Info      RECORD  Recording complete ({X:F1} s)
Verbose   RECORD  capture complete | audio_sec={X:F1} | buffers={n} | bytes={n} | rms_avg={X:F4} | rms_peak={X:F4} | dbfs_avg={X:F1}
Verbose   RECORD  tail | tail_ms={X:F0} | rms={X:F4} | dbfs={X:F1} | state={active|silent}
Narrative RECORD  Captured {X:F1} s of audio. Moving on to analysis and transcription.
```

**Supprimé** : `+Xs captured` toutes les 50 ms. Bruit pur, aucune valeur
agrégée.

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback |
|---|---|---|
| Mic absent (MMSYSERR 2, 6) | Error | **Critical déjà** "No microphone detected" |
| Mic occupé (MMSYSERR 4) | Error | **Critical déjà** "Microphone in use" |
| Autre MMSYSERR | Error | **Critical déjà** "Microphone unavailable" |
| Mic débranché pendant recording | Warning | **à ajouter** "Microphone disconnected" — détection via tail RMS = 0 |
| dBFS moyen < -50 dBFS | Warning | **à ajouter** "Low audio detected. Check microphone." |
| Durée max dépassée (auto-stop) | Warning | oui Narrative "Recording exceeded X s — auto-stopping" |

### 5. VAD

**Fichier** : `src/Deckle/WhispEngine.cs:240-400` (InstallWhisperLogHook),
`409-434` (EmitVadSummary), `195-209` (regex parsers)

**Mesures disponibles**

- `[logué]` segments count (Verbose consolidé)
- `[logué]` speech duration sec
- `[logué]` reduction %
- `[logué]` inference ms
- `[logué]` mapping points
- `[logué]` wall clock sec
- `[à instrumenter]` ratio silence = 1 - (speech_sec / audio_sec), utile pour UX

**Gabarit Info standard**

```
Info     TRANSCRIBE vad complete | n_seg={n} | speech_sec={X:F1} | reduction={X:F1}% | inference_ms={X:F0} | silence_ratio={X:F2}
```

**Narrative existantes — à conserver**

- `Looking for speech in the recording — a small detector is scanning the audio for spoken segments.`
- `No speech detected in the recording.` (si speech=0)
- `Speech detected — {X:F1} s of speech. Passing to Whisper for transcription.`

**Erreurs et sévérités**

Pas d'erreur VAD séparée : VAD échoue en silence, Whisper prend le relais sur
l'audio brut. Si speech=0, on remonte juste Narrative "No speech detected" et
pas de transcription. Pas d'UserFeedback ni Warning.

### 6. Transcription Whisper

**Fichier** : `src/Deckle/WhispEngine.cs:1060-1400` (Transcribe), `976-1058`
(OnNewSegment callback)

**Mesures disponibles**

- `[logué]` audio_sec, samples (Info "Audio reçu")
- `[logué]` params complets (Verbose dense — à alléger)
- `[logué]` initial_prompt truncated + length
- `[logué]` per-segment : t0, t1, dur, gap, nsp, p̄, min, text_tok/n_tok, elapsed, text
- `[logué]` whisper_full result code
- `[logué]` total whisper_ms, n_seg, full_text length
- `[à instrumenter]` tok_s whisper (total tokens / whisper_ms)
- `[à instrumenter]` avg confidence sur tous les segments
- `[à instrumenter]` avg gap entre segments
- `[à instrumenter]` repetition detector trigger (Warning existe, compter le déclenchement)

**Gabarit standard**

```
Info      TRANSCRIBE Transcribing
Verbose   TRANSCRIBE start | audio_sec={X:F1} | samples={n} | strategy={greedy|beam}
Verbose   TRANSCRIBE params | temp={X:F2}+{X:F2} | logprob_thold={X:F2} | entropy_thold={X:F2} | n_threads={n}
Verbose   TRANSCRIBE prompt | len={n} | carry={bool} | text="{truncated}"
Verbose   CALLBACK   seg #{i} | t0={X:F1} | t1={X:F1} | dur={X:F1} | gap={X:F1} | nsp={X:P0} | p̄={X:F2} | min={X:F2} | tok={textTok}/{nTok} | elapsed={X:F1} | text="{text}"
Success   TRANSCRIBE Transcription complete ({n} seg)
Verbose   TRANSCRIBE complete | whisper_ms={X} | n_seg={n} | chars={n}
Narrative TRANSCRIBE Whisper transcribed the speech into {n} segments in {X:F1} s.
```

**Callback entièrement en Verbose** : le signal par segment (texte +
timings + confiance) est trop granulaire pour Activity — Activity
affiche des jalons, pas des détails qui fusent à la cadence du décodeur.
**Une seule ligne dense par segment**, jamais deux (pas de split
Info+Verbose : ça crée des doublons visibles à l'écran avec le même
timestamp). Activity montre uniquement `Transcribing` et
`Transcription complete` comme étapes.

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback |
|---|---|---|
| Audio vide (0 samples) | Warning | non (user stop immédiat) |
| whisper_full returns ≠ 0 | Error | **à ajouter** Critical "Transcription failed. Check logs." |
| Repetition loop détecté | Warning | non (déjà Narrative "Whisper got stuck...") |
| Callback exception | Error | non (segment ignoré, continue) |

### 7. Rewriting LLM

**Fichier** : `src/Deckle/Llm/LlmService.cs:51-138` (Rewrite), `149-217`
(PollOllamaWhileBusy)

**Mesures disponibles**

- `[logué]` request init : text_chars, model, profile, family, options
- `[logué]` Rewrite OK : total_ms, in_chars, out_chars, profile
- `[logué]` metrics détaillés : total/load/prompt/output ms, tokens, tok/s (Verbose LLM)
- `[logué]` `ollama_load_ms`, `llm_prompt_eval_ms`, `llm_eval_ms`, `llm_prompt_tokens`, `llm_eval_tokens` remontés au caller via `RewriteResult` et écrits dans le `LatencyPayload` JSONL — permet l'analyse stat hors LogWindow.
- `[logué]` polling /api/ps warnings pendant attente
- `[logué]` timeout 15 min (Warning)
- `[logué]` unavailable (Warning log)
- `[à instrumenter]` first-token latency (dans stream response si on y passe — voir règle clipboard 2-états dans `CLAUDE.md` avant d'attaquer)

**Gabarit standard**

```
Info      LLM  Rewriting ({profile.Name})
Narrative LLM  Rewriting the transcript with {profile.Name} — the {family} model running in Ollama is cleaning up the raw text into the final phrasing.
Verbose   LLM  request | chars={n} | model={name} | profile={name} | family={name} | opts=…
Success   LLM  Rewrite complete
Verbose   LLM  rewrite complete | ms={X} | in_chars={n} | out_chars={n} | profile={name}
Verbose   LLM  metrics: total={X}ms load={X}ms | prompt {n}tok en {X}ms ({X:F1} tok/s) | output {n}tok en {X}ms ({X:F1} tok/s)
Narrative LLM  Rewrite complete in {X:F1} s with the {profile.Name} profile — the polished text is ready to paste.
```

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback |
|---|---|---|
| Profile.Model vide | Warning | non (config oubliée, rare) |
| Exception réseau (connection refused, timeout court) | Warning | **à ajouter** Warning "Rewriter unavailable. Raw transcript kept." |
| Timeout 15 min | Warning | **à ajouter** Warning "Rewriter took too long. Raw transcript kept." |
| Polling /api/ps unreachable | Warning | non (diagnostic interne) |
| Ollama busy (modèle résident, autre requête) | Warning | non (attente normale) |

### 8. Clipboard

**Fichier** : `src/Deckle/WhispEngine.cs:1403-1450` (CopyToClipboard)

**Mesures disponibles**

- `[logué]` GlobalAlloc bytes → hMem (Verbose)
- `[logué]` OpenClipboard bool (Verbose)
- `[logué]` SetClipboardData fail (Warning)
- `[logué]` verify no Unicode data (Warning)
- `[logué]` verify length mismatch (Warning)
- `[logué]` Text copied chars (Info)
- `[à instrumenter]` retour bool succès / échec propagé au caller
- `[à instrumenter]` elapsed ms total copy+verify

**Gabarit standard**

```
Info      CLIPBOARD Copied to clipboard
Narrative CLIPBOARD The transcription is now on the clipboard — {n} characters ready to paste anywhere.
Verbose   CLIPBOARD copy complete | chars={n} | bytes={n}
```

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback |
|---|---|---|
| GlobalAlloc fails | Error | **à ajouter** Critical "Clipboard copy failed." |
| OpenClipboard fails | Error | **à ajouter** Critical "Clipboard unavailable." |
| SetClipboardData fails | Error | **à ajouter** Critical "Clipboard copy failed." |
| Verify length mismatch | Warning | **à ajouter** Warning "Clipboard copy may be incomplete." |

**Signature à changer (vague 4)** : `void CopyToClipboard` → `bool`. Le
HUD `ShowCopied()` ne se déclenche que si `true`. Le caller peut décider
d'une autre surface UX sur `false`.

### 9. Paste (désactivé par défaut)

**Fichier** : `src/Deckle/WhispEngine.cs:1455-1520`

Paste désactivé (`PasteSettings.AutoPasteEnabled = false`). Pour mémoire —
aucune normalisation urgente. Les 5 refus actuels sont loggés Warning
chacun, et le HUD montre `ShowCopied()` en fallback (le clipboard
contient le texte). Comportement correct, ne pas toucher tant que paste
reste désactivé.

### 10. Recap final

**Fichier** : `src/Deckle/WhispEngine.cs:1921-1956` (Verbose recap +
Narrative Done + LatencyPayload).

**Mesures disponibles** — toutes écrites dans le `LatencyPayload` (JSONL `latency.jsonl`), accessibles aussi via Verbose `DONE timings`.

Pipeline (entrée → sortie) :

- `[logué]` `audio_sec` — durée de l'enregistrement (1 décimale).
- `[logué]` `model_load_ms` — load Whisper du run, 0 si warm.
- `[logué]` `hotkey_to_capture_ms` — entrée `StartRecording` → `waveInStart`. Inclut `model_load_ms` sur cold start, plus mic probe et thread spin-up.
- `[logué]` `record_drain_ms` — `_stopRecording=true` → fin de `Record()` (waveInStop + 100 ms guard sleep + drain buffers + telemetry compute). Sous-ensemble de `stop_to_pipeline_ms`.
- `[logué]` `stop_to_pipeline_ms` — entrée `StopRecording` → première ligne `whisper_vad`. Couvre `record_drain` + `Transcribe` entry.
- `[logué]` `whisper_init_ms` — entrée `whisper_full()` → première ligne `whisper_vad`. Pré-VAD overhead côté whisper.cpp.
- `[logué]` `vad_ms` — wall time bracket parsing logs whisper.cpp (premier `whisper_vad` → `Reduced audio from`).
- `[logué]` `vad_inference_ms` — `vad time = X ms` parsé depuis les logs whisper.cpp. `vad_ms − vad_inference_ms` = overhead non-inférence (alloc, log dispatch).
- `[logué]` `whisper_ms` — décodage pur : `transcribe_total − vad_ms − whisper_init_ms`.
- `[logué]` `llm_ms` — wall caller-side : HTTP POST + lecture body + JSON parse.
- `[logué]` `ollama_load_ms` — `load_duration` Ollama. 0 si modèle déjà résident.
- `[logué]` `llm_prompt_eval_ms` — `prompt_eval_duration` (eval input tokens).
- `[logué]` `llm_eval_ms` — `eval_duration` (génération output tokens).
- `[logué]` `llm_prompt_tokens` — `prompt_eval_count` (tokens d'entrée).
- `[logué]` `llm_eval_tokens` — `eval_count` (tokens de sortie). `tok/s = llm_eval_tokens / llm_eval_ms`.
- `[logué]` `clipboard_ms` — copie raw + verify (rewrite remplace ensuite, voir règle clipboard).
- `[logué]` `paste_ms` — UIA probe + SendInput Ctrl+V.
- `[logué]` `n_segments`, `text_chars`, `text_words`, `strategy`, `profile`, `pasted`, `outcome`.
- `[logué]` Narrative DONE "Done — X s of dictation processed".

**Côté LogWindow** : la ligne `[LATENCY]` (rendu compact dans `TelemetryService.Latency`) montre `audio | hotkey | vad | whisper | llm | outcome` — les étapes qui varient run-to-run et que l'œil humain peut diagnostiquer d'un coup. Les autres champs ne sont pas dans le rendu compact mais vivent dans le JSONL avec full précision.

**Gabarit standard**

```
Success   DONE Done ({outcome})
Verbose   DONE timings | audio_sec={X:F1} | model_load_ms={X} | hotkey_to_capture_ms={X} | record_drain_ms={X} | stop_to_pipeline_ms={X} | whisper_init_ms={X} | vad_ms={X} | vad_inference_ms={X} | whisper_ms={X} | llm_ms={X} | clipboard_ms={X} | paste_ms={X}
Verbose   DONE llm_metrics | ollama_load_ms={X} | prompt_eval_ms={X} | eval_ms={X} | prompt_tokens={n} | eval_tokens={n}
Verbose   DONE outputs | n_seg={n} | chars={n} | words={n} | strategy={name} | profile={name} | outcome={name}
Narrative DONE Done — {audio_sec:F1} s of dictation processed. Ready for the next.
```

**Rendu LogWindow** (précompilé par `TelemetryService.Latency`)

```
HH:mm:ss.fff [LATENCY] audio={X.X}s hotkey={X}ms vad={X}ms whisper={X}ms llm={X}ms outcome={X}
```

---

## Tableau synthétique des erreurs et surface UX

Récap croisé : quelle erreur, où, quelle sévérité, quel message utilisateur
(si applicable). `✅` = déjà en place. `➕` = à ajouter dans une vague.

| # | Erreur | Étape | Sévérité log | UserFeedback |
|---|---|---|---|---|
| 1 | Mic absent / occupé | Record probe | Error | ✅ Critical |
| 2 | Modèle absent / corrompu | Model load | Error | ✅ Critical |
| 3 | GPU init échoue | Model load | Error | ➕ Critical (vague 2) |
| 4 | Audio bas détecté | Record post-analyse | Warning | ➕ Warning (vague 4) |
| 5 | Mic débranché en cours | Record tail | Warning | ➕ Warning (vague 4) |
| 6 | Ollama injoignable au warmup | Warmup | Info silencieux | ➕ Warning au 1er hotkey (vague 3) |
| 7 | Ollama injoignable pendant session | LLM rewrite | Warning | ➕ Warning (vague 4) |
| 8 | Ollama timeout 15 min | LLM rewrite | Warning | ➕ Warning (vague 4) |
| 9 | Clipboard GlobalAlloc / SetData fail | Clipboard | Error | ➕ Critical (vague 4) |
| 10 | Clipboard verify length mismatch | Clipboard | Warning | ➕ Warning (vague 4) |
| 11 | whisper_full returns non-zero | Transcribe | Error | ➕ Critical (vague 2) |
| 12 | Hotkey collision err 1409 | Hotkey register | Error | ➕ Critical (vague 3) |
| 13 | Repetition loop | Transcribe | Warning | ✅ Narrative |
| 14 | No speech detected | VAD | Info | ✅ Narrative |

---

## Règles d'application (à suivre dans les vagues suivantes)

1. **Une étape = un Info de début, un Info de fin.** Entre les deux, du
   Verbose si nécessaire, pas d'Info répétés.
2. **Les heartbeats haute fréquence (< 1 s) ne sont pas loggués.** Ils
   alimentent les events UI (AudioLevel → HUD) mais pas la LogWindow.
3. **Les mesures suivent le vocabulaire ci-dessus.** Si une unité manque,
   l'ajouter dans ce document avant de l'utiliser.
4. **Les Narrative sont en anglais, les Info techniques aussi.** Pas de
   français dans les logs.
5. **Un UserFeedback est toujours doublé d'un log** du même niveau. Le
   log reste pour diagnostic, le HUD est pour l'utilisateur.
6. **Jamais de log multi-ligne.** Une entrée = une ligne.
7. **La source porte le contexte.** Ne pas écrire "RECORD: started
   recording..." dans le message : la colonne Source affiche déjà `RECORD`.

---

## Appendice — Lifecycle app

Pour complétude, signal à part du pipeline principal.

### Startup (`App.OnLaunched`)

Les milestones sont **accumulés** puis **flushés en un seul Verbose** à la
fin (pattern déjà en place, ligne 211 App.xaml.cs). Les grandes
transitions visibles dans Activity sont portées par `STATUS` (Ready,
Recording, Transcribing…), pas par la trace milestone. Format :

```
Verbose  APP  startup milestones | filesink=+{X}ms | engine=+{X}ms | logwindow=+{X}ms | settingswindow=+{X}ms | hudwindow=+{X}ms | tray=+{X}ms | hotkeys=+{X}ms | warmup=+{X}ms
Info     STATUS Ready
```

### Shutdown (`App.QuitApp`)

Cinq étapes de cleanup actuellement chacune en Warning try/catch. À
conserver. Aucune normalisation requise.

### Settings load

Actuellement silencieux sur les erreurs de parse JSON. À instrumenter en
vague 2 :

```
Info     SETTINGS  load complete | path={path} | migrated_from={ver} | sections={n}
Warning  SETTINGS  parse failed, fallback to defaults | path={path} | error={ex.Type}: {ex.Message}
```

---

## Mise à jour du document

Version à bump en `0.2` si des vagues suivantes révèlent des points de mesure
manquants ou des conventions à affiner. Version `1.0` quand les vagues 1-4
sont appliquées et le document reflète exactement le code.
