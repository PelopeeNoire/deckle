# `scripts/` — PowerShell entry points and helpers

All scripts target PowerShell 7+ and are meant to be run from the
repo root (the launcher resolves paths relatively to itself, so the
working directory does not actually matter).

## Entry points (run these)

| Script | What it does | When to use it |
|---|---|---|
| [`launcher.ps1`](launcher.ps1) | Two-step interactive menu — step 1 picks a worktree (auto-skipped when only one exists), step 2 picks an action (Build & run Debug, Build & run Release, Build only, Setup assets, Quit). Delegates to the relevant script. | Daily driver. F5 in VSCodium also points here (see `.vscode/launch.json`). |
| [`build-run.ps1`](build-run.ps1) | Resolves `MSBuild.exe` (via `-MsBuild`, `$env:WHISPUI_MSBUILD`, or `vswhere`), kills any running `WhispUI.exe`, builds via VS MSBuild Framework runtime (working around the `dotnet build` XamlCompiler MSB3073 bug), and launches the resulting exe. | Direct CLI use, launch.json profiles, anything that needs a non-interactive build. Switches: `-Configuration Debug\|Release`, `-NoRun`, `-Wait`, `-Restore`, `-MsBuild <path>`, `-Target <worktree-path>`, `-Pick`. |
| [`setup-assets.ps1`](setup-assets.ps1) | Populates `<UserDataRoot>\native\` and `<UserDataRoot>\models\` (default `%LOCALAPPDATA%\WhispUI\`) with the whisper.cpp DLLs, MinGW C++ runtime, and Whisper models. Idempotent. | Fresh clone, after a `whisper.cpp` rebuild, or to download a missing model. Switches: `-DataRoot <path>`, `-AlsoInRepo` (also populate `<repo>/native` and `/models`), `-WithLarge` (fetch `ggml-large-v3.bin` ~3 GB), `-Force`. |

## Modules (imported, never run directly)

| File | Purpose |
|---|---|
| [`_menu.psm1`](_menu.psm1) | PowerShell module exposing two functions: `Select-Worktree` (lists `git worktree list` entries, returns the chosen path; auto-resolves when only one exists) and `Select-Action` (generic Label/Value picker). Both share the same arrow-key controls (Up/Down to navigate, Enter to confirm, Esc to cancel) and the same in-place repaint logic. The leading `_` is a convention meaning "not an entry point" — `launcher.ps1` and `build-run.ps1 -Pick` import it automatically when needed. **Do not run this file directly.** |

## Architecture in one sentence

`launcher.ps1` orchestrates `setup-assets.ps1` and `build-run.ps1` via
the interactive picker provided by `_menu.psm1`; `build-run.ps1` keeps
its own non-interactive CLI for VSCodium / launch.json / scripted use.

## What is *not* here

- **Publish / installer scripts.** WhispUI is currently distributed as
  source only; there is no MSIX nor a self-contained installer. Build
  via `build-run.ps1` (or the launcher) and run from the build output.
- **CI / GitHub Actions.** None for now — personal project.
- **Asset publish to a remote.** All assets resolve from the local
  user data root; nothing is uploaded anywhere.
