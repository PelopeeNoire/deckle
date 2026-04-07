# CLAUDE.md — WhispInteropTest (WinForms)

Application de transcription vocale locale. WinForms tray-only + whisper.cpp via P/Invoke.
Statut : **fonctionnel, en maintenance**. Le futur point d'entrée sera WhispUI (WinUI 3).

---

## Structure des fichiers

```
WhispInteropTest\
├── Program.cs          — logique principale (~600 lignes)
├── WhispForm.Pinvoke.cs — tous les [DllImport] et signatures P/Invoke
├── Structs.cs          — structs P/Invoke (WAVEHDR, INPUT, GUITHREADINFO, etc.)
├── DebugForm.cs        — fenêtre de logs en temps réel
└── LlmService.cs       — appel Ollama (réécriture LLM)
```

---

## Hotkeys enregistrés

| Combo | ID | Comportement |
|---|---|---|
| Alt+` | 1 | Transcription + collage auto |
| Alt+Ctrl+` | 3 | Transcription + réécriture LLM + collage auto |

---

## Pipeline audio → transcription

1. Hotkey 1 → `StartRecording()` → capture waveIn PCM 16kHz mono
2. Chunks de 30 s poussés dans `_pipeline` (BlockingCollection)
3. Thread `Transcribe()` consomme le pipeline → `whisper_full()` → filtre hallucinations → accumule
4. Hotkey 2 → `_stopRecording = true` → pipeline terminé → clipboard → paste/LLM selon flags

---

## Acquis techniques WinForms / P/Invoke

