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
Patch en cours (filename à bumper à `1.1` quand on consolide) :
renforce la doctrine de séparation Verbose ↔ Info (les IDs et le
format `k=v` sont Verbose-only, jamais dans Info / Success / Warning /
Error) avec une table d'exemples ❌/✅, et documente le toggle **Log
ambient capture activity** ajouté dans Diagnostics. L'architecture
retenue après itération : filtre central dans `TelemetryService.Log`
avec dimension temporelle, `_captureActive` flag set/clear par
`AmbientEngine` autour de sa boucle. Couvre les Verbose émis par
HueBridgeClient et ScreenCaptureService aussi (modules consumed par
l'engine), pas seulement par l'engine lui-même. Default OFF —
quiet par défaut.
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

### Image / capture vidéo

| Mesure | Unité | Précision | Exemple | Notes |
|---|---|---|---|---|
| Frames par seconde | `fps` | 1 décimale | `fps=59.8` | mesuré sur fenêtre glissante 1 s |
| Compte de frames | `frames` | entier | `frames=124` | depuis Start de la session |
| Résolution écran | `WxH` | entier | `size=1920x1080` | `Direct3D11CaptureFrame.ContentSize` |
| Format pixel | nom | — | `format=B8G8R8A8UIntNormalized` | enum DirectXPixelFormat |
| Buffers pool | `bufs` | entier | `bufs=2` | typiquement 2 |
| Handle moniteur | `hex` | — | `hmon=0x000100A3` | retour `MonitorFromPoint` |

### Retours d'appel

| Mesure | Format | Exemple |
|---|---|---|
| Code natif | `name={int}` | `result=0`, `mmsys=4` |
| HRESULT | `name=0x{hex}` | `hr=0x80004005` |
| Outcome enum | `outcome={value}` | `outcome=Pasted` |
| Pointeur natif | `hex` | `ctx=0x7ff8a3c01400` |

### Réseau / drivers LED

| Mesure | Unité | Précision | Exemple | Notes |
|---|---|---|---|---|
| Adresse IP | IPv4 | — | `bridge_ip=192.168.1.5` | LAN local |
| Bridge identifier | hex16 | — | `bridge_id=001788FFFE3A2C18` | Hue serial number |
| Application key | hex32+ | — | `username=eDOvxk-...` | tronqué à 8 chars + `...` dans les logs |
| Pre-shared key | — | — | `clientkey=[redacted]` | jamais loggué en clair, valeur sensible (PSK DTLS) |
| Group / room ID | string | — | `group_id=3` | CLIP v1 = entier, v2 = UUID |
| HTTP status | `hr` | entier | `hr=200`, `hr=401` | code retour HTTP |
| Couleur CIE xy | `xy` | 4 décimales | `xy=0.4521,0.3895` | espace colorimétrique Hue |
| Luminance | `bri` | entier 0-254 | `bri=200` | CLIP v1 brightness |
| Couleur RGB | `rgb` | 3 octets | `rgb=180,60,240` | input app, converti en xy avant push |

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

### Doctrine de séparation Verbose ↔ Info — règle dure

**Les identifiants opaques et le format `k=v` sont Verbose-only.** Une ligne `Info`, `Success`, `Warning` ou `Error` est une phrase Capital courte, lisible par un humain qui n'a aucune connaissance de l'implémentation. Si la ligne contient un ID (light id Hue, group id, file path, hash, line index, opaque token quelconque) ou un format à séparateurs `|`, alors par définition c'est une ligne Verbose, pas une ligne sémantique. Un Info qui contient un ID est une erreur de niveau, pas une variante stylistique.

Lorsqu'une action mérite à la fois une signalisation sémantique pour l'utilisateur ET un détail technique pour le diag, on émet **deux lignes** : une Info Capital sans IDs, et son miroir Verbose en k=v avec les IDs. Aucun chevauchement.

| ❌ Mauvais (mélange) | ✅ Bon (séparation) |
|---|---|
| `Info AMBIENT zone assign \| id=42 \| zone=Top` | `Info AMBIENT Zone Top assigned to Falcon` |
| | `Verbose AMBIENT zone assign \| id=42 \| zone=Top` |
| `Info AMBIENT settings update \| key=UseMultiLight \| value=true` | `Info AMBIENT Pipeline mode set to per-zone` |
| | `Verbose AMBIENT settings update \| key=UseMultiLight \| value=true` |

Le miroir Verbose **suit toujours** l'Info Capital quand il y a un détail technique à acter. Ce n'est pas optionnel — c'est le contrat qui rend les logs greppables.

### Filtre runtime : capture activity (per-loop window)

Depuis le polish J4, la page Diagnostics expose une section **Logging** sœur de **Telemetry**, qui héberge un toggle par boucle runtime pour suspendre l'émission de ses lignes per-tick. Premier (et seul aujourd'hui) toggle : **Log ambient capture activity**, OFF par défaut.

