# AGENT.md — Deckle

Instructions for AI agents working on this codebase. Model-agnostic — applies
to Claude, Copilot, Cursor, or any LLM-based coding assistant.

For Claude-specific doctrine (skills, MCP servers, pedagogy, tone), see
[`CLAUDE.md`](CLAUDE.md).

---

## Non-negotiable rules

1. **Never build or publish.** Do not run `build-run.ps1`, `MSBuild.exe`,
   `dotnet build`, or any variant. The maintainer builds and validates
   runtime. Stop at the summary of changes.
2. **No Co-Authored-By trailers.** Commits go under the maintainer's identity
   only. No `Co-Authored-By: <agent>` lines, no `Generated with` footers.

---

## Scripts — what to run and when

All scripts live under `scripts/`. Run from the repo root in PowerShell 7+.

| Script | When to use | Safe to run? |
|--------|-------------|:---:|
| `bootstrap-dev-env.ps1` | Fresh machine setup — installs VS, .NET SDK, Scoop toolchain, provisions runtime assets. `-DryRun` to preview. `-Full` for the native recompile toolchain. | Yes |
| `setup-assets.ps1` | Populate `%LOCALAPPDATA%\Deckle\` with native DLLs and Whisper models. `-FromRelease X.Y.Z` for published bundles. | Yes |
| `launcher.ps1` | Interactive picker: select a worktree then an action (build, run, setup). | **No** (builds) |
| `build-run.ps1` | Build + launch Deckle. | **No** (builds) |
| `publish-unpackaged.ps1` | Self-contained publish. | **No** (builds) |
| `publish-native-runtime.ps1` | Zip + push a `native-vX.Y.Z` release. | **No** (publishes) |

---

## Module dependency graph

Leaves first, app host last. Arrows mean "depends on".

```
Deckle.Core
Deckle.Logging          → Core
Deckle.Localization     → Core
Deckle.Audio            → Core, Logging
Deckle.Chrono           (standalone)
Deckle.Composition      (standalone)
Deckle.Vision           → Core, Logging
Deckle.Lighting         → Core, Logging
Deckle.Chrono.Hud       → Chrono, Composition
Deckle.Shell            → Core, Logging
Deckle.Settings         → Core, Logging, Localization
Deckle.Llm              → Core, Logging
Deckle.Whisp            → Core, Logging, Audio, Llm
Deckle.Lighting.Ambient → Core, Logging, Vision, Lighting
Deckle                  → all of the above (app host, WinUI 3 entry point)
```

Dependencies are acyclic. Each module is one csproj. Sub-namespaces within a
module are used when internal structure warrants it.

---

## Where documentation lives

| Location | What | When to read |
|----------|------|--------------|
| `CLAUDE.md` (root) | Project doctrine, build environment, conventions, dev toolchain | Always loaded by Claude Code |
| `src/<Module>/CLAUDE.md` | Per-module contracts, pitfalls, internal architecture | Before modifying that module |
| `docs/reference--*.md` | Canonical reference sheets per subsystem | Before modifying the subsystem they cover |
| `docs/architecture--*.md` | Architecture decision records | When touching the systems they describe |
| `docs/research--*.md` | Dated research notes (context for past decisions) | When revisiting those decisions |
| `docs/security-review--*.md` | Pre-publication security audit | Before changes to security-sensitive surfaces |
| `README.md` | Public-facing overview, setup guide, repo layout | For onboarding / context |

### Modules with internal CLAUDE.md

Six of fifteen modules have non-trivial contracts documented:

- `src/Deckle/CLAUDE.md` — app host lifetime, WinUI 3 pitfalls, build commands
- `src/Deckle.Audio/CLAUDE.md` — WASAPI capture, circular buffers, RMS
- `src/Deckle.Logging/CLAUDE.md` — TelemetryService singleton, sink architecture
- `src/Deckle.Settings/CLAUDE.md` — NavigationView shell, SettingsHost, modular pages
- `src/Deckle.Whisp/CLAUDE.md` — transcription pipeline, segment callback, VAD, hot-reload

The remaining nine modules (Core, Chrono, Chrono.Hud, Composition, Vision,
Lighting, Lighting.Ambient, Llm, Localization, Shell) are either
straightforward or still being scaffolded.

---

## Build environment

- **Windows 11** (target SDK `10.0.26100.0`, min `10.0.17763.0`).
- **.NET 10** (`global.json` pins minimum `10.0.104`, `rollForward: latestFeature`).
- **Visual Studio 2026 Community** with *WinUI application development*
  workload — provides MSBuild Framework (`MSBuildRuntimeType=Full`).
  `dotnet build` is broken due to the XamlCompiler MSB3073 bug.
- **Windows App SDK** `1.8.260317003` (stable, pinned in all csproj files).

### Native recompile toolchain (optional)

Only needed to rebuild the whisper.cpp DLLs:

- GCC 15.2.0 via MinGW (Scoop)
- CMake 4.3.1, Ninja 1.13.2
- Vulkan SDK 1.4.341.1 (env var `VULKAN_SDK`)
- Recipe: [`docs/reference--native-runtime--1.0.md`](docs/reference--native-runtime--1.0.md)

---

## Coding conventions

- **Language:** all code and UI in English. Conversation with the maintainer
  in French.
- **Theme resources over magic values.** No hardcoded `#xxxxxx`, no numeric
  `CornerRadius`, no manual `BoxShadow`. Use Windows theme resources
  (`LayerFillColorDefaultBrush`, `OverlayCornerRadius`, etc.).
- **Logging:** all telemetry goes through `TelemetryService.Instance`. No
  parallel logging paths. Consult
  [`docs/reference--logging-inventory--1.0.md`](docs/reference--logging-inventory--1.0.md)
  before adding log lines.
- **Animations:** linear by default. No custom easing without explicit
  request (exception: HUD/overlay subsystem uses cubic ease-out 150ms).
