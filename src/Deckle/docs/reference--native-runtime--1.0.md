# Native runtime — recette de recompilation et provenance

Doc canonique pour produire les 8 DLLs du runtime natif Deckle (whisper.cpp
Vulkan + MinGW C++ runtime). Le wizard de premier lancement télécharge ce
bundle depuis une release GitHub du repo Deckle ; cette page explique
**comment le bundle est régénéré** quand whisper.cpp upstream sort une
nouvelle version, ou quand on veut un patch local.

Public : maintainer Deckle, ou contributeur qui veut reproduire le build.
Pas pour l'utilisateur final.

---

## Catalogue des 8 DLLs

Le catalogue source de vérité côté C# est
[NativeRuntime.RequiredDllNames](../Setup/NativeRuntime.cs). La même liste
est dupliquée côté PowerShell dans
[scripts/setup-assets.ps1](../../../scripts/setup-assets.ps1)
(`$WhisperDlls` + `$MingwDlls`). Toute divergence est un bug.

5 DLLs whisper.cpp Vulkan (sortie de `whisper.cpp/build/bin/`) :

- `libwhisper.dll`
- `ggml.dll`
- `ggml-base.dll`
- `ggml-cpu.dll`
- `ggml-vulkan.dll`

3 DLLs MinGW C++ runtime (sortie de `<scoop>/apps/mingw/current/bin/`) :

- `libgcc_s_seh-1.dll`
- `libstdc++-6.dll`
- `libwinpthread-1.dll`

Les MinGW DLLs sont nécessaires parce que `ggml-vulkan.dll` est compilée
contre la libstdc++ MinGW. Sans ces 3 fichiers à côté, la résolution
Windows échoue à charger `ggml-vulkan.dll` au runtime.

---

## Recette de build — version `native-v1.0.0`

État de référence du premier bundle publié (avril 2026) :

| Composant            | Version                       |
|----------------------|-------------------------------|
| whisper.cpp          | `v1.8.4` (tag amont)          |
| Compilateur C++      | GCC 15.2.0 MinGW (Scoop)      |
| Vulkan SDK           | 1.4.341.1                     |
| CMake                | 4.3.1                         |
| Ninja                | 1.13.2                        |

### 1. Récupérer le source

Le clone vit **à l'extérieur** du repo Deckle — par exemple dans
`D:\workspace\whisper.cpp`. Le repo Deckle ne contient pas le source.

```powershell
cd D:\workspace
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
git checkout v1.8.4
```

### 2. Configurer le build avec backend Vulkan

```powershell
cmake -B build -G Ninja `
    -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_C_COMPILER="$env:SCOOP\apps\mingw\current\bin\cc.exe" `
    -DCMAKE_CXX_COMPILER="$env:SCOOP\apps\mingw\current\bin\c++.exe" `
    -DGGML_VULKAN=ON
```

Notes :

- `GGML_VULKAN=ON` est le seul flag GGML actif. Toutes les options
  Vulkan secondaires (`GGML_VULKAN_DEBUG`, `GGML_VULKAN_VALIDATE`,
  `GGML_VULKAN_RUN_TESTS`, etc.) restent à `OFF` — c'est un build
  release, pas une instrumentation.
- Le compilateur est imposé explicitement parce que cmake choisit
  parfois MSVC ou clang sur Windows si présents — le runtime Vulkan
  ggml a été développé contre GCC/MinGW, pas MSVC.
- `Ninja` est l'unique générateur testé. MSBuild fonctionnerait mais
  pas validé.

### 3. Compiler

```powershell
cmake --build build --config Release -j
```

Sortie : `build/bin/` contient les 5 whisper DLLs ci-dessus + des
exécutables (whisper-cli, bench, etc.) qu'on **ignore**.

### 4. Récupérer les 3 DLLs MinGW C++ runtime

Source : `$env:SCOOP\apps\mingw\current\bin\`. Versions liées au
compilateur — installer/mettre à jour MinGW via Scoop assure la
cohérence :

```powershell
scoop install mingw
scoop update mingw
```

---

## Production du bundle publié

Le script `scripts/publish-native-runtime.ps1` automatise toute la
suite : copie du catalogue (5 + 3), génération du `PROVENANCE.txt`,
calcul des SHA256 par fichier dans `SHA256SUMS`, zip à plat, et
optionnellement `gh release create native-v$Version`.

```powershell
.\scripts\publish-native-runtime.ps1 `
    -Version 1.0.0 `
    -WhisperRepo D:\workspace\whisper.cpp `
    -OutDir D:\tmp\deckle-native-1.0.0
```

