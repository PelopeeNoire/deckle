# `scripts/` — Deckle dev workflows

All scripts target PowerShell 7+. The single entry point lives at
[`deckle.ps1`](deckle.ps1); the worker scripts it dispatches to live
under [`lib/`](lib/) and stay usable on their own CLI for automation.

## Entry point — `deckle.ps1`

`deckle.ps1` is what F5 runs in VSCodium (see
[`.vscode/launch.json`](../.vscode/launch.json)) and what you call from
a terminal for daily work. It opens an arrow-key menu grouping every
dev action by purpose:

| Section | Action | Per-worktree? | Delegates to |
|---|---|:---:|---|
| **Build** | Build & run (Debug) | yes | `lib/build-run.ps1 -Configuration Debug` |
|  | Build & run (Release) | yes | `lib/build-run.ps1 -Configuration Release` |
|  | Build only (no run) | yes | `lib/build-run.ps1 -Configuration Release -NoRun` |
| **Worktree maintenance** | Clean bin/obj | yes | `lib/clean.ps1` |
|  | Stats (LOC, files) | yes | `lib/stats.ps1` |
| **Setup** | Setup assets (UserDataRoot) | no | `lib/setup-assets.ps1` |
|  | Bootstrap dev environment | no | `lib/bootstrap-dev-env.ps1` |
| **Maintainer** | Publish native runtime | no | `lib/publish-native-runtime.ps1` |

Per-worktree actions prompt for a worktree right after the action is
picked (auto-resolved when only the main repo exists). Global actions
go straight to a short parameter prompt where needed.

## Worker scripts — `lib/`

Each worker is callable directly from a terminal or a `launch.json`
profile — `deckle.ps1` is purely additive.

| File | Purpose | Common switches |
|---|---|---|
| [`lib/build-run.ps1`](lib/build-run.ps1) | Resolve MSBuild (via `-MsBuild`, `$env:DECKLE_MSBUILD`, or `vswhere`), kill running `Deckle.exe`, build via VS MSBuild Framework runtime (works around the `dotnet build` XamlCompiler MSB3073 bug), and launch the freshly built exe through `cmd /c start`. | `-Configuration Debug\|Release`, `-NoRun`, `-Wait`, `-MsBuild <path>`, `-Target <worktree>`, `-Pick`, `-NoAutoRestart` |
| [`lib/clean.ps1`](lib/clean.ps1) | Remove every `bin/` and `obj/` directory directly under `src/<module>/`. Guards against symlinks / junctions (won't recurse into a reparse point and risk nuking its target). Reports total freed bytes. | `-Target <worktree>`, `-Pick` |
| [`lib/stats.ps1`](lib/stats.ps1) | Walk every `.csproj` under `src/` and tally file counts (`.cs` / `.xaml` / `.xaml.cs` / `.resw`) plus LOC. Excludes `bin/obj/.vs/Properties` and generated files (`*.g.cs`, `*.g.i.cs`, `*.xaml.g.cs`). | `-Target <worktree>`, `-Pick`, `-Detailed`, `-Json <path>` |
| [`lib/setup-assets.ps1`](lib/setup-assets.ps1) | Populate `<UserDataRoot>\native\` and `<UserDataRoot>\models\` with the whisper.cpp DLLs, MinGW C++ runtime, and Whisper / Silero VAD models. Idempotent. See *Native runtime* below for the three sourcing modes. | `-DataRoot <path>`, `-FromRelease X.Y.Z`, `-WhisperRepo <path>`, `-WithLarge`, `-Force` |
| [`lib/bootstrap-dev-env.ps1`](lib/bootstrap-dev-env.ps1) | Provision a fresh Windows 11 machine: winget (VS 2026, .NET 10, git, gh), optional scoop Tier 2 (MinGW, CMake, Ninja, Vulkan SDK, Ollama). Probes existing state, builds a plan, asks for confirmation, then executes. Calls `setup-assets.ps1` in the final step. | `-DryRun`, `-Full`, `-Yes`, `-SkipAssets`, `-AssetsRelease X.Y.Z` |
| [`lib/publish-native-runtime.ps1`](lib/publish-native-runtime.ps1) | **Maintainer-only.** Assemble the native runtime zip (8 DLLs + `PROVENANCE.txt` + `SHA256SUMS`) from a local whisper.cpp build tree, optionally publish it to GitHub Release as `native-vX.Y.Z`. | `-Version X.Y.Z`, `-WhisperRepo <path>`, `-OutDir <path>`, `-Publish`, `-Notes <path>` |
| [`lib/_menu.psm1`](lib/_menu.psm1) | Module exposing `Select-Worktree` (lists `git worktree list`, returns the chosen path) and `Select-Action` (Label/Value picker with optional `IsHeader` section dividers). Up/Down navigates, Enter confirms, Esc cancels. Imported by `deckle.ps1`, `build-run.ps1 -Pick`, `clean.ps1 -Pick`, `stats.ps1 -Pick`. **Not an entry point.** |

## Native runtime — three sourcing modes

`lib/setup-assets.ps1` provisions the 8 native DLLs (5 whisper.cpp
Vulkan + 3 MinGW C++ runtime) through one of three paths:

1. **`-FromRelease <X.Y.Z>` (default for non-rebuilders).** Fetches
   `deckle-native-<X.Y.Z>.zip` from the Deckle GitHub Release and
   extracts the catalog DLLs in place. No local whisper.cpp clone
   needed. Same source as the first-run wizard's auto-download path.

2. **`-WhisperRepo <path>` (for whisper.cpp rebuilders).** Copies DLLs
   from a local whisper.cpp build tree (`<path>\build\bin\` plus the
   MinGW runtime from Scoop). Use when iterating on whisper.cpp source
   — recompile, point the script at your tree, the bundle on
   `<UserDataRoot>` refreshes without going through GitHub. Falls back
   to `$env:DECKLE_WHISPER_REPO` and then to a sibling
   `<repo>\..\whisper.cpp` clone.

3. **Skip.** When neither path resolves to a valid build tree, the
   native step is skipped with a warning. Useful when only models need
   refreshing on a machine without a build tree.

The Whisper + Silero VAD models are always pulled from HuggingFace
afterwards (regardless of mode).

## Post-build HUD topmost mitigation

`lib/build-run.ps1` passes `--post-build` to the launched
`Deckle.exe` by default. The app finishes its boot, waits ~800ms,
then re-launches itself once via `cmd /c start`, then exits. The
second instance inherits a clean foreground state and the HUD's
`WS_EX_TOPMOST` flag applies correctly on the first recording.
Disable with `-NoAutoRestart` if you need a stable PID (attached
debugger, log capture). See `App.RestartViaShellExecute()` and the
`--post-build` parsing in `App.OnLaunched`.

This is a workaround for the visible symptom only — the underlying
topmost-loss behaviour (HUD loses topmost when another window grabs
foreground, especially other WinUI 3 apps) is a separate investigation
gated on the EventSource logging refactor.

## What is *not* here

- **App installer / MSIX.** Deckle is distributed as source only —
  there is no `Deckle.exe` GitHub Release. Build via `lib/build-run.ps1`
  (or the launcher) and run from the build output.
- **CI / GitHub Actions.** None for now — personal project. The native
  runtime publish flow is run manually by the maintainer.
- **Source mirror of whisper.cpp.** The repo no longer carries a
  `whisper.cpp/` clone. Rebuilders clone it themselves alongside the
  Deckle repo (recipe in
  [`../docs/reference--native-runtime--1.0.md`](../docs/reference--native-runtime--1.0.md))
  and point `-WhisperRepo` at it.
