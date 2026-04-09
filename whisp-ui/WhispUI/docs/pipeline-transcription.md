# Pipeline de transcription — monobloc

## Refonte monobloc

Plus de chunking externe 30 s. `Record()` accumule tout l'audio capturé dans un unique `List<byte>` et retourne un `float[]` au Stop. `Transcribe(float[])` fait **un seul** appel `whisper_full()` — Whisper gère son propre fenêtrage interne (30 s + seek dynamique) et la propagation de contexte inter-fenêtres via tokens.

La récupération progressive passe par `new_segment_callback` (binding du champ `WhisperFullParams.new_segment_callback` via `Marshal.GetFunctionPointerForDelegate` ; délégué stocké en champ d'instance `_newSegmentCallback` pour échapper au GC pendant l'appel natif).

Chaque segment est poussé sous lock dans `List<TranscribedSegment> _segments` (`Text` / `T0` / `T1` / `NoSpeechProb`), depuis le thread d'inférence de whisper.cpp. Le texte final est assemblé à partir de cette liste — garantit qu'un segment loggé est exactement un segment du texte produit.

Un seul thread worker `Record → Transcribe` au lieu de deux threads parallèles (plus de `BlockingCollection`). `MatchHallucination` et la mémoire `initial_prompt` chunk-par-chunk supprimées. `LooksRepeated` conservée en log-only sur le texte complet.

## Instrumentation par segment — tuning hallucinations

Logs Verbose enrichis avec `p̄`, `min`, `dur`, `gap` filtrés sur les seuls tokens texte (via `whisper_token_beg`). Patterns d'hallucinations identifiables visuellement :

- **Boucle** — `dur=3,0s gap=+0,0s` métronomique avec texte identique répété.
- **Hallucination de silence** — gros `gap` + `p̄ < 0,5` sur le 1er segment.
- **Saut Whisper** — gros `gap` isolé.

`nsp` inutile sur dictation (toujours 0 %). `min` seul inutilisable comme discriminant (chute aussi sur parole saine).

## Defaults whisper.cpp restaurés

Suppression des overrides `entropy_thold=1,9` et `no_speech_thold=0,7` (héritage chunking, plus d'actualité). Le fallback natif est désormais actif : `temperature=0,0 / temperature_inc=0,2 / logprob_thold=-1,0 / entropy_thold=2,4`. Re-décode automatiquement les segments ratés à température croissante jusqu'à ≤1,0.

**Piège `entropy_thold` — contre-intuitif** : test interne = `entropy < seuil`, donc seuil HAUT = STRICT (déclenche fallback plus souvent), seuil BAS = PERMISSIF. L'ancien override 1,9 était donc **plus permissif** que le défaut 2,4, à l'inverse de ce qu'on croyait initialement. Documenté en commentaire dans `Transcribe()`.

## Hot-reload via SettingsService

`Transcribe()` reconstruit ses `WhisperFullParams` à chaque appel via `whisper_full_default_params_by_ref` — snapshot `SettingsService.Instance.Current` en début d'appel pour hot-reload gratuit, sans re-init modèle. Piège thread safety : l'approche snapshot immutable au début de `Transcribe` suffit.

Voir `settings.md` pour le câblage WhisperPage → AppSettings → WhisperParamsMapper.

## Cartographie logs actuelle

- **MODEL** — path / taille / use_gpu, Step au succès, Warning si fichier absent.
- **HOTKEY** — `DescribeHwnd` cible + Warning si pas de focus clavier.
- **CLIPBOARD** — méthode d'instance, instrumentation `GlobalAlloc` / `OpenClipboard` / `SetClipboardData` + re-lecture de vérification post-copie.
- **PASTE** — voir `paste.md`.
- **LLM** — callbacks `onWarn` / `onStep` / `onInfo`, fallback détaillé avec type d'exception.
- Helper `Win32Util.DescribeHwnd` (exe / titre / classe focusée).

**À refaire post-pipeline-monobloc** : les événements historiques `Chunk N extrait`, `Mémoire passée au chunk suivant`, `Chunk N → texte recollé` n'existent plus. Nouvelle réalité : `RECORD` heartbeat 5 s + capture terminée, `TRANSCRIBE` audio reçu + un Verbose par segment via callback + récap final. Repasser sur les niveaux (Verbose / Info / Step) une fois que le nouvel usage aura révélé ce qui est utile.

## Tâches ouvertes

- **Paste "fantôme" intermittent** — voir `paste.md`.
- **Filtrage par segment + seuils Whisper** — plus de filtrage textuel par patterns ; on s'appuie sur `entropy_thold=2,4` (défaut) et les seuils natifs. À valider en usage réel. Sinon, brancher un filtre **par segment** basé sur `no_speech_prob` (déjà accessible via `_segments`) plutôt que rejeter tout le texte.
- **Bugs connus restants** : hallucinations sur silences longs / musique en fond (`no_speech_thold`, `suppress_blank`) ; ponctuation manquante si stop net (~300 ms de silence PCM en fin de buffer) ; screensaver casse l'enregistrement (`SetThreadExecutionState`).
- **Instrumentation niveau voix** — RMS sur PCM16 dans `Record()`, event `AudioLevel(float rms)`, affiché dans LogWindow (niveau instantané + moyenne). But : ajuster la gate du micro, savoir si le marmonnement passe. Prérequis pour le contour animé HUD.
- **VAD Silero** — intégration si `libwhisper.dll` a été compilée avec support VAD.
