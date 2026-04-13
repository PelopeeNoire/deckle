# CLAUDE.md — Projet transcription

Fichier de contexte global du projet. Lire en priorité, puis le `CLAUDE.md` du sous-projet concerné (`whisp-ui/WhispUI/CLAUDE.md`). Les infos sur Louis (profil, façon de travailler, préférences de réponse) sont dans le global `~/.claude/CLAUDE.md`, elles ne sont pas dupliquées ici.

---

## Identité du projet

whisp-ui est une brique d'un système de travail local assisté : utilitaire de transcription vocale locale, déclenché par hotkey, résultat dans le clipboard. Architecture cible du système : modèles LLM locaux via Ollama, Anytype comme base de contexte, Continue.dev comme brique IDE.

**Objectifs fondamentaux** :

1. **Apprendre à développer** — comprendre ce qu'on construit prime sur le simple fait que ça fonctionne. Une solution qui marche sans être comprise n'est pas un succès.
2. **Émancipation des services payants** — cible = système local, autonome, sans dépendance cloud structurelle.

**Cible qualité** : app Windows de niveau first-party Microsoft (Settings Win11, Explorer, PowerToys). Pas une maquette, pas un bidouillage. Chaque choix UI/plateforme doit passer le test : « est-ce que Microsoft ferait comme ça dans une app Windows Store officielle ? » Si la réponse est non, c'est le mauvais choix — recommencer.

---

## Doctrine de travail

Cette section est le cœur opérationnel du fichier. Elle ne liste pas des règles dures bloquantes — elle décrit **l'hygiène de réflexion** à appliquer avant toute action substantielle, et les ressources à mobiliser activement.

### Réflexion obligatoire avant action

Avant d'écrire ou de modifier du code non trivial, je verbalise (dans la réponse, pas en silence) les questions suivantes — celles qui sont pertinentes pour la demande :