- `[StructLayout(LayoutKind.Sequential)]` + `byte` pour les `bool` C → layout mémoire identique à GCC
- Pattern `_by_ref` + `Marshal.PtrToStructure` : la DLL alloue, on copie, on libère
- `Marshal.StringToHGlobalAnsi` / `FreeHGlobal` : chaînes C en mémoire non managée
- `Marshal.PtrToStringUTF8` (pas `Ansi`) pour les retours texte de whisper
- Buffers WAVEHDR en mémoire non managée (`AllocHGlobal`) : le GC ne doit pas déplacer ces structs
- Top-level statements C# : les `struct` doivent venir **après** tout le code exécutable
- DLLs whisper copiées dans le dossier de sortie via `.csproj` (`CopyToOutputDirectory`)
- WinForms tray-only : `OutputType=WinExe` + `UseWindowsForms=true` + `net10.0-windows`
- `SetVisibleCore(false)` : pattern standard pour qu'une Form ne s'affiche jamais
- `volatile bool` : garantit la visibilité entre threads sans lock (signal d'arrêt d'enregistrement)
- `BeginInvoke` : poster un appel sur le thread UI depuis un thread de fond
- `RegisterHotKey` / `WM_HOTKEY` / `WndProc` : intercepter un raccourci global système
- `SendInput` / `INPUT` struct aplatie : injecter Ctrl+V dans la fenêtre cible
- `sizeof(INPUT)` sur Windows 64 bits = **40 octets** (MOUSEINPUT fixe la taille de l'union)
- `GetForegroundWindow` + `SetForegroundWindow` : capturer la fenêtre cible au moment du stop
- `[StructLayout(LayoutKind.Explicit)]` sans `Size` : les champs les plus lointains fixent la taille
- `RegisterHotKey` × N : IDs distincts, masques combinés avec `|`
- `m.WParam` dans `WndProc` identifie lequel des N hotkeys a été pressé
- Flags `volatile bool` (`_shouldPaste`, `_useLlm`) : décision au 1er appui, consommée à la fin de `Transcribe()`
- `Application.SetHighDpiMode(PerMonitorV2)` : avant `EnableVisualStyles()` — rescale automatique
- `AutoScaleMode.Dpi` + `AutoScaleDimensions = (96f, 96f)` : pixels logiques @ 96 DPI multipliés par DPI_réel/96
- `MenuStrip` + `MainMenuStrip` : `Controls.Add` dans l'ordre — contenu Fill en premier, MenuStrip en dernier
- Bouton X masque, Fichier > Quitter quitte : `FormClosing` avec `e.Cancel = true` + `Hide()`
- `WS_EX_NOACTIVATE` : empêche le focus **y compris intentionnel** — barre titre grisée en permanence. À éviter sauf pour overlays purement passifs.
- `ShowWithoutActivation = true` : `Show()` ne vole pas le focus, mais l'activation par clic reste possible. Bon niveau pour une fenêtre de monitoring interactive.
- `NotifyIcon.Click` : clic gauche uniquement. `MouseClick` couvre les deux boutons.
- `partial class` WinForms : découpe en plusieurs fichiers `.cs`, le compilateur fusionne.
- `GetGUIThreadInfo` / `GUITHREADINFO` : récupère `hwndFocus` dans la fenêtre cible
- `GetClassName` : classe Windows d'une HWND (utilisé pour autoriser/bloquer le paste)
- Classes positives pour paste : `Edit`, `RichEdit*`, `Scintilla`, `Chrome_RenderWidgetHostHWND`, `Chrome_WidgetWin_1`, `Mozilla*`, `ConsoleWindowClass`
- Apps Electron (VSCode, VSCodium, Discord) : retournent `Chrome_WidgetWin_1` comme hwndFocus (pas `Chrome_RenderWidgetHostHWND` qui était l'ancienne architecture Chrome < v100)

---

## Paramètres whisper actifs

- Modèle : `ggml-large-v3.bin`
- Langue : `fr`
- `initial_prompt` : `"Transcription en français."` (ancre le modèle, favorise la ponctuation)
- `entropy_thold` : `1.9` (rejette les segments répétitifs)
- `no_speech_thold` : `0.7`
- Filtre `IsHallucinatedOutput` : rejette tokens parasites (silence, "Sous-titrage", "Radio-Canada"…)
- Chunks : 30 s, buffer cumulé, `initial_prompt` mis à jour avec les 150 derniers caractères

---

## Branchement Ollama

- Endpoint : `/api/chat` (pas `/api/generate` — détection TEMPLATE défaillante sur GGUF Mistral)
- Modèle actif : `ministral-3:3b--instruct--96k`
- Parsing : `root["message"]["content"]`
- Filet : texte brut copié en clipboard avant l'appel LLM, réécrit remplace si LLM répond
- **Réécriture LLM non validée** — Ollama à reconfigurer (modèles + Modelfiles + température)

---

## Modèles Ollama disponibles

| Nom Ollama | Contexte | Rôle |
|---|---|---|
| `ministral-3:3b--instruct--96k` | 96k | Réécriture rapide (à tester) |
| `ministral-3:3b--reasoning--96k` | 96k | Reasoning sans thinking (à tester) |
| `ministral-3:8b--instruct--64k` | 64k | Réécriture qualité (à tester) |
| `ministral-3:14b--reasoning--32k` | 32k | Synthèse de réunion (futur) |
| `qwen2.5-coder:1.5b` | — | Autocomplete Continue.dev |

Convention : `{name}:{params-nb}--{type}--{context-size}`
Paramètres Modelfile (instruct) : `temperature 0.1`, `repeat_penalty 1.1`, `top_p 0.9`, `num_predict 16384`
Script : `D:\models\llm\[SCRIPT]--ollama--create-llm-model.ps1`

---

## Workflow de développement

Build/publish via `dotnet build -c Release` ou `dotnet publish -c Release -o ../publish/`
depuis `whisp-ui/WhispInteropTest/` (le bug XamlCompiler ne touche que WinUI 3, pas
WinForms — `dotnet` CLI fonctionne ici). Tuer le process avant tout rebuild :
`taskkill /F /IM WhispInteropTest.exe`.

Tâche planifiée Windows `Whisp` : pointe sur `whisp-ui/publish/WhispInteropTest.exe`.

---

## Backlog / priorités

1. **Screensaver pendant l'enregistrement** : investiguer si `SetThreadExecutionState` (Win32) suffit, ou si le screensaver interrompt réellement le flux audio.
2. **Gestion d'erreurs** :
   - Crash `whisper_full` sur un chunk → log chunk fautif, clipboard avec chunks valides
   - Buffer non vide mais transcription incomplète → `%TEMP%\whisp_recovery.txt`
   - Ollama injoignable → message explicite DebugForm
   - Micro absent / coupé → codes MMSYSERR/WAVERR + message clair
3. **Valider réécriture LLM** (Alt+Ctrl+`) — bloqué par reconfiguration Ollama