Le filtre est centralisé dans `TelemetryService.Log` et joue sur trois dimensions :

```
si  _captureActive
    && level == LogLevel.Verbose
    && source ∈ {AMBIENT, SCREEN, HUE}
    && !LogAmbientCaptureActivity
alors drop
```

`_captureActive` est un `volatile bool` sur `TelemetryService`, set par `AmbientEngine.StartAsync` juste avant de lancer la boucle push (donc après ses milestones Info + Verbose mirror), clear par `AmbientEngine.Stop` dès l'entrée (donc avant les milestones de stop). C'est cette dimension temporelle qui rend le filtre correct : un `Verbose AMBIENT` émis pendant que la boucle est idle (zone suggest au build, settings update au démarrage, etc.) passe parce que `_captureActive` est faux. Un `Verbose HUE push colour | …` émis pendant que la boucle tourne (depuis `HueBridgeClient.SetLightColorAsync`, appelé inside loop) est filtré.

Ce qui n'est jamais coupé par ce toggle :

- Tout niveau ≥ Info — milestones, warnings, errors, narrative. Les `Info AMBIENT Ambient pipeline started / stopped` passent toujours, les `Warning AMBIENT Push failed` et `Error AMBIENT Push loop crashed` aussi.
- Toute ligne émise hors window capture-active — par construction, le flag est faux à ces moments-là (group resolve, lights listing, sampler init, stop diagnostic mirror).
- Toute Verbose émise depuis une source hors {AMBIENT, SCREEN, HUE} — le set est ciblé sur la pipeline ambient.

Ce qui est gated quand toggle OFF :

- `Verbose AMBIENT push | …` (per-tick), `Verbose AMBIENT heartbeat | …` — émis par `AmbientEngine.PushLoopAsync`.
- `Verbose HUE push colour | light_id=… | rgb=… | …` — émis par `HueBridgeClient` à chaque PUT inside loop.
- `Verbose SCREEN` runtime traces (frame ops, sampler diagnostics inside loop).

**Pourquoi un filtre central plutôt qu'au site d'émission.** Une itération précédente avait posé le check `if (LoggingSettingsService.Instance.Current.LogAmbientCaptureActivity) _log.Verbose(...)` dans `AmbientEngine.PushLoopAsync`. Ça gate l'engine mais PAS les modules qu'il consomme (HueBridgeClient, ScreenCaptureService) — leurs lignes Verbose continuaient de sortir. Pour les couvrir, il faudrait répéter le check dans chaque module, ce qui couple les modules consumed à un toggle qui n'est pas leur affaire. Le filtre central laisse les modules naïfs et porte la dimension temporelle (`_captureActive`) une seule fois, sur le hub qui voit tout passer.

**Pourquoi pas un filtre `(level, source)` central sans la dimension temporelle.** Une itération encore antérieure avait tenté ça. Le problème : un `Verbose AMBIENT zone assign | …` mirror d'action utilisateur est structurellement identique à un `Verbose AMBIENT push | …` per-tick. Sans dimension temporelle, le filtre ne peut pas les distinguer et silence les deux. Le flag temporel résout ça : un user action émis pendant que la boucle est idle (cas normal) passe ; émis pendant que la boucle tourne (cas rare), il est filtré, mais l'Info Capital miroir (« Zone Top assigned to Falcon ») le compense en restant visible dans Activity.

Ce pattern grandira avec les boucles suivantes (transcription Whisp, capture micro Audio…) : un toggle par boucle, un flag `_<loop>Active` par boucle sur `TelemetryService`, le module set/clear son flag autour de son window, le filtre central étend son set de sources et son test booléen.

### Format par niveau — deux registres distincts

**Info / Success** — phrase Capital courte, lue comme un jalon dans Activity.
Pas de `k=v`, pas d'unités techniques. Un détail court entre parenthèses
reste admis quand il porte l'essentiel du jalon (backend, durée perçue,
outcome). Exemples :

