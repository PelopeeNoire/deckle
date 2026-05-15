# CLAUDE.md — Deckle.Audio

Module de capture audio. Le périmètre actuel est 100 % microphone : capture WASAPI via `waveInOpen`, polling sur quatre buffers circulaires de 50 ms, conversion PCM16 → float, calcul RMS en temps réel pour alimentation du HUD, télémétrie tail RMS au Stop pour détection de mic débranché ou audio bas, auto-calibration heuristique sur les N dernières sessions. Le module est aussi propriétaire de l'`AudioLevelMapper` (statiques mutables qui contrôlent la courbe dBFS → niveau perceptuel) — l'app et le HUD lisent ces statiques à chaque vsync pour rendre l'animation du chrono.

Le nom du module est volontairement plus large que son contenu actuel. Le module a été renommé `Deckle.Capture → Deckle.Audio` en mai 2026 parce que `Capture` était un faux générique qui n'avait aucune primitive partageable avec la capture vidéo (le futur module `Deckle.Vision`). Le nom `Audio` reste correct si on ajoute plus tard une capture loopback (output PC pour triggers audio dans l'ambient lighting), une sortie audio (TTS de confirmation, monitoring), ou tout autre traitement de signal audio. Les classes internes ont gardé leurs noms historiques (`CaptureSettings`, `CaptureResult`, `MicrophoneCapture`, `CaptureSettingsService`) — l'API en consommation devient `Deckle.Audio.CaptureSettings` qui se lit comme « settings de capture audio dans le module Audio ».

## Contrat avec les consommateurs

Le module expose `MicrophoneCapture` (orchestrateur de cycle de vie : `Probe()` pour pré-vol, `Record(IAudioRecordingHost, CancellationToken)` pour la séance), `IAudioRecordingHost` (contrat injecté par l'orchestrateur — Whisp typiquement — qui expose les settings live consultés à chaque entrée de `Record()`), `CaptureResult` (audio float[] + télémétrie micro + outcome), `CaptureSettings` + `CaptureSettingsService` (UI Settings → Recording page + auto-load), `AudioLevelMapper` (mappage RMS → niveau perceptuel utilisé par `Deckle.Chrono.Hud`).

Le pattern fondamental : le module ne sait pas pourquoi on capture (transcription, futur Ask-Ollama, autre). Il sait juste comment capturer proprement et comment exposer la télémétrie nécessaire pour calibrer l'expérience utilisateur. Les consommateurs implémentent `IAudioRecordingHost` pour fournir le device id, le cap durée et le toggle télémétrie, et reçoivent un `CaptureResult` complet à la sortie de `Record()`.

## Caractéristiques de la capture

Format unique non paramétrable : 16 kHz, mono, PCM16. C'est le format attendu par Whisper et il reste valide pour les futurs usages (les SLM audio comme Voxtral utilisent aussi cette résolution). Quatre buffers circulaires de 50 ms en polling pur (pas de queue managée, pas d'event-driven). La taille de la fenêtre RMS pour le mapping HUD est paramétrable côté `CaptureSettings.LevelWindow` (RMS over a sliding window of N samples) mais la cadence de polling reste 50 ms.

Le RMS de chaque sous-fenêtre est émis en event temps réel pour alimenter l'animation HUD via `AudioLevelMapper`. La courbe `dBFS → [0, 1]` est définie par trois statiques (`MinDbfs`, `MaxDbfs`, `DbfsCurveExponent`) — l'app les pousse à chaque changement de setting via `App.ApplyLevelWindow(...)`. Ces statiques sont mutables à dessein pour la calibration runtime depuis le Playground.

À la fin du `Record()`, `MicrophoneTelemetryCalculator` calcule un récap distributif (p10, p25, p50, p75, p90, peak) sur toute la session plus un tail RMS sur les 600 derniers ms (utilisé pour détecter un mic débranché ou un audio très bas). `MicrophoneCalibrationCalculator` ajuste les bornes dBFS sur les N dernières sessions (médiane des p10 → MinDbfs, médiane des p90 + 2 dB → MaxDbfs) pour que la courbe perceptuelle reste adaptée à l'environnement réel de l'utilisateur.

## Télémétrie

Les mesures audio sont structurées dans `MicrophoneTelemetryPayload` (canal `Microphone` de `TelemetryService.Instance`) avec un gating user-settable (toggle « Log microphone » dans Settings → Telemetry). Le vocabulaire et les unités suivent l'inventaire normatif documenté dans [docs/reference--logging-inventory--1.0.md](../../docs/reference--logging-inventory--1.0.md), section « Enregistrement audio ». Toute nouvelle mesure passe par l'inventaire avant d'apparaître dans le code — voir le CLAUDE.md de `Deckle.Logging` pour la discipline complète.

## Persistance

`CaptureSettingsService` est un singleton lazy qui charge et persiste les settings sous `<UserDataRoot>/modules/audio/settings.json` via `JsonSettingsStore<CaptureSettings>`. Les anciens utilisateurs qui ont leur fichier sous `modules/capture/` sont migrés automatiquement au premier boot par `SettingsBootstrap.MigrateModuleFolder("capture", "audio")` (idempotent : no-op si la cible existe déjà). Le mutex nommé pour la synchro multi-process est `Deckle-Settings-Audio-Save`. Le prefix de log dans `LogSource.Settings` est `[audio]`.
