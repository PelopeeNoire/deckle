# CLAUDE.md — Projet whisp-ui

Fichier de contexte pour Claude Code. À lire en priorité avant toute action.

---

## Qui est Louis

UX/UI designer de métier (R&D, UX/UI, marketing), qui apprend le développement logiciel en construisant ce projet. La compréhension prime sur la livraison. Une solution qui fonctionne sans être comprise n'est pas un succès.

**Conséquence directe pour Claude Code :**
- Expliquer chaque paramètre individuellement, pas la commande globale
- Signaler les erreurs logiques et distinguer fait établi et interprétation
- Si incertain : dire "je ne sais pas" — zéro supposition
- 1 ou 2 tâches à la fois, validation explicite avant de continuer

---

## Objectifs fondamentaux du projet

1. **Apprendre à développer** : comprendre ce qu'on construit prime sur le simple fait que ça fonctionne. Le mode pédagogique n'est pas optionnel.
2. **Émancipation des services payants** : cible = système local, autonome, sans dépendance cloud structurelle. Pas de dogme anti-cloud — objectif pratique de maîtrise et de durabilité.

---

## Place de whisp-ui dans le système plus large

Ce projet n'est pas isolé. Il s'inscrit dans un **système de travail local assisté** en cours de construction, dont l'architecture cible repose sur :
- Des modèles LLM locaux via Ollama (famille Qwen actuellement)
- Anytype comme base de contexte structurée
- Continue.dev comme brique IDE
- Un routeur local qui gère les modèles selon le besoin

**whisp-ui est destiné à devenir une brique skill de ce système** : un utilitaire de transcription vocale locale, déclenché par hotkey, qui produit du texte transcrit disponible dans le clipboard — potentiellement avec nettoyage LLM en option.

Ce positionnement implique que les décisions d'architecture de whisp-ui doivent rester cohérentes avec le système cible : local, sobre, maîtrisé, sans dépendance cloud structurelle.

---

## Projet whisp-ui