```
Info     MODEL       Loading model
Success  MODEL       Model loaded (Vulkan)
Info     CAPTURE     Recording start
Info     CAPTURE     Recording complete (12.3 s)
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
- Ne jamais répéter l'étape dans le message : la source (`CAPTURE`, `LLM`…)
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
| Enregistrement | `CAPTURE` |
| VAD | `WHISPER` (interne whisper.cpp) + consolidation via `TRANSCRIBE` |
| Transcription | `TRANSCRIBE` |
| Callback segment | `CALLBACK` |
| LLM rewrite | `LLM` |
| Clipboard | `CLIPBOARD` |
| Paste | `PASTE` |
| Recap final | `DONE` |
| App lifecycle | `APP`, `STATUS`, `CRASH` |
| Settings | `SETTINGS`, `SET.*` |
| Capture écran (ambient lighting) | `SCREEN` |
| Driver Hue (discovery, pairing, REST control) | `HUE` |
| AmbientEngine (capture → analyse → push) | `AMBIENT` |

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

**Fichier** : `src/Deckle.Audio/MicrophoneCapture.cs` (Record + Probe),
`src/Deckle.Audio/Internal/WaveInLoop.cs` (polling loop + EmitSubWindows),
`src/Deckle.Audio/Telemetry/MicrophoneTelemetryCalculator.cs` (tail RMS + percentiles)

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
Info      CAPTURE  Recording start
Verbose   CAPTURE  capture start | sample_rate=16 kHz | channels=mono
Info      CAPTURE  Recording complete ({X:F1} s)
Verbose   CAPTURE  capture complete | audio_sec={X:F1} | buffers={n} | bytes={n} | rms_avg={X:F4} | rms_peak={X:F4} | dbfs_avg={X:F1}
Verbose   CAPTURE  tail | tail_ms={X:F0} | rms={X:F4} | dbfs={X:F1} | state={active|silent}
Narrative CAPTURE  Captured {X:F1} s of audio. Moving on to analysis and transcription.
```

> Source renommée le 2026-05-02 : `RECORD` → `CAPTURE` lors de l'extraction
> du module `Deckle.Audio`. La capture micro est partagée entre Whisp et
> les futurs modules (Ask-Ollama), donc le tag reflète la capability plutôt
> que l'intention d'un orchestrateur. Voir
> `src/Deckle.Audio/MicrophoneCapture.cs` pour l'émetteur unique.

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

## Pipeline ambient lighting

Pipeline distinct du pipeline transcription. Démarre quand l'utilisateur active la fonction depuis le panneau Playground (J1, période d'investigation) ou plus tard depuis le toggle Settings (J3+). Les étapes côté capture écran sont décrites ici ; les étapes analyse image et pilotage LED s'ajouteront au fil des jalons.

### 11. Capture écran

**Fichier** : `src/Deckle.Vision/ScreenCaptureService.cs` (lifecycle, FrameArrived dispatch, HDR detection, MinUpdateInterval), `src/Deckle.Vision/ScreenCaptureInterop.cs` (P/Invoke D3D11 + IGraphicsCaptureItemInterop COM, DXGI HDR factory walk), `src/Deckle.Vision/FrameSampler.cs` (GPU downsample via GenerateMips, CPU readback, tone-map).

**Mesures disponibles**

- `[logué]` taille frame initiale au Start (W×H)
- `[logué]` format pixel utilisé (DirectXPixelFormat — BGRA8 en SDR, R16G16B16A16Float en HDR)
- `[logué]` flag HDR détecté (on/off depuis `IDXGIOutput6::GetDesc1`)
- `[logué]` peak luminance reporté par l'écran (nits)
- `[logué]` cadence cible `min_update_ms` (66 ms = 15 Hz par défaut, throttle côté session via `GraphicsCaptureSession.MinUpdateInterval`)
- `[logué]` nombre de buffers dans le pool
- `[logué]` HMONITOR ciblé (hex)
- `[logué]` HRESULT D3D11CreateDevice (Verbose si succès, Error si échec)
- `[logué]` HRESULT CreateForMonitor (Verbose si succès, Error si échec)
- `[logué]` compte total de frames au Stop
- `[logué]` durée de session
- `[logué]` FPS moyen sur la session (frames / durée)
- `[logué]` FrameSampler init : taille grille (cols×rows), niveau de mip cible, mode tone-map (`none` ou `scrgb_to_srgb`)
- `[logué]` FrameSampler map failure (HRESULT) — Warning si lecture staging texture échoue
- `[à instrumenter]` taille frame courante au resize (resize en cours d'exécution si l'écran change)
- `[à instrumenter]` FPS instantané (fenêtre glissante 1 s) — émis par le consumer Playground côté UI, pas dans le service
- `[à instrumenter]` cadence push réelle vs cible (mesure à valider quand le pipeline tourne longtemps)

