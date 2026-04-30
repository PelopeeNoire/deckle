# `scripts/` — PowerShell entry points and helpers

All scripts target PowerShell 7+ and are meant to be run from the
repo root (the launcher resolves paths relatively to itself, so the
working directory does not actually matter).

## Entry points (run these)

| Script | What it does | When to use it |
|---|---|---|
| [`launcher.ps1`](launcher.ps1) | Two-step interactive menu — step 1 picks a worktree (auto-skipped when only one exists), step 2 picks an action (Build & run Debug, Build & run Release, Build only, Setup assets, Quit). Delegates to the relevant script. | Daily driver. F5 in VSCodium also points here (see `.vscode/launch.json`). |
| [`build-run.ps1`](build-run.ps1) | Resolves `MSBuild.exe` (via `-MsBuild`, `$env:DECKLE_MSBUILD`, or `vswhere`), kills any running `Deckle.exe`, builds via VS MSBuild Framework runtime (working around the `dotnet build` XamlCompiler MSB3073 bug), and launches the resulting exe. | Direct CLI use, launch.json profiles, anything that needs a non-interactive build. Switches: `-Configuration Debug\|Release`, `-NoRun`, `-Wait`, `-Restore`, `-MsBuild <path>`, `-Target <worktree-path>`, `-Pick`. |
| [`setup-assets.ps1`](setup-assets.ps1) | Populates `<UserDataRoot>\native\` and `<UserDataRoot>\models\` (default `%LOCALAPPDATA%\Deckle\`) with the native runtime DLLs and Whisper models. Idempotent. | Fresh clone, populating a fresh data root, or refreshing the models. See **Native runtime** below for the three modes. |
| [`publish-native-runtime.ps1`](publish-native-runtime.ps1) | Builds the versioned native runtime zip (8 DLLs + `PROVENANCE.txt` + `SHA256SUMS`) from a local whisper.cpp build tree, computes the SHA256 the wizard hardcodes in `NativeRuntime.CurrentBundle`, and (with `-Publish`) uploads it as a `native-vX.Y.Z` GitHub Release on the Deckle repo. | Maintainer-only. Run after every whisper.cpp upstream bump or toolchain change. Recipe: [`src/Deckle/docs/reference--native-runtime--1.0.md`](../src/Deckle/docs/reference--native-runtime--1.0.md). |

## Native runtime — three modes

`setup-assets.ps1` provisions the 8 DLLs (5 whisper.cpp Vulkan + 3
MinGW C++ runtime) through one of three paths, in priority order:

1. **`-FromRelease <X.Y.Z>` (recommended for non-rebuilders).** Fetches
   `deckle-native-<X.Y.Z>.zip` from the Deckle GitHub Release and
   extracts the catalog DLLs in place. No local whisper.cpp clone
   required. Same source as the first-run wizard's auto-download path.

2. **`-WhisperRepo <path>` (for whisper.cpp rebuilders).** Copies DLLs
   from a local whisper.cpp build tree (`<path>\build\bin\` plus the
   MinGW runtime from Scoop). Use this when iterating on the whisper.cpp
   source — recompile, point the script at your tree, the bundle on
   `<UserDataRoot>` is refreshed without going through GitHub. Falls
   back to `$env:DECKLE_WHISPER_REPO` and then to a sibling
   `<repo>\..\whisper.cpp` clone.

3. **Skip.** When neither path resolves to a valid build tree, the
   native step is skipped with a warning. Useful when only models
   need refreshing on a machine without a build tree.

In all three cases, the Whisper + Silero VAD models are still pulled
from HuggingFace afterwards.

## Modules (imported, never run directly)

| File | Purpose |
|---|---|
| [`_menu.psm1`](_menu.psm1) | PowerShell module exposing two functions: `Select-Worktree` (lists `git worktree list` entries, returns the chosen path; auto-resolves when only one exists) and `Select-Action` (generic Label/Value picker). Both share the same arrow-key controls (Up/Down to navigate, Enter to confirm, Esc to cancel) and the same in-place repaint logic. The leading `_` is a convention meaning "not an entry point" — `launcher.ps1` and `build-run.ps1 -Pick` import it automatically when needed. **Do not run this file directly.** |

## Architecture in one sentence

`launcher.ps1` orchestrates `setup-assets.ps1` and `build-run.ps1` via
the interactive picker provided by `_menu.psm1`; `build-run.ps1` keeps
its own non-interactive CLI for VSCodium / launch.json / scripted use.
`publish-native-runtime.ps1` is a maintainer-only side track that feeds
both `setup-assets.ps1 -FromRelease` and the in-app wizard's auto-DL.

## What is *not* here

- **App installer / MSIX.** Deckle is distributed as source only —
  there is no `Deckle.exe` GitHub Release. Build via `build-run.ps1`
  (or the launcher) and run from the build output.
- **CI / GitHub Actions.** None for now — personal project. The native
  runtime publish flow is run manually by the maintainer.
- **Source mirror of whisper.cpp.** The repo no longer carries a
  `whisper.cpp/` clone. Rebuilders clone it themselves alongside the
  Deckle repo (recipe in [`src/Deckle/docs/reference--native-runtime--1.0.md`](../src/Deckle/docs/reference--native-runtime--1.0.md))
  and point `-WhisperRepo` at it.