Utilitaire de transcription vocale locale sur Windows 11.
Stack : whisper.cpp (C++) + interface WPF (C#).

### Architecture retenue

**Option B — DLL native + P/Invoke**

- whisper.cpp compilé en `libwhisper.dll` (MinGW + CMake + Ninja)
- Appelée depuis C# via P/Invoke
- Flux cible : hotkey → capture micro → `temp.wav` (PCM 16kHz mono) → transcription → clipboard (+ collage auto et/ou réécriture LLM selon le raccourci)

### Structure du projet

```
D:\projects\ai\transcription\
├── whisper.cpp\          # Dépôt whisper.cpp cloné
│   └── build\
│       └── bin\          # DLLs compilées
├── whisp-ui\
│   └── WhispInteropTest\ # Projet console C# de test P/Invoke
│       └── Program.cs    # Appel whisper_print_system_info() via P/Invoke
├── shared\
│   └── ggml-base.bin     # Modèle (~142 MB)
└── CLAUDE.md             # Ce fichier
```

---

## État d'avancement

### Phase 1 — Compilation DLL ✅ TERMINÉE

Prérequis validés :
- GCC 15.2.0 (MinGW via Scoop)
- CMake 4.3.1
- Ninja 1.13.2

Commande exécutée depuis `whisper.cpp\build\` :
```
cmake -G Ninja -DBUILD_SHARED_LIBS=ON -DCMAKE_BUILD_TYPE=Release ..
ninja
```

DLLs produites dans `whisper.cpp\build\bin\` :
- `libwhisper.dll` — 646 KB — DLL principale à consommer via P/Invoke
- `ggml.dll` — 106 KB — dépendance runtime
- `ggml-base.dll` — 838 KB — dépendance runtime
- `ggml-cpu.dll` — 1.06 MB — backend CPU (détection native AMD64)

Warning bénin à la compilation : format string ligne 3437 (`%ld` vs `long long unsigned int`). Sans impact fonctionnel.

### Phase 2 — Console C# interop ✅ TERMINÉE

Projet console `WhispInteropTest` dans `whisp-ui\WhispInteropTest\`.
- P/Invoke validé : `whisper_print_system_info()` appelée et retourne les capacités CPU
- Résultat confirmé : AVX2, FMA, OpenMP actifs
- Stack : .NET 10, top-level statements, `[DllImport]` + `Marshal.PtrToStringAnsi`

### Phase 2b — Recompilation DLL avec Vulkan ✅ TERMINÉE

Contexte : GPU AMD RX 7900 XT (20 Go VRAM). ROCm non supporté sur Windows → Vulkan retenu.

DLLs produites dans `whisper.cpp\build\bin\` :
- `ggml-vulkan.dll` — backend Vulkan (nouveau)
- `libwhisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll` — inchangés

Commande utilisée depuis `whisper.cpp\build\` :
```
cmake -G Ninja -DBUILD_SHARED_LIBS=ON -DCMAKE_BUILD_TYPE=Release -DGGML_VULKAN=ON ..
ninja
```

Warning bénin identique à Phase 1 (`%ld` ligne 3437). Sans impact fonctionnel.

### Plan d'implémentation — 6 étapes séquentielles

| # | Nom | Critère de succès | APIs C# | État |
|---|-----|-------------------|---------|------|
| 1 | WAV fixe → texte | Texte transcrit affiché en console | `DllImport`, `Marshal`, lecture binaire WAV | ✅ |
| 2 | Micro → WAV → texte | `temp.wav` créé + texte transcrit | `waveIn*` via P/Invoke sur `winmm.dll` | ✅ |
| 3 | Pipeline console + clipboard | Texte dans le presse-papier | `Clipboard` (thread STA) | ✅ |
| 4 | Hotkey + tray icon | Hotkey `Alt+\`` depuis n'importe quelle app | `RegisterHotKey` P/Invoke `user32.dll`, `NotifyIcon` | ✅ |
| 5 | Collage automatique | Texte inséré sans Ctrl+V manuel | `SendInput` P/Invoke `user32.dll` | ✅ |
| 6 | 4 raccourcis avec comportements distincts | Alt+\` / Alt+Shift+\` / Alt+Ctrl+\` / Alt+Ctrl+Shift+\` fonctionnels | `RegisterHotKey` × 4, masques `MOD_*` combinés, flags `_shouldPaste` / `_useLlm` | ✅ |

### Acquis techniques

- `[StructLayout(LayoutKind.Sequential)]` + `byte` pour les `bool` C → layout mémoire identique à GCC
- Pattern `_by_ref` + `Marshal.PtrToStructure` : la DLL alloue, on copie, on libère
- `Marshal.StringToHGlobalAnsi` / `FreeHGlobal` : chaînes C en mémoire non managée
- `Marshal.PtrToStringUTF8` (pas `Ansi`) pour les retours texte de whisper
- Buffers WAVEHDR en mémoire non managée (`AllocHGlobal`) : le GC ne doit pas déplacer ces structs
- Top-level statements C# : les `struct` doivent venir **après** tout le code exécutable
- DLLs whisper copiées dans le dossier de sortie via `.csproj` (`CopyToOutputDirectory`)
- `winmm.dll` et `kernel32.dll` sont des DLLs système Windows — zéro dépendance externe
- WinForms tray-only : `OutputType=WinExe` + `UseWindowsForms=true` + `net10.0-windows`
- `SetVisibleCore(false)` : pattern standard pour qu'une Form ne s'affiche jamais
- `volatile bool` : garantit la visibilité entre threads sans lock (signal d'arrêt d'enregistrement)
- `BeginInvoke` : poster un appel sur le thread UI depuis un thread de fond
- `RegisterHotKey` / `WM_HOTKEY` / `WndProc` : intercepter un raccourci global système
- `SendInput` / `INPUT` struct aplatie : injecter Ctrl+V dans la fenêtre cible
- `sizeof(INPUT)` sur Windows 64 bits = **40 octets** (MOUSEINPUT fixe la taille de l'union, son ULONG_PTR force du padding interne)
- `GetForegroundWindow` + `SetForegroundWindow` : capturer la fenêtre cible au moment du stop, la restaurer avant l'injection
- `[StructLayout(LayoutKind.Explicit)]` sans `Size` : les champs les plus lointains fixent la taille — ajouter un champ `_pad` si nécessaire pour forcer la bonne taille
- `RegisterHotKey` × N : enregistrer plusieurs hotkeys avec des IDs distincts, masques combinés avec `|` (`MOD_ALT | MOD_SHIFT | MOD_CONTROL`)
- `m.WParam` dans `WndProc` identifie lequel des N hotkeys a été pressé
- Flags `volatile bool` (`_shouldPaste`, `_useLlm`) : décision prise au premier appui, consommée à la fin de `Transcribe()` — pattern propre pour porter une intention entre deux appuis distants dans le temps

- `Application.SetHighDpiMode(PerMonitorV2)` : à appeler avant `EnableVisualStyles()` — Windows envoie des WM_DPICHANGED, WinForms rescale automatiquement. Élimine le flou bitmap sur écrans 4K/125%+.
- `AutoScaleMode.Dpi` + `AutoScaleDimensions = (96f, 96f)` : les tailles codées en pixels logiques @ 96 DPI sont multipliées par DPI_réel/96 à l'affichage. Ne pas utiliser `AutoScaleMode.Font` si on contrôle la police explicitement.
- `MenuStrip` + `MainMenuStrip` : ajouter les menus de niveau 1 dans `menuStrip.Items`, les items dans `menu.DropDownItems`. `Controls.Add` dans l'ordre : contenu Fill en premier, MenuStrip en dernier (z-order).
- Bouton X masque, Fichier > Quitter quitte : `FormClosing` avec `e.Cancel = true` + `Hide()` sur la DebugForm — elle est réutilisée entre les transcriptions.

- `WS_EX_NOACTIVATE` (0x08000000) via `CreateParams` : la Form ne vole pas le focus au clic utilisateur. Mais **attention** : ce style empêche aussi l'activation par clic depuis l'arrière-plan — la barre de titre est toujours grisée, la fenêtre ne peut jamais être ramenée au premier plan par un clic. À n'utiliser que pour des overlays purement passifs (HUD, notifications).
- `ShowWithoutActivation = true` : propriété WinForms. Empêche le `Show()` programmatique de voler le focus — sans bloquer l'activation par clic ou par `Activate()` explicite. C'est le bon niveau pour une fenêtre de monitoring qui doit rester interactive. Se combine avec un `Activate()` explicite là où on veut donner le focus volontairement (clic tray, par exemple).
- `NotifyIcon.Click` vs `MouseClick` : `Click` est déclenché sur le clic gauche uniquement. `MouseClick` couvre les deux boutons. Pour un handler "clic gauche tray → ramener la fenêtre", utiliser `Click` + filtrer `MouseEventArgs.Button == MouseButtons.Left`.
- `partial class` WinForms : une Form peut être découpée en plusieurs fichiers `.cs` — le compilateur les fusionne. Pattern standard pour séparer P/Invoke, structs, et logique UI.
- `GetGUIThreadInfo` / `GUITHREADINFO` : permet de récupérer le `hwndFocus` dans le thread UI de la fenêtre cible — la handle du contrôle actuellement focusé, sans interaction visible.
- `GetClassName` : retourne la classe Windows d'une HWND. Utilisé pour décider si le contrôle focusé est un champ texte (`Edit`, `RichEdit*`, `Scintilla`, `Chrome_RenderWidgetHostHWND`, `Chrome_WidgetWin_1`, `Mozilla*`, `ConsoleWindowClass`).
- Détection de champ texte avant paste : `GetGUIThreadInfo` → `hwndFocus` → `GetClassName` → si classe connue = paste, sinon log "collage annulé". Évite d'injecter Ctrl+V dans une app qui ne l'attend pas.
- Classes Windows des apps Electron (VSCode, VSCodium, Discord…) : `GetGUIThreadInfo` retourne `Chrome_WidgetWin_1` comme `hwndFocus`, pas `Chrome_RenderWidgetHostHWND`. En Chrome récent (et toutes les apps Electron), `Chrome_WidgetWin_1` est le frame principal — c'est cette classe qui reçoit le focus clavier, même quand un champ texte est actif dans la page. `Chrome_RenderWidgetHostHWND` était l'ancienne architecture (Chrome < ~v100). **Les deux classes doivent être dans la liste positive** — même logique : on ne distingue pas champ texte / reste du contenu, on fait confiance à l'intention de l'utilisateur.

**Toutes les étapes terminées. ✅**

### Phase 3 — Branchement Ollama ✅ TERMINÉE (session 2026-04-01)

3 corrections appliquées dans `Program.cs` :
1. `OLLAMA_MODEL` : `qwen2.5:1.5b` → `ministral-3:3b--instruct--96k`
2. Endpoint : `/api/generate` → `/api/chat` + corps `messages: [{role:"system",...}, {role:"user",...}]`
3. Parsing : `root["response"]` → `root["message"]["content"]`
4. Filet clipboard : brut copié avant l'appel LLM, réécrit remplace si LLM répond

Raison du switch `/api/generate` → `/api/chat` : Ollama détecte mal le TEMPLATE des GGUF Mistral locaux,
ce qui peut faire ignorer le system prompt du Modelfile. `/api/chat` contourne ce problème.

**Réécriture LLM (Alt+Ctrl+`) : non validée.** Ollama a été réinitialisé — les modèles sont à retélécharger et reconfigurer (system prompt, température). Bloquer cette fonctionnalité jusqu'à ce que la base soit stabilisée.