**Gabarit standard**

```
Info      SCREEN  Screen capture starting
Verbose   SCREEN  start | hmon=0x{X} | size={W}x{H} | format={pixelFormat} | hdr={on|off} | peak_lum={X:F0} | bufs={n} | min_update_ms={n}
Success   SCREEN  Screen capture started
Verbose   SCREEN  sampler init | grid={W}x{H} | mip={n} | tone_map={none|scrgb_to_srgb} | peak_lum={X:F0}
Warning   SCREEN  sampler map fail | hr=0x{X}
Info      SCREEN  Screen capture stopped ({frames} frames in {duration_sec:F1} s)
Verbose   SCREEN  stop | frames={n} | duration_ms={X} | fps_avg={X:F1}
```

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback | Notes |
|---|---|---|---|
| `GraphicsCaptureSession.IsSupported()` false | Error | ➕ Critical (vague Ambient) | OS trop ancien (< Windows 10 1809) |
| D3D11CreateDevice échoue (HRESULT) | Error | ➕ Critical (vague Ambient) | Pas de GPU compatible / driver KO |
| `MonitorFromPoint` retourne `IntPtr.Zero` | Error | ➕ Critical (vague Ambient) | Pas d'écran primaire détecté (cas pathologique) |
| `CreateForMonitor` échoue (HRESULT) | Error | ➕ Critical (vague Ambient) | API capture refuse le moniteur |
| `FrameArrived` callback exception | Warning | non (frame ignorée, session continue) | logged Verbose avec stack |
| `MinUpdateInterval` setter exception | Verbose | non | pré-Win11 22H2 ignore le setter — pipeline tourne juste au native rate |
| Resize en cours (`ContentSize` change) | Verbose | non | non-traité en J3 step 2, log seul |
| `FrameSampler.Map` HRESULT non-zero | Warning | non (frame ignorée) | typique : driver flush en cours, frame suivante retry |
| `FrameSampler.Process` exception générique | Warning | non (frame ignorée) | catch-all autour du chemin D3D11 |

**Vocabulaire de l'entête start**

- `size={W}x{H}` — résolution du moniteur source en pixels (entier)
- `format={enum}` — `B8G8R8A8UIntNormalized` (SDR) ou `R16G16B16A16Float` (HDR)
- `hdr={on|off}` — état de la sortie HDR du moniteur primaire au moment du Start
- `peak_lum={X:F0}` — peak luminance en nits (1 décimale arrondie ; 80 par défaut SDR)
- `bufs={n}` — taille du pool de frames (2 par défaut)
- `min_update_ms={n}` — cadence cible côté session (66 ms = 15 Hz, Windows throttle `FrameArrived` au niveau de `GraphicsCaptureSession.MinUpdateInterval`)

**Vocabulaire FrameSampler**

