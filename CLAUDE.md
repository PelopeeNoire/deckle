# CLAUDE.md — Projet transcription

<identity>
whisp-ui est un utilitaire de transcription vocale locale, déclenché par
hotkey, résultat dans le clipboard. Pensé pour s'intégrer à un pipeline
de travail local (LLM via Ollama optionnel pour la réécriture).

Objectifs fondamentaux :

1. Apprendre à développer — comprendre ce qu'on construit prime sur le
   fait que ça fonctionne. Une solution qui marche sans être comprise
   n'est pas un succès.
2. Émancipation des services payants — système local, autonome, sans
   dépendance cloud structurelle.

Cible qualité : app Windows de niveau first-party Microsoft (Settings
Win11, Explorer, PowerToys). Chaque choix UI ou plateforme passe le
test : « est-ce que Microsoft ferait comme ça dans une app Windows Store
officielle ? » Si la réponse est non, c'est le mauvais choix, recommencer.
</identity>

<rules>
Ne jamais lancer de build ni de publish WhispUI — ni `build-run.ps1`, ni
`MSBuild.exe`, aucune variante. Le maintainer s'en charge. S'arrêter au
résumé des changements et laisser le maintainer builder puis valider
runtime.
</rules>

<observability>
Logging et télémétrie : **source unique** dans
`src/WhispUI/Logging/TelemetryService.Instance`. Tout passe par ce hub —
fenêtre LogWindow et fichiers JSONL (`{app,latency,microphone,corpus}.jsonl`)
sont des sinks de la même source. Jamais de `File.AppendAllText`,
`Console.WriteLine`, `Debug.WriteLine`, ni de `*Logger.cs` parallèle dans
le code métier. Avant d'écrire le moindre code de log ou de mesure, lire
`src/WhispUI/CLAUDE.md` § « Télémétrie — source unique » et l'inventaire
canonique `src/WhispUI/docs/reference--logging-inventory--0.1.md`.
Exception unique et subordonnée : helper `DebugLog` file-based pour
crash natif non rattrapable, instrumentation **temporaire** jamais
commitée en l'état.
</observability>

<doctrine>
Avant d'écrire ou modifier du code non trivial, les questions suivantes
sont verbalisées dans la réponse, pour celles pertinentes à la demande.

**Primitive native d'abord.** Quelle primitive Windows, quel contrôle,
quelle theme resource, quel pattern canonique couvre le besoin — y
compris pour les cas qui semblent simples.

**Signaux de re-invention à détecter.** Border manuel là où une theme
resource Card existe, ombre dessinée à la place du DWM Shell, rayon
numérique à la place de `OverlayCornerRadius` / `ControlCornerRadius`,
`MicaBackdrop` sur une fenêtre transient à la place de
`DesktopAcrylicBackdrop`, caption buttons customs à la place de
`Microsoft.UI.Xaml.Controls.TitleBar` natif.

**Casquette nommée.** Archi, UX, plateforme : ce n'est pas la même
posture. Nommer la casquette au début de la réponse cadre le mode de
pensée et invoque les bons skills.

**Zéro valeur magique dans le XAML.** Pas de `#xxxxxx`, pas de
`CornerRadius="7"` numérique, pas de `BorderThickness` arbitraire, pas de
`BoxShadow` calculé à la main. Tout passe par des theme resources Windows
natives qui suivent automatiquement light / dark / contrast / accent :
`LayerFillColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`,
`CardStrokeColorDefaultBrush`, `OverlayCornerRadius`,
`ControlCornerRadius`, `SystemFillColor*`, `TextFillColor*`. Une valeur
Figma sans theme resource équivalente signale la mauvaise primitive.

**Matériaux et ombres gérés par le système.** `MicaBackdrop` sur les
fenêtres app longue vie, `DesktopAcrylicBackdrop` sur les transient
(popups, menus, dialogs, HUD, notifications). DWM applique le rendu
correspondant, incluant l'ombre Shell des transient. Ombres et coins
arrondis sont du ressort de DWM, pas du XAML.

### Casquettes

- Engineer (archi, patterns, threading, perf, tests) → skills
  `engineering:*` + Microsoft Learn pour les APIs.
- WinUI 3 expert (XAML, contrôles, backdrop, theme resources, lifetime,
  ombres DWM) → MCP Microsoft Learn + `winui3-migration-guide` +
  comparaison avec WinUI 3 Gallery / PowerToys / Settings Win11.
- Designer (layout, hiérarchie, rendu, cohérence visuelle) → skills
  `design:*` + design system Windows 11 en référence.
- Product manager (quoi, dans quel ordre, pour qui) → skills
  `product-management:*`, tri proposé avant code.

Une demande peut demander plusieurs casquettes simultanément — les
nommer toutes et traiter le sujet sous chaque angle avant de converger.

### Skills et MCP

Avant de proposer du code dans un domaine couvert, invoquer la
ressource correspondante pour grounder la réponse sur du matériau
canonique.

- UI, XAML, APIs Windows → MCP Microsoft Learn en premier, avant le
  code local : `microsoft_docs_search`, `microsoft_code_sample_search`,
  `microsoft_docs_fetch`. Skills complémentaires : `microsoft-docs`,
  `winui3-migration-guide`.
- Décision structurelle, ADR → `engineering:architecture`,
  `engineering:system-design`.
- Revue avant diff substantiel → `engineering:code-review`.
- Refacto, nettoyage → `refactor`, `simplify`.
- Debug structuré → `engineering:debug`.
- Surfaces, layout, design system → `design:design-system`,
  `design:design-critique`, `design:accessibility-review` pour
  contraste / focus / clavier.