### Phase 5 — Qualité de transcription brute + observabilité (session 2026-04-02)

Commit `01fece1` (session précédente) :
1. `initial_prompt` : `"Transcription en français."` — ancre le modèle, favorise la ponctuation.
2. `entropy_thold` : `2.4` → `1.9` — rejette plus tôt les segments répétitifs.
3. Filtre `IsHallucinatedOutput` : rejette tokens parasites Radio-Canada.
4. `no_speech_thold` : `0.6` → `0.7`.

Session 2026-04-02 (non encore commité) :
5. Transcription par chunks de 30 s : chaque chunk transcrit et filtré indépendamment, buffer cumulé, clipboard mis à jour après chaque chunk propre.
6. Fenêtre de debug WinForms (`DebugForm`) : logs horodatés `[HH:mm:ss.fff] [PHASE] message`, flag `const bool DEBUG_LOG` pour activer/désactiver sans recompiler.
   - Phase `RECORD` : démarrage boucle, chaque buffer WHDR_DONE récolté, fin de boucle + total.
   - Phase `TRANSCRIBE` : réception rawPcm, conversion float[], nb chunks, envoi/retour whisper_full par chunk avec durée, écriture clipboard.
   - Phase `INIT` : durée de chargement du modèle au démarrage.
7. `_ctx` sorti de `Transcribe()` → chargé une seule fois au démarrage sur thread de fond. Tray affiche "Chargement du modèle..." jusqu'à ce que le contexte soit prêt. Libéré dans `Dispose()`.