- `grid={W}x{H}` — dimensions de la grille échantillonnée (≈ 30×17 par défaut, varie selon l'aspect ratio source)
- `mip={n}` — niveau de mip choisi côté GPU pour atteindre la grille cible (typ. 6-7)
- `tone_map={enum}` — `none` (SDR BGRA8 path) ou `scrgb_to_srgb` (HDR FP16 → 8-bit sRGB)

**Threading**

Le `FrameArrived` event est levé sur le worker thread interne du `Direct3D11CaptureFramePool` (parce qu'on utilise `CreateFreeThreaded`, pas `Create`). Le service relaye l'event vers ses consommateurs sur ce même thread — c'est au consommateur de marshaler vers son thread UI s'il en a besoin (cas typique : le panneau Playground utilise `DispatcherQueue.TryEnqueue` pour rafraîchir les labels).

**Disposable**

`ScreenCaptureService` est `IDisposable`. `Dispose()` est idempotent et appelle `Stop()` si la session est encore active. Le consommateur est responsable du `Dispose()` quand il n'a plus besoin du service — typiquement à la fermeture de la fenêtre Playground ou à l'arrêt explicite de l'utilisateur.

### 12. Driver Hue (REST direct)

**Fichier** : `src/Deckle.Lighting/Hue/HueDiscovery.cs` (cloud lookup `discovery.meethue.com`), `src/Deckle.Lighting/Hue/HueBridgeClient.cs` (HTTPS bypass cert + pairing CLIP v1 + control), `src/Deckle.Lighting/Hue/HueRestLightOutput.cs` (implémentation `ILightOutput`).

J2 emprunte délibérément la voie REST CLIP v1 plafonnée à ~10-20 Hz, pas la voie Entertainment v2 DTLS-PSK (cf. `docs/research--hue-entertainment-v2--2026-05-15.md`). Le pipeline reste 100 % C#, zéro dépendance native, zéro NuGet tier — la cadence est suffisante pour un mode ambient avec smoothing au-dessus. La voie Entertainment v2 est archivée pour plus tard si la perception le justifie ; le swap se fera derrière l'abstraction `ILightOutput` sans toucher au reste du pipeline.

**Mesures disponibles**

- `[logué]` lookup discovery cloud start + N bridges retournés
- `[logué]` bridge_id + bridge_ip pour chaque bridge découvert
- `[logué]` pairing start + waiting tick (1 / sec pendant 30 s)
- `[logué]` pairing success : bridge_id, username tronqué (8 chars + `...`), clientkey `[redacted]`
- `[logué]` pairing failure : timeout (30 s écoulées sans pression) ou refus bridge
- `[logué]` groups list start + N groups + leur id/name
- `[logué]` set colour : group_id + rgb d'origine + xy + bri calculés
- `[à instrumenter]` cadence réelle d'envoi (push/s) quand le driver tournera derrière le pipeline J3
- `[à instrumenter]` round-trip latency (mesure ping-pong sur PUT) pour diagnostic bridge surchargé

**Gabarit standard**

```
Info      HUE  Looking up Hue bridges
Verbose   HUE  discover start | source=cloud | url=https://discovery.meethue.com/
Success   HUE  Found {N} Hue bridge(s)
Verbose   HUE  discover result | bridge_id={id} | bridge_ip={ip}
Info      HUE  Pairing started — press the link button on the bridge
Verbose   HUE  pair start | bridge_ip={ip} | timeout_sec=30
Success   HUE  Bridge paired ({bridge_id})
Verbose   HUE  pair result | bridge_id={id} | username={head8}... | clientkey=[redacted]
Info      HUE  Listing groups
Verbose   HUE  groups list | bridge_id={id} | count={N}
Info      HUE  Listing lights in group {id}
Verbose   HUE  lights list | group_id={id} | count={N}
Verbose   HUE  light | id={id} | name={n} | type={t} | reachable={true|false}
Info      HUE  Listing entertainment configurations
Verbose   HUE  entertainment list | count={N}
Verbose   HUE  entertainment | id={id} | name={n} | lights={N}
Verbose   HUE  placement | ent_id={id} | light_id={id} | x={X:F3} | y={Y:F3} | z={Z:F3}
Verbose   HUE  light identify | light_id={id} | alert={lselect|none} | phase={start|stop}
Verbose   HUE  push colour | group_id={id} | rgb={r},{g},{b} | xy={x:F4},{y:F4} | bri={bri} | tt_ds={n}
Verbose   HUE  push colour | group_id={id} | rgb={r},{g},{b} | on=false | tt_ds={n}
Verbose   HUE  push colour | light_id={id} | rgb={r},{g},{b} | xy={x:F4},{y:F4} | bri={bri} | tt_ds={n}
Verbose   HUE  push colour | light_id={id} | rgb={r},{g},{b} | on=false | tt_ds={n}
Warning   HUE  Set colour failed | group_id={id} | hr={status}
Warning   HUE  Set colour failed | light_id={id} | hr={status}
Warning   HUE  Identify {start|stop} failed | light_id={id} | hr={status}
Warning   HUE  CLIP v2 GET failed | path={path} | hr={status}
```

The `entertainment list` / `entertainment` / `placement` triple comes out of `ListEntertainmentConfigurationsAsync` (CLIP v2 GET /resource/entertainment_configuration) which we use only to recover Hue's stored XYZ positions per light. The XYZ feeds the `LightZoneSuggester` in `Deckle.Lighting.Ambient` so the Light zones UI can pre-fill the per-light zone ComboBox instead of asking the user to map them by hand. `light identify` is the bridge-side flash (alert=lselect, ~15 s) used by the [Identify] button in the same UI.

`tt_ds` is the Hue `transitiontime` parameter in deciseconds (1 = 100 ms). Forced to 1 by the ambient driver to override the bridge factory default of 4 (= 400 ms), which would lag the lamp behind the screen on every push.

The `light_id=` variant of `push colour` and `Set colour failed` is emitted by the multi-light path (one PUT /lights/{id}/state per fixture). The `group_id=` variant is emitted by the single-colour group path (one PUT /groups/{id}/action covering all lights in the group). Same payload schema on the wire, only the target tag differs in the log.

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback | Notes |
|---|---|---|---|
| Cloud discovery timeout / DNS fail | Warning | non (fallback manual IP) | retry possible |
| Cloud discovery retourne 0 bridge | Info | non (mais surface dans Playground) | utilisateur entre IP manuellement |
| Bridge IP injoignable (TCP refused / timeout) | Error | ➕ Critical (vague Ambient) | bridge éteint ou IP fausse |
| Bridge cert auto-signé refus (avant bypass) | n/a | n/a | bypass configuré en dur, ne devrait pas remonter |
| Pair `error 101` (link not pressed) | Verbose | non (status text mis à jour) | normal pendant les 30 s d'attente |
| Pair timeout 30 s | Warning | ➕ Warning (vague Ambient) | utilisateur a oublié de presser |
| Pair bridge refus autre que 101 | Error | ➕ Critical (vague Ambient) | rare (bridge saturé d'apps connues) |
| HTTP 401/403 sur control | Error | ➕ Critical (vague Ambient) | username révoqué côté bridge |
| HTTP 5xx sur control | Warning | non (push dropped, session continue) | transient |
| Group id sélectionné disparait | Warning | ➕ Warning (vague Ambient) | groupe supprimé côté Hue app |

**Threading**

`HueBridgeClient` expose des méthodes `async Task<...>`. Discovery + pairing + control sont async sur le pool d'I/O .NET — pas de marshalling UI nécessaire côté driver. Le consommateur (Playground ou plus tard l'`AmbientEngine`) appelle ces méthodes depuis n'importe quel thread, en attendant le résultat ; la couche UI marshale avec `DispatcherQueue.TryEnqueue` quand elle reflète l'état dans des controls XAML.

**Secret**

Le `clientkey` retourné par le bridge au pairing est une PSK qui servira au tunnel DTLS Entertainment v2 si jamais on l'active. Il est traité comme un secret : jamais affiché en clair dans la LogWindow (ni Verbose ni autre niveau), jamais persisté en JSON non chiffré sans avertissement. Le `username` (= application key REST) est moins sensible mais reste tronqué à 8 chars + `...` dans les logs pour minimiser l'exposition dans des screenshots de support.

**Disposable**

`HueRestLightOutput` est `IAsyncDisposable`. La fermeture libère l'`HttpClient` interne ; le `username` reste valide côté bridge tant que l'utilisateur ne le révoque pas manuellement via l'app Hue. La rétention du `username` entre sessions est une décision du consommateur (Playground = transient, AmbientSettings = persistant).

### 13. AmbientEngine (pipeline orchestration)

**Fichier** : `src/Deckle.Lighting.Ambient/AmbientEngine.cs`.

L'engine est le hub qui relie l'amont (capture écran via `Deckle.Vision.ScreenCaptureService` + analyse via `Deckle.Vision.FrameSampler`) à l'aval (driver LED via `Deckle.Lighting.ILightOutput`). Il ne sait pas avec quel matériel il parle — l'`ILightOutput` est agnostique — et il ne fait pas d'analyse pixel par lui-même : `FrameSampler` produit en permanence un `SampledFrame.Average` que la boucle de l'engine consomme. En J3 step 2 l'engine ne propose qu'un seul mode (`analysis`) ; le mock HSV de J3 step 1 est retiré, le test isolation bridge passe désormais par les boutons Red/Green/Blue/White/Off du Playground.

**Mesures disponibles**

- `[logué]` start / stop de la session (Info + Verbose miroir avec config)
- `[logué]` config sampler au start (grille, état HDR)
- `[logué]` couleur poussée à chaque tick (Verbose, mode + RGB + flag dropped)
- `[logué]` total `pushed` / `dropped` à la fin de session (Verbose stop)
- `[à instrumenter]` latence push (entre frame analysée et SetColorAsync renvoyé)
- `[à instrumenter]` ΔRGB mesuré (utile pour calibrer le seuil early-exit en J5)

**Gabarit standard**

```
Info      AMBIENT  Ambient pipeline started
Verbose   AMBIENT  start | source={capture status} | output={driver type} | shape={group|multi} | lights={N} | push_hz={N} | sampler_grid={W}x{H} | hdr={on|off}
Info      AMBIENT  Ambient pipeline stopped
Verbose   AMBIENT  stop | reason={user|cancelled|disposed} | shape={group|multi} | duration_sec={X:F1} | pushed={n} | dropped={n}
Verbose   AMBIENT  push | mode=group | rgb={r},{g},{b} | off={true|false}
Verbose   AMBIENT  push | mode=multi | lights={K}/{N} | colors={lightId=R,G,B …}
Verbose   AMBIENT  heartbeat | mode={group|multi} | period_sec={X:F1} | ticks={N} | pushed={N} | dropped={N} [| unmapped_lights={N}]
Info      AMBIENT  Zone {None|Top|Bottom|Left|Right} assigned to {lightName}
Info      AMBIENT  Zone cleared on {lightName}
Verbose   AMBIENT  zone assign | id={lightId} | zone={None|Top|Bottom|Left|Right}
Verbose   AMBIENT  zone suggest | id={lightId} | zone={…} | from=ent_config | ent_name={n} | xyz={X:F2},{Y:F2},{Z:F2}
Verbose   AMBIENT  zone suggest skipped | reason={no_entertainment_areas|no_matching_entertainment_area}
Info      AMBIENT  Pipeline mode set to {group|per-zone}
Verbose   AMBIENT  settings update | key={name} | value={v}
Warning   AMBIENT  Multi-light requested but driver doesn't expose IMultiLightOutput ({type}) — falling back to group push
Warning   AMBIENT  Multi-light requested but driver returned no lights — falling back to group push
Warning   AMBIENT  Multi-light push failed — {ExType}: {message}
```

**Vocabulaire AmbientEngine**

- `source={capture status}` — `running` ou `stopped` au moment du start (l'engine démarre la capture si stopped)
- `output={driver type}` — nom de la classe ILightOutput (typ. `HueRestLightOutput`)
- `shape={group|multi}` — pipeline résolu au start : `group` = single-colour push au group, `multi` = per-light push via `IMultiLightOutput`. Choix gouverné par `AmbientSettings.UseMultiLight` + capability driver + non-vide `MultiLights`
- `lights={N}` — nombre de lights résolues par le driver en mode multi (0 en mode group)
- `push_hz={N}` — cadence cible de la boucle push (15 en mode group, 10 en mode multi pour rester dans la zone de confort du bridge à N PUT en parallèle)
- `sampler_grid={W}x{H}` — dimensions de la grille du FrameSampler au moment du start
- `hdr={on|off}` — état HDR négocié par la capture (miroir du SCREEN log)
- `mode={group|multi}` — pipeline shape sur la ligne `push` (miroir de `shape=` au start)
- `lights={K}/{N}` — sur une ligne `push mode=multi`, K = nombre de lights effectivement pushées au tick (i.e. ayant changé de couleur), N = total
- `colors={id=R,G,B …}` — sur une ligne `push mode=multi`, la composition exacte du batch envoyé : par light id, le sRGB pushé. Les lights non listées sont soit unmapped, soit ont passé l'early-exit ; le détail s'agrège dans le heartbeat suivant
- `off={true|false}` — true si la couleur de zone était sous le seuil "lights-out" et a été clampée à (0,0,0) avant push (la bridge convertit en `on:false`)
- `pushed`/`dropped` au stop — compteurs cumulés sur la session

**Cadence de log et heartbeat.** Avant le ramatonement des logs, la ligne `push` sortait à chaque tick (10-15 Hz). Sur un écran statique, 300 lignes identiques en 30 s pour rien. Le nouveau pattern :

1. **Une ligne `push` n'est émise que lorsqu'un push effectif a lieu** — c'est-à-dire quand au moins une couleur change. Sur écran statique, zéro ligne `push`. Sur contenu dynamique, le débit suit le rythme des changements perceptibles.
2. **Un `heartbeat` toutes les 5 s** consolide les ticks intermédiaires : nombre de ticks, push effectués, dropped (couleur identique au précédent push donc skipped), et en multi-mode `unmapped_lights` (somme des lights mappées à `None` ou absentes du dict, par tick × N ticks). Le heartbeat sort même quand rien ne pousse, ce qui prouve que la boucle tourne.
3. **Cumulés au stop** (déjà documentés plus bas) restent la vérité de session.

Ce pattern suit la doctrine VAD : on ne loge pas chaque cycle, on loge quand le sujet change + résumé périodique. Les compteurs internes de l'engine (`_hbTicks`, `_hbPushed`, `_hbDropped`, `_hbUnmappedLights`) sont remis à zéro à chaque heartbeat émis, donc chaque ligne `heartbeat` lit "depuis le dernier heartbeat" et pas "depuis le start".
- `Zone {…} assigned to {lightName}` / `Zone cleared on {lightName}` — pair Info Capital sémantique pour l'action utilisateur dans la card Light zones. Ne porte aucun ID, lisible dans Activity. Toujours visible quel que soit l'état de Log ambient capture activity (sort hors boucle de capture)
- `zone assign | id={lightId} | zone={…}` — miroir Verbose technique de l'Info ci-dessus. `id` = light id opaque (CLIP v1 pour Hue), `zone` = valeur de l'enum `LightZone`. `zone=None` correspond à un retrait de mapping (l'entrée est supprimée du dict). Verbose car porte l'ID — la doctrine impose les IDs en Verbose uniquement
- `zone suggest` — émis au moment où la card Light zones se construit, **une ligne par lampe** dont la position dans l'entertainment area Hue a permis de dériver une zone via `LightZoneSuggester`. `from=ent_config` indique la source de la suggestion (anticipation : `from=room` ou `from=ha_area` plus tard). `xyz` reproduit la position Hue normalisée [-1, 1] qui a été classée. La suggestion est ensuite persistée dans `AmbientSettings.LightZones` (et `zone assign` ne sera PAS émis pour cette même opération — c'est une init silencieuse, pas un acte utilisateur)
- `zone suggest skipped` — émis quand la card Light zones s'est construite sans suggestion possible. `reason=no_entertainment_areas` = le bridge n'expose aucune entertainment area ; `reason=no_matching_entertainment_area` = au moins une area existe mais aucune ne contient les lights du group sélectionné
- `Pipeline mode set to {group|per-zone}` — Info Capital sémantique émis par le RadioButtons UseMultiLight du Playground. Phrase user-facing, pas d'ID, pas de k=v. Sort hors boucle de capture donc toujours visible
- `settings update | key={name} | value={v}` — miroir Verbose technique. Gabarit générique pour toute modification d'une property d'`AmbientSettings` depuis l'UI (UseMultiLight pour V0, d'autres champs à venir en J5/J9). Verbose car le format `k=v` + identifiants techniques (nom de property) relèvent du diag expert

**Erreurs et sévérités**

| Condition | Sévérité | UserFeedback | Notes |
|---|---|---|---|
| Output `ConnectAsync` échoue au start | Error | ➕ Critical (vague Ambient) | bridge déco / token KO |
| `SetColorAsync` throw (transient HTTP) | Warning | non (push dropped) | retry au tick suivant |
| Analysis loop crash | Error | ➕ Critical (vague Ambient) | engine s'arrête, statut UI mis à jour |

**Threading**

L'engine démarre une boucle async (`Task.Run`) qui lit `_sampler.LatestSample` (volatile read), évalue l'early-exit, et appelle `SetColorAsync` si pertinent. La capture (`FrameArrived` sur worker thread) alimente le sampler en parallèle ; la synchronisation est portée par `Volatile.Read/Write` côté `_latestSample` — pas de lock partagé entre la boucle push et la boucle capture.

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
| 15 | OS too old (no `GraphicsCaptureSession`) | Screen capture | Error | ➕ Critical (vague Ambient) |
| 16 | D3D11 device creation fails | Screen capture | Error | ➕ Critical (vague Ambient) |
| 17 | `CreateForMonitor` fails (HRESULT) | Screen capture | Error | ➕ Critical (vague Ambient) |

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
7. **La source porte le contexte.** Ne pas écrire "CAPTURE: started
   recording..." dans le message : la colonne Source affiche déjà `CAPTURE`.

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
