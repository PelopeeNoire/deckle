# CLAUDE.md — Projet transcription

Fichier de contexte global. Lire en priorité, puis le CLAUDE.md du sous-projet concerné.

---

## Qui est Louis

UX/UI designer de métier (R&D, UX/UI, marketing), qui apprend le développement logiciel en construisant ce projet. La compréhension prime sur la livraison. Une solution qui fonctionne sans être comprise n'est pas un succès.

**Conséquence directe pour Claude Code :**
- Expliquer chaque paramètre individuellement, pas la commande globale
- Signaler les erreurs logiques et distinguer fait établi et interprétation
- Si incertain : dire "je ne sais pas" — zéro supposition
- 1 ou 2 tâches à la fois, validation explicite avant de continuer

---

## Objectifs fondamentaux

1. **Apprendre à développer** : comprendre ce qu'on construit prime sur le simple fait que ça fonctionne.
2. **Émancipation des services payants** : cible = système local, autonome, sans dépendance cloud structurelle.

---

## Place dans le système plus large

whisp-ui est une brique skill d'un système de travail local assisté : utilitaire de transcription vocale locale, déclenché par hotkey, résultat dans le clipboard. Architecture cible du système : modèles LLM locaux via Ollama, Anytype comme base de contexte, Continue.dev comme brique IDE.

---

## Structure du dépôt

```
D:\projects\ai\transcription\
├── whisper.cpp\              — dépôt whisper.cpp cloné (DLLs dans build\bin\)
├── shared\                   — modèles Whisper (ggml-base.bin, ggml-large-v3.bin)
├── whisp-ui\
│   └── WhispUI\              — app WinUI 3, unique point d'entrée → voir son CLAUDE.md
├── archive\                  — code gelé pour référence (ex. WhispInteropTest)
└── CLAUDE.md                 — ce fichier
```

**DLLs whisper** dans `whisper.cpp\build\bin\` : `libwhisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-vulkan.dll`. Compilées avec Vulkan (GPU AMD RX 7900 XT, ROCm non supporté sur Windows).

---

## Environnement technique

- OS : Windows 11
- Shell : PowerShell
- .NET : 10 uniquement (pas de .NET 9 installé)
- Compilateur C++ : GCC 15.2.0 via MinGW (Scoop) — `D:\bin\scoop\apps\mingw\current\bin\`
- CMake : 4.3.1 / Ninja : 1.13.2 (Scoop)
- IDE : VSCodium avec extension Claude Code
- Vulkan SDK : 1.4.341.1 (Scoop), variable `VULKAN_SDK` définie
- IDE / build : **Visual Studio 2026 Community** installé dans `D:\bin\visual-studio\visual-studio-2026` (workload *WinUI application development*). VSCodium reste l'IDE d'édition. Build WhispUI via le `MSBuild.exe` de VS uniquement — `dotnet build` CLI reste cassé par le bug Microsoft documenté ci-dessous (installer VS ne le débloque pas : `dotnet build` reste sur `MSBuildRuntimeType=Core`, qui déclenche la branche fautive).
- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle).

### Bug connu — XamlCompiler.exe MSB3073 sur `dotnet build`

`dotnet build` plante sans log avec `MSB3073 ... XamlCompiler.exe exited with code 1`.
Cause : dans `Microsoft.UI.Xaml.Markup.Compiler.interop.targets` (sous-package
`Microsoft.WindowsAppSDK.WinUI`), une condition force `UseXamlCompilerExecutable=true` dès
que `MSBuildRuntimeType=Core` (donc `dotnet build` CLI), sans vérifier si l'utilisateur l'a
déjà défini. Cela appelle l'EXE `net472` cassé au lieu de la Task `net6.0` in-process qui
fonctionne. Régression non revue. Cf. [microsoft-ui-xaml#8871](https://github.com/microsoft/microsoft-ui-xaml/issues/8871).

**Contournement retenu** : builder uniquement via le `MSBuild.exe` de Visual Studio 2026
(MSBuild Framework, `MSBuildRuntimeType=Full`), qui désactive la condition fautive. Aucun
patch csproj nécessaire. Détails dans `whisp-ui/WhispUI/CLAUDE.md`.

Diagnostic d'un futur problème de build : `MSBuild ... -bl:fresh.binlog` puis
`binlogtool search fresh.binlog <pattern>` (`dotnet tool install -g binlogtool`).

---

## Conventions documentaires

**Nom de fichier :**
```
[type]--[sujet]--[version].md
```
- ASCII simple, sans espaces, sans accents. `--` entre segments, `-` à l'intérieur.
- Version : `0.1` première version, `1.0` stable.

**Types valides :** `architecture`, `reference`, `nomenclature`, `specification`, `ressource`, `astuce`

---

## Contraintes de travail (non négociables)

0. **Qualité cible = app Windows Store.** Ce projet vise une app Windows de qualité production — pas une maquette, pas un bidouillage. Chaque choix UI/plateforme doit passer le test « est-ce que Microsoft ferait comme ça dans une app first-party Win11 ? ». Si la réponse est non, c'est le mauvais choix — recommencer. Zéro valeur magique (couleurs hex, rayons, spacings, shadows en dur) : tout passe par des **theme resources natives Windows** ou des **primitives natives** qui réagissent automatiquement au thème système (light/dark), au contraste, à l'accent utilisateur, aux matériaux (Mica, Acrylic) et aux ombres DWM.

0.1. **Source primaire = Microsoft Learn + skills.** Avant **toute** implémentation UI/plateforme (WinUI 3, Windows App SDK, .NET) — et *a fortiori* avant de sortir du custom — interroger en premier lieu le MCP Microsoft Learn (`microsoft_docs_search` / `microsoft_code_sample_search` / `microsoft_docs_fetch`) et invoquer les skills disponibles (notamment `microsoft-docs`, `winui3-migration-guide`). Chercher explicitement **la primitive, le contrôle, le backdrop, la theme resource, le pattern de layering canonique** qui couvre le besoin — y compris pour les cas qui semblent « simples ». Exemples concrets de pièges : une HUD flottante = matériau `DesktopAcrylicBackdrop` (transient window), pas `MicaBackdrop` (main window) ; une surface Card = Grid + `LayerFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` + `OverlayCornerRadius`, pas `Border BorderBrush="#xxxxxx"` ; un corner radius DWM n'est pas à redessiner en XAML, il est déjà géré par la window.

0.2. **Le custom vient en dernier recours, jamais en premier réflexe.** Avant de proposer un Border manuel, un SolidColorBrush en dur, une ombre dessinée à la main, un rayon numérique, un stroke custom : prouver qu'**aucune** brique native ne couvre le besoin. Si Louis signale une déviation custom (« pas assez natif », « mets plutôt une theme resource »), rectifier immédiatement et ne pas re-dériver. Référence de qualité typique : WinUI 3 Gallery (github.com/microsoft/WinUI-Gallery), PowerToys (github.com/microsoft/PowerToys), Windows 11 Settings, Windows 11 Explorer.
1. **1 ou 2 tâches à la fois.** Validation explicite avant de continuer.
2. **Toujours préciser : admin ou sans admin**, avec raison courte.
3. **Toujours préciser dans quel dossier lancer la console.**
4. **Expliquer chaque paramètre individuellement**, pas la commande globale.
5. **Signaler les erreurs logiques.** Distinguer fait établi et interprétation.
6. **Si incertain : dire "je ne sais pas".** Zéro supposition.

---

## Références

- Rapport de session Anytype : `bafyreidhjz5ie3yr23sbc6xf3s6attddpzv3z2b627awjxon7nwlsssxyy`