Session 2026-04-02 (suite) :
8. DPI et fenêtre debug — `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` + `Application.SetColorMode(SystemColorMode.System)` ajoutés avant `EnableVisualStyles()`.
   - `DebugForm` : `FormBorderStyle.Sizable` (barre titre Windows 11 standard), `AutoScaleMode.Dpi` + `AutoScaleDimensions = (96f, 96f)` pour le scaling DPI automatique.
   - `MenuStrip` avec deux menus : **Fichier** (Quitter → `Application.Exit()`) et **Vue** (Effacer les logs).
   - Le bouton X masque la fenêtre (`FormClosing` annulé + `Hide()`) — Fichier > Quitter quitte l'application entière.
   - Chaque menu a un bloc commenté avec sa responsabilité et un marqueur "ajouter ici" pour les futurs items.

**Priorités prochaine session :**
- Reconfigurer Ollama (modèles + Modelfiles) et valider la réécriture LLM (Alt+Ctrl+`)

### Phase 6 — Refactoring + DebugForm améliorée (session 2026-04-03)

Refactoring `Program.cs` (1171 lignes → ~600) :
- `WhispForm` devient une `partial class` découpée en 4 fichiers :
  - `WhispForm.Pinvoke.cs` — tous les `[DllImport]` et signatures P/Invoke
  - `Structs.cs` — toutes les structs P/Invoke (`WAVEHDR`, `INPUT`, `GUITHREADINFO`, etc.)
  - `DebugForm.cs` — classe `DebugForm` complète
  - `Program.cs` — logique principale (~600 lignes)

Améliorations `DebugForm` :
1. `ShowWithoutActivation = true` — `Show()` n'interrompt plus le focus de l'app cible. Note : `WS_EX_NOACTIVATE` avait été ajouté ici puis retiré en session 2026-04-03 — il empêchait aussi l'activation par clic utilisateur (voir acquis techniques).
2. Menu **Vue > Toujours visible** (checkbox) — toggle `TopMost` de la fenêtre debug.
3. `IsTextInputFocused` : avant chaque collage auto, `GetGUIThreadInfo` récupère `hwndFocus` dans la fenêtre cible, `GetClassName` lit la classe Windows du contrôle. Classe connue (`Edit`, `RichEdit*`, `Scintilla`, `Chrome_RenderWidgetHostHWND`, `Chrome_WidgetWin_1`, `Mozilla*`, `ConsoleWindowClass`) → paste autorisé. Sinon → log "Collage annulé", aucune injection. Note : `Chrome_WidgetWin_1` ajouté en session 2026-04-03 pour VSCodium (Electron) — VSCodium est une app Electron et retourne cette classe, pas `Chrome_RenderWidgetHostHWND`.

### Phase 7 — Correction clipboard + comportement fenêtre (en cours)

Session 2026-04-03 (commit précédent) :
- Suppression du `CopyToClipboard` intermédiaire dans la boucle de chunks. Le clipboard n'est plus écrit qu'une seule fois à la fin de `Transcribe()`, avec le texte complet.
- **Bug paste VSCodium corrigé** : `IsTextInputFocused` retournait `false` car VSCodium retourne `Chrome_WidgetWin_1` comme classe hwndFocus.

Session 2026-04-03 (suite — comportement fenêtre) :
- **Suppression de `WS_EX_NOACTIVATE`** dans `DebugForm.CreateParams` : ce style empêchait l'activation par clic utilisateur, causant le titre grisé permanent et l'impossibilité de ramener la fenêtre au premier plan.
- **`ShowWithoutActivation = true` conservé** : le `Show()` de démarrage d'enregistrement ne vole plus le focus.
- **Handler clic gauche tray** : `_trayIcon.Click` → `_debugForm.Show()` + `_debugForm.Activate()` — ramène la fenêtre au premier plan depuis la zone de notification.
- `_debugForm.Activate()` au démarrage de l'enregistrement supprimé — il volait le focus à l'app active au moment du premier hotkey.

**Priorités prochaine session :**

1. **Screensaver / mise en veille pendant l'enregistrement** — le screensaver s'active pendant une longue session d'enregistrement et casse le flux. Deux approches à investiguer : (a) empêcher le screensaver de s'activer via `SetThreadExecutionState` (Win32) pendant toute la durée de l'enregistrement, (b) vérifier si le screensaver interrompt réellement l'audio ou seulement l'affichage. À investiguer avant de choisir.

2. **Gestion d'erreurs** (backlog, ordre de priorité) :
   - Crash whisper_full sur un chunk : afficher quel chunk a échoué, copier les chunks valides en clipboard.
   - chunkBuffer non vide mais transcription incomplète : fichier temp `%TEMP%\whisp_recovery.txt` comme filet.
   - Ollama injoignable : message explicite dans la DebugForm.
   - Micro absent ou coupé : codes MMSYSERR/WAVERR attendus + message clair.

3. **Valider la réécriture LLM** (Alt+Ctrl+`) — dépend de la reconfiguration Ollama.