- Cadrage flou, tri de priorités → `product-management:brainstorm`,
  `product-management:write-spec`.
- Documentation technique → `engineering:documentation`.
- Commit → `git-commit`, uniquement sur demande explicite.

### Références de qualité

En cas de doute sur un rendu ou un pattern :

- WinUI 3 Gallery — `github.com/microsoft/WinUI-Gallery`.
- PowerToys — `github.com/microsoft/PowerToys` (référence HUD, tray,
  autostart admin).
- Windows 11 Settings et Explorer — référence live pour
  NavigationView adaptatif, `SettingsCard`, auto-save, TitleBar,
  NavView responsive.
</doctrine>

<conventions>
### Langue du code et de l'UI : anglais

Strings user-facing en anglais d'emblée : labels UI, messages d'erreur
visibles, tooltips, statuts tray, chrono HUD, placeholders, copy
Settings, status keys moteur. Même quand la conversation avec le
maintainer se déroule en français. Le code (commentaires, noms, logs
techniques) est en anglais.

Traduction FR éventuelle : passe de localization dédiée plus tard. On
pense en anglais, pas en français avec une intention de traduction.

Dette héritée : quand du FR subsiste dans le code, basculer au passage.
Sweeps opportunistes au fil de l'eau.

### Documentation

Nom de fichier : `[type]--[sujet]--[version].md`. ASCII simple, sans
espaces ni accents. `--` entre segments, `-` à l'intérieur. Version :
`0.1` première, `1.0` stable.

Types valides : `architecture`, `reference`, `nomenclature`,
`specification`, `ressource`, `astuce`.

**Piège XML.** Ces noms contiennent `--`, séquence interdite dans un
commentaire XML par la spec XML 1.0 (parser MSBuild lève `MSB4025` au
build). Conséquence : ne jamais citer un nom de fichier doc tel quel
dans un commentaire `<!-- ... -->` d'un csproj, d'un manifest, d'un
`.xaml` ou d'un `.resw`. Paraphraser sans `--` (« voir la référence
localisation sous `docs/` ») plutôt que mettre le nom exact. Même
règle pour toute autre chaîne contenant `--` (séparateur d'option CLI,
flag de diff, etc.).
</conventions>

<environment>
- OS : Windows 11
- Shell : PowerShell
- .NET : 10 uniquement (pas de .NET 9 installé)
- Compilateur C++ : GCC 15.2.0 via MinGW (Scoop) — typiquement sous
  `<scoop-root>\apps\mingw\current\bin\`
- CMake 4.3.1 / Ninja 1.13.2 (Scoop)
- IDE édition : VSCodium avec extension Claude Code
- IDE build : Visual Studio 2026 Community (workload *WinUI application
  development*) — chemin d'install variable selon la machine
- Vulkan SDK : 1.4.341.1 (Scoop), variable `VULKAN_SDK` définie
- `Microsoft.WindowsAppSDK` : `1.8.260317003` (stable officielle)

### Bug connu — `XamlCompiler.exe` MSB3073 sur `dotnet build`

`dotnet build` plante sans log avec
`MSB3073 ... XamlCompiler.exe exited with code 1`.

**Cause** : dans `Microsoft.UI.Xaml.Markup.Compiler.interop.targets`
(sous-package `Microsoft.WindowsAppSDK.WinUI`), une condition force
`UseXamlCompilerExecutable=true` dès que `MSBuildRuntimeType=Core`
(donc `dotnet build` CLI), sans vérifier si l'utilisateur l'a déjà
défini. Cela appelle l'EXE `net472` cassé au lieu de la Task `net6.0`
in-process qui fonctionne. Régression non revue. Cf.
[microsoft-ui-xaml#8871](https://github.com/microsoft/microsoft-ui-xaml/issues/8871).

**Contournement retenu** : builder via le `MSBuild.exe` de Visual Studio
2026 (MSBuild Framework, `MSBuildRuntimeType=Full`), qui désactive la
condition fautive. Aucun patch csproj nécessaire. Commande exacte dans
`src/WhispUI/CLAUDE.md`.

**Diagnostic futur** : `MSBuild ... -bl:fresh.binlog` puis
`binlogtool search fresh.binlog <pattern>`
(`dotnet tool install -g binlogtool`).
</environment>

<repository>
```
<repo-root>/
├── src/
│   └── WhispUI\              — app WinUI 3, unique point d'entrée → voir son CLAUDE.md
│       └── docs\             — journal d'implémentation détaillé, lu à la demande
├── scripts\                  — build-run.ps1, publish-unpackaged.ps1 (versionnés)
├── native\                   — DLLs pré-compilées whisper + MinGW (git-ignored)
├── models\                   — modèles Whisper (ggml-base.bin, ggml-large-v3.bin)
├── benchmark/                — suite de benchmark Python
├── whisper.cpp/              — dépôt whisper.cpp cloné (git-ignored)
└── CLAUDE.md                 — ce fichier
```

DLLs whisper dans `native/whisper/` : `libwhisper.dll`, `ggml.dll`,
`ggml-base.dll`, `ggml-cpu.dll`, `ggml-vulkan.dll`. Compilées avec le
backend Vulkan (toute carte compatible Vulkan suffit ; sur AMD,
ROCm n'est pas supporté côté Windows donc Vulkan est la voie de
choix). Source : `whisper.cpp/build/bin/`.
</repository>
