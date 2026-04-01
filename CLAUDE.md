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

**Toutes les étapes terminées. ✅**

### Prochaine étape prévue — Finaliser le branchement Ollama (Alt+Ctrl+`)

`RewriteWithLlm()` est implémentée dans `Program.cs` mais nécessite 3 corrections :

1. **Mauvais modèle** : `OLLAMA_MODEL = "qwen2.5:1.5b"` → à remplacer par un des modèles ci-dessous
2. **Prompt inline redondant** : le prompt contient des instructions dupliquant le system prompt du Modelfile → passer uniquement le texte brut à réécrire
3. **Pas de filet clipboard** : copier le brut d'abord, puis remplacer par le réécrit si le LLM répond

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
