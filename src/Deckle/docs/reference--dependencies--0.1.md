# Reference — Dependencies & layout pour publication

Carte des artefacts dont l'app a besoin pour tourner, où ils vivent au build,
au publish, et au runtime, et qui les produit. Pivot pour piloter la
réorganisation pré-publication. `<AppName>` = nom user-facing final
(placeholder — voir `AppPaths.AppFolderName`).

## 1. Inventaire des artefacts

Six familles. Pour chacune : ce que c'est, qui produit, qui consomme,
volume typique, mutabilité.

| Famille | Contenu | Volume | Mutable | Produit par | Consommé par |
|---|---|---|---|---|---|
| **Binaire app** | `<AppName>.exe`, `.deps.json`, `.runtimeconfig.json`, WinAppSDK runtime DLLs, `.pri`, `Assets/` (icônes, fonts, sons warmup) | ~50-100 MB | Non | `publish-unpackaged.ps1` | OS / utilisateur |
| **Runtime natif whisper** | `libwhisper.dll` + `ggml.dll` + `ggml-base.dll` + `ggml-cpu.dll` + `ggml-vulkan.dll` + MinGW C++ runtime (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`) | ~50 MB | Non (versionné) | Build whisper.cpp via CMake/Ninja+MinGW (Vulkan backend) | `NativeMethods.cs` via `[DllImport("libwhisper")]` |
| **Modèles Whisper** | `ggml-large-v3.bin` (par défaut) ou `ggml-base.bin` ; plus `ggml-silero-v6.2.0.bin` (VAD) | 700 KB → 3 GB | Oui (utilisateur peut swap) | HuggingFace `ggerganov/whisper.cpp` + `ggml-org/whisper-vad` | `WhispEngine.LoadModel` |
| **Settings** | `settings.json` + `backups/settings-YYYYMMDD-HHmmss.json` | < 100 KB | Oui (chaque modif UI) | `SettingsService.Flush` | Toute l'app au boot |
| **Télémétrie** | `app.jsonl` (+ rotations `app-1`, `app-2`…), `latency.jsonl`, `microphone.jsonl`, `<slug>/corpus.jsonl`, `<slug>/audio/<ts>.wav`, `legacy/` | 0 → quelques GB selon l'usage | Oui (append-only sauf rotation) | `JsonlFileSink`, `WavCorpusWriter` | Outils benchmark, debug humain |
| **Benchmark suite** | Scripts Python (`benchmark.py`, `rewrite_bench.py`, `segment_corpus.py`, `autoresearch.py`), `reports/` | ~quelques MB scripts + variable reports | Oui | Repo séparé `<AppName>-benchmark` (à créer) | Outil offline, jamais lu par l'app au runtime |

## 2. Sources externes

D'où vient chaque artefact qui n'est pas produit dans le repo principal.

| Artefact | Source canonique | Stabilité de l'URL | Vérification |
|---|---|---|---|
| `ggml-base.bin` | `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin` | Très stable (dépôt officiel ggerganov) | À ajouter : SHA-256 fixe |
| `ggml-large-v3.bin` | `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin` | Idem | Idem |
| `ggml-silero-v6.2.0.bin` | `https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin` | Versionné par tag `v6.2.0` | Idem |
| Runtime natif whisper | **À héberger** : repo GitHub `<AppName>-redist` à créer, releases avec ZIP des DLLs précompilées Vulkan x64 + checksums | Tu contrôles | SHA-256 calculé à la release |
| `whisper.cpp` source | `https://github.com/ggerganov/whisper.cpp` | Très stable | Tag whisper.cpp pinné dans la doc build |
| Suite benchmark | **À héberger** : repo GitHub `<AppName>-benchmark` à extraire de `benchmark/` actuel | Tu contrôles | n/a |
| Vulkan SDK runtime | LunarG ou drivers GPU AMD/Intel/NVIDIA | Externe | n/a — vérification "Vulkan dispo" au runtime |
| WinAppSDK runtime | Bundled via `WindowsAppSDKSelfContained=true` dans le csproj | Pin sur `1.8.260317003` dans `<PackageReference>` | n/a |
| .NET 10 runtime | Bundled via `SelfContained=true` au publish | Pin via `global.json` (`10.0.104`) | n/a |

## 3. Cibles disque au runtime

Deux racines, séparation stricte read-only / writable.

### 3a. `%PROGRAMFILES%\<AppName>\` — read-only, idem folder portable

```
%PROGRAMFILES%\<AppName>\
├── <AppName>.exe                           # bundled managed (PublishSingleFile)
├── <AppName>.deps.json
├── <AppName>.runtimeconfig.json
├── <AppName>.pri                           # XAML resources compilées (cf. EnableMsixTooling)
├── Microsoft.WindowsAppRuntime.*.dll       # WinAppSDK self-contained
├── Microsoft.UI.Xaml.dll
├── …d'autres DLLs WinAppSDK / .NET self-contained…
└── Assets\
    ├── Icons\
    ├── Fonts\
    └── Sounds\                             # warmup PCM WAV
```

Contenu produit par `publish-unpackaged.ps1`. Aucun fichier mutable, aucune
DLL whisper, aucun modèle.

### 3b. `%LOCALAPPDATA%\<AppName>\` — writable, racine UserDataRoot

Définie par `AppPaths.UserDataRoot`, override en dev via env var
`DECKLE_DATA_ROOT`.

```
%LOCALAPPDATA%\<AppName>\
├── settings\
│   ├── settings.json
│   └── backups\
│       └── settings-YYYYMMDD-HHmmss.json
├── telemetry\
│   ├── app.jsonl                           # rotated app-1, app-2, …
│   ├── latency.jsonl
│   ├── microphone.jsonl
│   ├── <profile-slug>\
│   │   ├── corpus.jsonl
│   │   └── audio\
│   │       └── <ts>.wav
│   └── legacy\
├── models\
│   ├── ggml-base.bin                       # optionnel
│   ├── ggml-large-v3.bin                   # défaut WhispEngine.MODEL_FILE
│   └── ggml-silero-v6.2.0.bin              # VAD
└── native\
    ├── libwhisper.dll
    ├── ggml.dll, ggml-base.dll, ggml-cpu.dll, ggml-vulkan.dll
    └── libgcc_s_seh-1.dll, libstdc++-6.dll, libwinpthread-1.dll
```

`benchmark/` n'apparaît pas par défaut — installé sur demande explicite via
Settings (Lot B.5 reporté). Si installé, suit le même pattern :

```
%LOCALAPPDATA%\<AppName>\benchmark\
├── benchmark.py, rewrite_bench.py, segment_corpus.py, autoresearch.py
├── README.md, AGENT.md
└── reports\
```

### 3c. Justification de la séparation

- Program Files = lecture seule sous UAC standard (sandbox MSIX, ou
  install en mode admin). Aucune écriture runtime n'y est possible
  sans élévation. Toute donnée mutable doit être ailleurs.
- `%LOCALAPPDATA%` = convention Windows / Settings Win11 / PowerToys.
  Per-user, non-roaming (vs `%APPDATA%` qui suit l'utilisateur réseau).
  Modèles + télémétrie n'ont aucune raison de roamer.
- Désinstallation propre : suppression de Program Files n'altère pas
  les données utilisateur, et inversement.

## 4. Matrice publish / first-run / runtime

Comment chaque artefact arrive sur la machine cible.

| Artefact | Ship dans publish ? | Téléchargé first-run ? | Créé runtime ? |
|---|:---:|:---:|:---:|
| `<AppName>.exe` + WinAppSDK + Assets | Oui | Non | Non |
| Runtime natif whisper (8 DLLs) | **Non** | **Oui** (browse local V1, download depuis `<AppName>-redist` plus tard) | Non |
| Modèle par défaut `ggml-large-v3.bin` | **Non** | **Oui** (HuggingFace) | Non |
| Modèles secondaires + Silero VAD | **Non** | Oui (à la demande, Settings) | Non |
| `settings\settings.json` | Non | Non | Oui (créé vide au premier `Save`) |
| `telemetry\*.jsonl` | Non | Non | Oui (créés à l'écriture, gated par toggles) |
| `benchmark\` | **Non** | Non (Settings → "Install benchmark suite") | Non |

Conséquence côté `publish-unpackaged.ps1` : artefact attendu < 100 MB. Tout
ce qui est volumineux ou versionné indépendamment descend post-install.

## 5. Dépendances système hors notre contrôle

Ce que l'OS doit fournir lui-même pour que l'app tourne.

| Dépendance | Quoi | Ship-able ? | Comment vérifier |
|---|---|---|---|
| **Windows 11** | Cible 22H2+ via `<TargetFramework>net10-windows10.0.19041.0</TargetFramework>` ; minimum `<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>` | Non | `Environment.OSVersion` au boot |
| **Vulkan runtime** | Driver GPU avec support Vulkan 1.x. Toute GPU AMD/Intel/NVIDIA récente. Pas de SDK requis sur la machine cible — uniquement le runtime, fourni avec le driver. | Non | Backend ggml-vulkan affiche `ggml_vulkan: ...` au load ; absence = bascule CPU silencieuse |
| **MS Visual C++ Runtime** | Pas requis : on ship avec MinGW, pas avec MSVC. Aucun `vcredist` à installer. | n/a | n/a |
| **.NET 10 runtime** | Bundled via `SelfContained=true` + `PublishSingleFile=true`. Aucune install runtime requise. | **Oui (bundled)** | Pas de check, partie du publish |
| **WinAppSDK runtime** | Bundled via `WindowsAppSDKSelfContained=true`. Pas de bootstrapper requis. | **Oui (bundled)** | Pas de check, partie du publish |

Sécurité : Windows SmartScreen va flagger la première exécution d'un
`.exe` non signé. Solution propre = code-signing certificate (Authenticode)
appliqué par le pipeline de publish — **hors scope de cette passe**, à
prévoir avant distribution publique.

## Annexe — Inventaire git aujourd'hui

Pour mémoire, où ces artefacts vivent actuellement dans le repo de dev :

```
D:\projects\ai\transcription\
├── src\Deckle\                       # binaire app (versionné)
├── native\whisper\*.dll               # runtime natif (git-ignored)
├── native\mingw\*.dll                 # MinGW runtime (git-ignored)
├── models\ggml-*.bin                  # modèles (git-ignored, taille)
├── whisper.cpp\                       # source whisper.cpp clonée (git-ignored)
├── benchmark\                         # suite benchmark (versionné — à extraire)
└── scripts\
    ├── build-run.ps1                  # build + launch dev
    ├── publish-unpackaged.ps1         # produit publish/<AppName>.exe
    └── restore-assets.ps1             # télécharge whisper.cpp + modèles dans native/ et models/
```

`restore-assets.ps1` est aujourd'hui la "voie d'install" en dev (clone
whisper.cpp, build les DLLs, télécharge les modèles depuis HuggingFace).
Le first-run wizard reproduira ce flux côté utilisateur final, mais
sans build local — il pointera sur les artefacts déjà compilés du repo
`<AppName>-redist`.