Le SHA256 du zip est émis sur stdout — à coller dans
`NativeRuntime.CurrentBundle.Sha256` côté C#.

---

## Schéma de version `native-vX.Y.Z`

Indépendant de la version de l'app Deckle :

- `X.Y` suit le minor de whisper.cpp upstream (ex : `1.8` pour
  `v1.8.x` upstream).
- `Z` est un compteur **local** Deckle, incrémenté à chaque rebuild
  (changement de toolchain, patch local, fix Vulkan, etc.).

Exemple de progression :

- `native-v1.0.0` — premier release stable basé sur whisper.cpp v1.8.4.
- `native-v1.0.1` — même whisper.cpp, MinGW upgradé en 16.0 (rebuild
  forcé par changement d'ABI).
- `native-v2.0.0` — rebase sur whisper.cpp v2.x quand upstream sort.

---

## Gabarit `PROVENANCE.txt`

Embarqué dans chaque zip publié. Le script `publish-native-runtime.ps1`
le génère automatiquement à partir des données du build courant. Format
texte lisible, pas de JSON — l'utilisateur final peut l'ouvrir sans
outil :

```
Deckle native runtime bundle
============================

Bundle version : native-v1.0.0
Build date     : 2026-04-30T14:32:18+02:00
Builder        : <hostname optionnel>

Upstream
--------
whisper.cpp    : v1.8.4
commit         : <40 hex>

Toolchain
---------
Compiler       : GCC 15.2.0 (x86_64-posix-seh-rev1, MinGW-Builds)
Vulkan SDK     : 1.4.341.1
CMake          : 4.3.1
Generator      : Ninja 1.13.2

Build flags
-----------
CMAKE_BUILD_TYPE    = Release
GGML_VULKAN         = ON
(other GGML_VULKAN_* flags = OFF)

Files (8)
---------
libwhisper.dll        sha256=<64 hex>
ggml.dll              sha256=<64 hex>
ggml-base.dll         sha256=<64 hex>
ggml-cpu.dll          sha256=<64 hex>
ggml-vulkan.dll       sha256=<64 hex>
libgcc_s_seh-1.dll    sha256=<64 hex>
libstdc++-6.dll       sha256=<64 hex>
libwinpthread-1.dll   sha256=<64 hex>

Licenses
--------
whisper.cpp / ggml : MIT — https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE
MinGW C++ runtime  : GPL-3 with runtime exception (libgcc, libstdc++) /
                     MIT (libwinpthread). Redistribution permitted as
                     dynamic linkage runtime per the GCC runtime library
                     exception.

Reproduction
------------
See src/Deckle/docs/reference--native-runtime--1.0.md in the Deckle
repository for the full recompilation recipe.
```

Le bloc `Files` est rempli par le script à partir du calcul SHA256
incrémental sur chaque DLL ajoutée au zip. `SHA256SUMS` à côté de
`PROVENANCE.txt` reprend la même info en format `sha256sum -c`
compatible.

---

## Validation après build

Avant publication, vérifier que le bundle est utilisable :

1. Décompresser dans un dossier vide.
2. Pointer une instance Deckle dessus via le bouton Browse du wizard,
   ou via `setup-assets.ps1 -WhisperRepo <chemin>`.
3. Lancer Deckle, déclencher une transcription. Si `WhispEngine` charge
   `libwhisper.dll` sans `DllNotFoundException` et exécute un
   `whisper_full` complet, les 8 fichiers sont cohérents.
4. Vérifier `LogWindow` source `Setup` puis `Engine` — pas d'erreur de
   chargement, log `whisper_init_with_params_no_state` OK.

Si `ggml-vulkan.dll` ne charge pas, c'est presque toujours un mismatch
MinGW : la libstdc++ embarquée doit correspondre à celle du compilateur
qui a produit `ggml-vulkan.dll`. Reconstruire les DLLs MinGW depuis le
même Scoop install que le compilateur et republier.
