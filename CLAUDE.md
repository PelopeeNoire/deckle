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

1. **1 ou 2 tâches à la fois.** Validation explicite avant de continuer.
2. **Toujours préciser : admin ou sans admin**, avec raison courte.
3. **Toujours préciser dans quel dossier lancer la console.**
4. **Expliquer chaque paramètre individuellement**, pas la commande globale.
5. **Signaler les erreurs logiques.** Distinguer fait établi et interprétation.
6. **Si incertain : dire "je ne sais pas".** Zéro supposition.

---

## Références

- Rapport de session Anytype : `bafyreidhjz5ie3yr23sbc6xf3s6attddpzv3z2b627awjxon7nwlsssxyy`