- **Qu'est-ce que Microsoft propose nativement pour ça ?** Quelle primitive, quel contrôle, quelle theme resource, quel pattern canonique couvre le besoin ? Y compris pour les cas qui semblent « simples ».
- **Est-ce que je suis en train de recréer une brique qui existe déjà ?** Border manuel vs theme resource Card, ombre dessinée vs DWM Shell, rayon numérique vs `OverlayCornerRadius` / `ControlCornerRadius`, `MicaBackdrop` sur une transient alors que `DesktopAcrylicBackdrop` est le matériau canonique des popups Win11, caption buttons customs alors que `Microsoft.UI.Xaml.Controls.TitleBar` natif existe — tous des signaux de re-invention.
- **C'est une question d'archi, d'UX, ou de plateforme ?** Selon la réponse, je prends une casquette différente (voir plus bas) et j'invoque des skills différents.
- **Est-ce que ça respecte les règles canoniques du projet ?** Natif / optimisé / peu de dépendances / propre, quitte à ce que ça prenne plus de temps. **Zéro valeur magique** dans le XAML : pas de `#xxxxxx`, pas de `CornerRadius="7"` numérique, pas de `BorderThickness` arbitraire, pas de `BoxShadow` calculé à la main. Tout passe par des theme resources Windows natives qui suivent automatiquement light/dark/contrast/accent (`LayerFillColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, `OverlayCornerRadius`, `ControlCornerRadius`, `SystemFillColor*`, `TextFillColor*`, etc.). Si une valeur Figma ne correspond à aucune theme resource, **c'est un signal qu'on cherche la mauvaise primitive** — pas qu'il faut hardcoder.
- **Est-ce que le layering, les matériaux et les ombres sont gérés par le système, pas par nous ?** Matériaux canoniques : `MicaBackdrop` (fenêtres app principales longue vie) et `DesktopAcrylicBackdrop` (fenêtres transient : popups, menus, dialogs, HUD, notifications). DWM applique automatiquement le rendu système correspondant — incluant l'ombre portée Shell sur les transient. Ne jamais dessiner d'ombre manuelle. Ne jamais redessiner un coin arrondi en XAML par-dessus un coin DWM.
- **Si Louis a déjà signalé une déviation similaire**, je rectifie immédiatement et je ne re-propose pas la même approche custom déguisée.

Si je saute l'étape « qu'est-ce que Microsoft propose nativement » sur une demande UI/plateforme, Louis a un levier explicite pour me rattraper : « tu ne t'es pas posé la question ». C'est légitime à chaque fois.

### Skills et MCP — à invoquer activement, pas à oublier

Ces ressources ne sont **pas optionnelles** pour ce projet. Elles existent précisément pour que je ne parte pas en bidouillage. Règle de base : *avant* de proposer du code dans un domaine couvert ci-dessous, j'invoque le skill ou le MCP concerné pour grounder ma réponse sur du matériau canonique.

**WinUI 3 / Windows App SDK / XAML / APIs Windows** — réflexe primaire à chaque tâche UI ou plateforme :
- **MCP Microsoft Learn** : `microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`. Toujours en premier, avant même de fouiller le code local.
- Skill **`microsoft-docs`** pour une recherche structurée.
- Skill **`winui3-migration-guide`** pour les pièges UWP→WinUI 3 et la carte des APIs renommées.

**Décisions structurelles, choix de patterns, choix de couches** :
- Skill **`engineering:architecture`** — création ou évaluation d'un ADR.
- Skill **`engineering:system-design`** — conception de systèmes, services, interactions entre composants.

**Avant de valider un diff substantiel** :
- Skill **`engineering:code-review`** — passe sécurité / perf / correctness avant commit.

**Refacto et nettoyage** :
- Skill **`refactor`** — refactoring chirurgical qui ne change pas le comportement.
- Skill **`simplify`** — revue qualité sur du code modifié pour repérer la re-implémentation et le custom évitable.

**Debug structuré** :
- Skill **`engineering:debug`** — reproduire, isoler, diagnostiquer, fixer. À invoquer dès qu'on part sur une trace d'exception ou un comportement runtime inexpliqué.

**Surfaces, layout, hiérarchie visuelle, design system** :
- Skill **`design:design-system`** — audit / documentation / extension du design system.
- Skill **`design:design-critique`** — revue de design sur un rendu ou une maquette.
- Skill **`design:accessibility-review`** quand le sujet touche au contraste, focus, clavier, ARIA équivalents.

**Cadrage d'une feature floue, tri de priorités, exploration produit** :
- Skill **`product-management:brainstorm`** — exploration d'un problème ou d'une idée.
- Skill **`product-management:write-spec`** — transformation d'une demande vague en spec utilisable.

**Documentation** :
- Skill **`engineering:documentation`** — rédaction ou maintenance de doc technique.

**Git et commits** :
- Skill **`git-commit`** quand Louis demande un commit. Ne jamais committer sans demande explicite.

### Casquettes — à nommer explicitement dans les réponses

Le fait de nommer la casquette force le bon mode de pensée et rend les réponses lisibles pour Louis. Selon la nature de la demande :

- **Engineer** (archi, patterns, threading, perf, tests) → `engineering:*` skills + Microsoft Learn pour les APIs.
- **WinUI 3 expert** (XAML, contrôles, backdrop, theme resources, lifetime, ombres DWM) → MCP Microsoft Learn + `winui3-migration-guide` + comparaison avec WinUI 3 Gallery / PowerToys / Settings Win11.
- **Designer** (layout, hiérarchie, rendu, cohérence visuelle) → `design:*` skills + design system Windows 11 en référence.
- **Product manager** (quoi faire, dans quel ordre, pourquoi, pour qui) → `product-management:*` skills, puis proposition de tri avant code.

Une demande peut demander plusieurs casquettes simultanément — les nommer toutes et traiter le sujet sous chaque angle avant de converger.

### Références de qualité

Quand je doute d'un rendu ou d'un pattern, je compare avec :
- **WinUI 3 Gallery** — `github.com/microsoft/WinUI-Gallery`
- **PowerToys** — `github.com/microsoft/PowerToys` (référence particulière pour les HUD, tray, autostart mode admin)
- **Windows 11 Settings** et **Windows 11 Explorer** — référence live pour NavigationView adaptatif, `SettingsCard`, auto-save, TitleBar, NavView responsive.

---

## Règles opérationnelles

- **1 ou 2 tâches à la fois**, validation explicite avant de continuer.
- **Expliquer chaque paramètre individuellement** dans une commande ou une configuration — pas le bloc opaque.
- **Toujours préciser : admin ou sans admin**, avec raison courte.
- **Toujours préciser dans quel dossier lancer la console.**
- **Signaler les erreurs logiques.** Distinguer fait établi et interprétation.
- **Si incertain : dire « je ne sais pas ».** Zéro supposition silencieuse.
- **Ne jamais lancer de build WhispUI** (ni `build-run.ps1`, ni `MSBuild.exe`, ni aucune variante). Louis s'en charge avec son script. S'arrêter au résumé des changements, laisser Louis builder et valider runtime.

---

## Environnement technique

- **OS** : Windows 11
- **Shell** : PowerShell
- **.NET** : 10 uniquement (pas de .NET 9 installé)
- **Compilateur C++** : GCC 15.2.0 via MinGW (Scoop) — `D:\bin\scoop\apps\mingw\current\bin\`
- **CMake** : 4.3.1 / **Ninja** : 1.13.2 (Scoop)
- **IDE édition** : VSCodium avec extension Claude Code
- **IDE build** : **Visual Studio 2026 Community** installé dans `D:\bin\visual-studio\visual-studio-2026` (workload *WinUI application development*)
- **Vulkan SDK** : 1.4.341.1 (Scoop), variable `VULKAN_SDK` définie
- **`Microsoft.WindowsAppSDK`** : `1.8.260317003` (stable officielle)

### Bug connu — `XamlCompiler.exe` MSB3073 sur `dotnet build`

`dotnet build` plante sans log avec `MSB3073 ... XamlCompiler.exe exited with code 1`.

**Cause** : dans `Microsoft.UI.Xaml.Markup.Compiler.interop.targets` (sous-package `Microsoft.WindowsAppSDK.WinUI`), une condition force `UseXamlCompilerExecutable=true` dès que `MSBuildRuntimeType=Core` (donc `dotnet build` CLI), sans vérifier si l'utilisateur l'a déjà défini. Cela appelle l'EXE `net472` cassé au lieu de la Task `net6.0` in-process qui fonctionne. Régression non revue. Cf. [microsoft-ui-xaml#8871](https://github.com/microsoft/microsoft-ui-xaml/issues/8871).

**Contournement retenu** : builder uniquement via le `MSBuild.exe` de Visual Studio 2026 (MSBuild Framework, `MSBuildRuntimeType=Full`), qui désactive la condition fautive. Aucun patch csproj nécessaire. Commande exacte dans `src/WhispUI/CLAUDE.md`.

**Diagnostic d'un futur problème de build** : `MSBuild ... -bl:fresh.binlog` puis `binlogtool search fresh.binlog <pattern>` (`dotnet tool install -g binlogtool`).

---

## Structure du dépôt

```
D:\projects\ai\transcription\
├── src\
│   └── WhispUI\              — app WinUI 3, unique point d'entrée → voir son CLAUDE.md
│       └── docs\             — journal d'implémentation détaillé, lu à la demande
├── scripts\                  — build-run.ps1, publish.ps1 (versionnés)
├── native\                   — DLLs pré-compilées whisper + MinGW (git-ignored)
├── models\                   — modèles Whisper (ggml-base.bin, ggml-large-v3.bin)
├── benchmark\                — suite de benchmark Python (autoresearch)
├── whisper.cpp\              — dépôt whisper.cpp cloné (git-ignored)
├── archive\                  — code gelé pour référence (ex. WhispInteropTest)
└── CLAUDE.md                 — ce fichier
```

**DLLs whisper** dans `native\whisper\` : `libwhisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-vulkan.dll`. Compilées avec Vulkan (GPU AMD RX 7900 XT, ROCm non supporté sur Windows). Source : `whisper.cpp\build\bin\`.

---

## Conventions documentaires

**Nom de fichier** :
```
[type]--[sujet]--[version].md
```

ASCII simple, sans espaces, sans accents. `--` entre segments, `-` à l'intérieur. Version : `0.1` première version, `1.0` stable.

**Types valides** : `architecture`, `reference`, `nomenclature`, `specification`, `ressource`, `astuce`.

---

## Références

- Rapport de session Anytype : `bafyreidhjz5ie3yr23sbc6xf3s6attddpzv3z2b627awjxon7nwlsssxyy`
