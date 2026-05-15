# CLAUDE.md — Deckle.Whisp

Module de transcription vocale via whisper.cpp. Couvre tout le pipeline du hotkey à l'écriture clipboard final : ouverture du runtime natif, chargement du modèle Whisper, capture audio (déléguée à `Deckle.Audio`), VAD, transcription monobloc avec callback par segment, filtrage de répétitions, optionnellement réécriture LLM (déléguée à `Deckle.Llm`), écriture clipboard, paste optionnel. Le module possède aussi son UI Settings (`WhisperPage.xaml`) et son setup first-run (téléchargement des natives et des modèles).

Le contrat avec l'app hôte passe par `IWhispEngineHost` — interface bridge qui expose les settings utiles côté engine sans coupler `Deckle.Whisp` à `Deckle.Settings`. L'app implémente `AppWhispEngineHost` dans `src/Deckle/Engine/`. La transcription en elle-même est invoquée via `_engine.RequestToggle(...)` depuis le handler de hotkey.

## Pipeline transcription monobloc

Le pipeline tourne en un seul appel `whisper_full()` avec `new_segment_callback` qui pousse chaque segment au fil de l'eau dans `OnNewSegment`. Pas de chunking externe : whisper.cpp gère sa fenêtre interne, le VAD coupe les silences en amont, et nous récupérons les segments dès qu'ils sont prêts plutôt que de les attendre tous. Les détails (séquence d'appels, gestion des paramètres, callback par segment avec instrumentation, hot-reload via `SettingsService`) vivent dans [docs/reference--pipeline-transcription--1.0.md](../../docs/reference--pipeline-transcription--1.0.md).

Piège connu sur les paramètres whisper.cpp : `entropy_thold` est inversé dans la doc officielle par rapport au code. Les defaults du wrapper Deckle ont été restaurés au comportement réel de whisper.cpp (voir la fiche référence ci-dessus). Toute proposition de retouner les seuils doit relire la fiche avant de toucher au code.

## Native runtime

Le module dépend de `libwhisper.dll` et des backends ggml (Vulkan en priorité, CPU en fallback). Les DLLs ne sont pas embarquées dans le repo — elles sont téléchargées au first-run depuis la release GitHub `native-vX.Y.Z` du repo Deckle ou recompilées localement par le maintainer quand un upgrade upstream est nécessaire. La recette de recompilation, les chemins, et l'inventaire des fichiers attendus vivent dans [docs/reference--native-runtime--1.0.md](../../docs/reference--native-runtime--1.0.md). Le code de bootstrap est dans `Setup/NativeRuntime.cs`.

## Règles UX non négociables

### Clipboard — 2 états maximum par transcription

Le clipboard porte au plus deux contenus successifs sur la durée d'une transcription : la transcription brute Whisper, puis le texte réécrit par le LLM si un profil est actif. **Jamais d'accumulation token par token, jamais d'incréments mot par mot.** L'historique du presse-papier système doit rester propre — un utilisateur qui ouvre l'historique clipboard après une transcription voit `raw` et `rewrite`, pas `raw1`, `raw1+w2`, `raw1+w2+w3`. Conséquence pour un éventuel streaming LLM : si on stream, on remplace l'objet clipboard en place (ou on le supprime puis on ré-ajoute), pas d'append. La granularité acceptable est la phrase entière (sur détection de point) ou un intervalle régulier d'environ 5 s, jamais token par token. Cette règle prime sur le gain de latence perçue.

### Pré-chargement VAD au hotkey (idée notée, pas implémentée)

Le VAD de whisper.cpp prend environ 5 % du temps audio quel que soit le backend GPU ou CPU — confirmé sur plus de 700 runs de télémétrie. Une piste plausible serait de pré-charger le contexte VAD dès la réception du hotkey (avant même le `waveInStart`) et de libérer au stop. À tester avec mesure avant et après une fois l'instrumentation correcte en place. Pas implémenté à ce jour, noté ici pour ne pas l'oublier.

## Paste

Le paste automatique est désactivé par défaut côté settings — la valeur par défaut est `false`, le HUD montre toujours `Copied to clipboard` en fallback quand l'utilisateur n'a pas explicitement opté pour le paste. Quand le paste est activé, le pipeline re-capture la fenêtre cible au Stop avec un filet PID (le HWND foreground peut avoir changé entre Start et Stop sur des sessions longues), rendez-vous synchrone via `ManualResetEventSlim` pour `HideSync` avant le `SendInput Ctrl+V`, et plusieurs refus explicites de `PasteFromClipboard` quand l'UIA n'est pas sûre que la cible accepte le texte. Les détails de la mécanique, le bug paste fantôme intermittent encore en investigation, et les cinq refus actuellement loggués Warning vivent dans [docs/reference--paste-behavior--1.0.md](../../docs/reference--paste-behavior--1.0.md).

## Structure interne

Le module suit le pattern canonique des modules Deckle.

`WhispSettings.cs` est le POCO de configuration (sept sections imbriquées : transcription, speech detection, prompt, paste, autorewrite rules, models directory, level window). `WhispSettingsService.cs` est le singleton lazy qui charge et persiste les settings sous `<UserDataRoot>/modules/whisp/settings.json` via `JsonSettingsStore<T>`. `IWhispEngineHost.cs` est l'interface bridge exposée aux consommateurs (l'app implémente `AppWhispEngineHost`). `WhisperPage.xaml(.cs)` et `ViewModels/WhisperViewModel.cs` portent l'UI Settings du module (rendu via `Type.GetType` depuis le NavView de `SettingsWindow`). Le dossier `Engine/` héberge le moteur principal (`WhispEngine.cs`) et ses helpers (`RepetitionDetector.cs`, `WhisperParamsMapper.cs`). Le dossier `Pinvoke/` contient les wrappers `[LibraryImport]` autour de whisper.cpp natif (`WhisperPInvoke.cs`, `WhisperStructs.cs`). Le dossier `Setup/` héberge le first-run provisioning (`NativeRuntime.cs`, `SpeechModels.cs`, `Downloader.cs`, `SetupContext.cs`). Le dossier `Strings/en-US/` porte les ressources `.resw` pour les `x:Uid` de `WhisperPage`.