### Workflow de développement

Deux scripts dans `whisp-ui/` :
- `dev-run.ps1` : kill instance en cours → `dotnet build -c Release` → lance l'exe depuis `bin/Release/` pour tester
- `dev-publish.ps1` : kill instance de test → `dotnet publish -c Release -o ../publish/` pour mettre en production

La tâche planifiée Windows `Whisp` pointe sur `whisp-ui/publish/WhispInteropTest.exe` — relancer manuellement après publish si besoin.

### Phase 4 — Déploiement + Git (session 2026-04-01)

- `dotnet publish -c Release -o ../publish/` → `whisp-ui\publish\` mis à jour
- Tâche planifiée Windows `Whisp` pointe sur `whisp-ui\publish\WhispInteropTest.exe` ✅
- Dépôt git initialisé à la racine `d:\projects\ai\transcription\` ✅
  - Branche : `main`
  - Premier commit : `cceb090`
  - Identité git : `Louis <louis@local.dev>`
  - `whisper.cpp\`, `shared\*.bin`, `bin\`, `obj\`, `publish\` exclus via `.gitignore`

### Modèles Ollama disponibles (session 2026-04-01)

| Nom Ollama | Source GGUF | Contexte | Rôle |
|---|---|---|---|
| `ministral-3:3b--instruct--96k` | Ministral-3-3B-Instruct-2512-Q4_K_M.gguf | 96k | Réécriture rapide (à tester) |
| `ministral-3:3b--reasoning--96k` | Ministral-3-3B-Reasoning-2512-Q4_K_M.gguf | 96k | Reasoning sans thinking (à tester) |
| `ministral-3:8b--instruct--64k` | Ministral-3-8B-Instruct-2512-Q4_K_M.gguf | 64k | Réécriture qualité (à tester) |
| `ministral-3:14b--reasoning--32k` | Ministral-3-14B-Reasoning-2512-Q4_K_M.gguf | 32k | Synthèse de réunion (futur) |
| `qwen2.5-coder:1.5b` | — | — | Autocomplete Continue.dev |

Convention de nommage Ollama : `{name}:{params-nb}--{type}--{context-size}`
Paramètres Modelfile retenus (instruct) : `temperature 0.1`, `repeat_penalty 1.1`, `top_p 0.9`, `num_predict 16384`
Script de création/mise à jour : `D:\models\llm\[SCRIPT]--ollama--create-llm-model.ps1`
GGUFs source : `D:\models\llm\`

---

## Environnement technique

- OS : Windows 11
- Shell : PowerShell
- Compilateur C++ : GCC 15.2.0 via MinGW (Scoop) — `D:\bin\scoop\apps\mingw\current\bin\`
- CMake : 4.3.1 (Scoop)
- Ninja : 1.13.2 (Scoop)
- IDE : VSCodium avec extension Claude Code
- Gestionnaire de paquets : Scoop (base `D:\bin\scoop\`)
- .NET : 10
- Vulkan SDK : 1.4.341.1 (Scoop) — `D:\bin\scoop\apps\vulkan\current\`
- `VULKAN_SDK` : définie en variable d'environnement utilisateur
- Modèles Whisper disponibles : `ggml-base.bin` et `ggml-large-v3.bin` dans `shared\`
- Modèle actif : `ggml-large-v3.bin` (constante `MODEL_FILE` dans Program.cs)

---

## Conventions documentaires

Si Claude Code produit des fichiers de documentation, suivre la nomenclature du projet :

**Nom de fichier sur disque :**
```
[type]--[sujet]--[version].md
```
- ASCII simple, sans espaces, sans accents
- `--` entre segments, `-` à l'intérieur des segments
- Version en fin : `0.1` pour première version, `1.0` pour version stable

**Types valides :** `architecture`, `reference`, `nomenclature`, `specification`, `ressource`, `astuce`

Exemples :
- `reference--pinvoke-whisper--0.1.md`
- `astuce--mingw-cmake-ninja--0.1.md`

---

## Contraintes de travail (non négociables)

1. **1 ou 2 tâches à la fois.** Validation explicite avant de continuer.
2. **Toujours préciser : admin ou sans admin**, avec raison courte.
3. **Toujours préciser dans quel dossier lancer la console.**
4. **Expliquer chaque paramètre individuellement**, pas la commande globale.
5. **Signaler les erreurs logiques.** Distinguer fait établi et interprétation.
6. **Si incertain : dire "je ne sais pas".** Zéro supposition.

---

## Références

- Rapport de session Anytype : `bafyreidhjz5ie3yr23sbc6xf3s6attddpzv3z2b627awjxon7nwlsssxyy`
- Rapport disque : `C:\Users\Louis\Desktop\rapport-de-session--whisper-cpp-architecture--0.1.md`
