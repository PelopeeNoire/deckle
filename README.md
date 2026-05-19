# Deckle

Local-first Windows utility that bundles personal productivity tools into a
single system-tray app. No cloud dependency, no account, no telemetry leaving
the machine.

Built with WinUI 3, .NET 10, and Windows App SDK 1.8. Targets Windows 11.

> Status — **personal project, early public release.** Tested on two
> Windows 11 machines. Not packaged for the Microsoft Store. Build from source
> with the instructions below.

---

## Subsystems

### Voice transcription (Whisp)

Press a hotkey, talk, release — the transcription lands in the clipboard,
ready to paste anywhere.

- **Hotkey-driven recording.** Three configurable global hotkeys (default:
  the key just left of `1`) toggle audio capture from the system microphone.
- **Local Whisper transcription.** Audio is decoded by `libwhisper`
  ([whisper.cpp](https://github.com/ggerganov/whisper.cpp)) with the user's
  chosen model (`ggml-base`, `ggml-large-v3`, etc.). Vulkan backend by
  default; CPU fallback if no GPU is available.
- **Clipboard write + optional auto-paste.** The raw transcription is written
  to the clipboard. If the foreground window has a text-editable focus, a
  `Ctrl+V` is injected. If not, the text sits on the clipboard for manual
  paste.
- **Optional LLM rewrite via Ollama.** A configurable profile can
  post-process the raw transcription through a local Ollama instance. Off by
  default.

### Ambient lighting

Captures the screen in real-time via DXGI Output Duplication and drives
Philips Hue lights to match the dominant colors, with per-zone mapping and
HDR support.

- **DXGI capture backend.** No yellow capture border (unlike
  `Windows.Graphics.Capture`). Minimal latency, GPU-side frame copy.
- **Color science pipeline.** Gamut clipping, linear-light averaging, OKLCh
  saturation boost. Accurate color reproduction on any display profile.
- **Multi-zone support.** Map screen regions to individual Hue lights. Auto-
  discovery of the Hue Entertainment group.
- **Hue Entertainment API v2.** Direct UDP streaming to Hue Bridge — no
  third-party NuGet, no cloud relay.

### Shared infrastructure

- **HUD overlay.** A small topmost window shows recording state, elapsed
  time, and microphone level while the hotkey is held.
- **System tray.** Quit, open settings, toggle ambient lighting, open the
  live log window — all from the tray.
- **Settings.** NavigationView-based settings UI with per-module pages,
  auto-save, and consent dialogs for telemetry.

---

## What it does *not* do

- It does not upload audio or screen captures anywhere.
- It does not auto-update. Builds are manual.
- It does not run on Windows 10. Targets Windows 11.
- It does not work on macOS or Linux. The stack is Win32 / WinUI 3 /
  Windows App SDK.

---

## Building from source

### Prerequisites

The fastest path from a fresh Windows 11 machine is the bootstrap script:

```powershell
scripts/lib/bootstrap-dev-env.ps1           # Tier 1 (managed build)
scripts/lib/bootstrap-dev-env.ps1 -Full     # Tier 1 + native recompile + Ollama
```

It probes what is already installed, installs the missing pieces via winget
and Scoop, sets the required environment variables, and invokes
`setup-assets.ps1` to provision the runtime data. Run with `-DryRun` first
to see the plan without installing anything. The same flow is reachable
via the interactive menu at `scripts/deckle.ps1` (Setup → Bootstrap dev
environment).

#### Tier 1 — build & run Deckle (sufficient for C# / XAML work)

- **Windows 11.**
- **.NET 10 SDK** (pinned by `global.json` with `rollForward: latestFeature`).
- **Visual Studio 2026 Community** with the *WinUI application development*
  workload. The build relies on its `MSBuild.exe` (Framework runtime) because
  `dotnet build` currently breaks WinUI 3 XAML compilation — see the
  *Build* section in [`CLAUDE.md`](CLAUDE.md) for the technical detail.

#### Tier 2 — recompile whisper.cpp native DLLs (rare, maintainer-only)

- **Scoop** (`scoop.sh`).
- **MinGW** (`scoop install mingw`) — GCC 15.2, C++ toolchain.
- **CMake** and **Ninja** (`scoop install cmake ninja`).
- **Vulkan SDK** (`scoop install vulkan`) — headers for the `ggml-vulkan`
  backend.
- A local clone of [whisper.cpp](https://github.com/ggerganov/whisper.cpp)
  outside this repo. See
  [`docs/reference--native-runtime--1.0.md`](docs/reference--native-runtime--1.0.md)
  for the full recipe.

#### Optional

- **Ollama** for LLM rewrite (`winget install --id Ollama.Ollama -e`). Off
  by default.
- **GitHub CLI** for auth and PR workflows
  (`winget install --id GitHub.cli -e`).

### Fresh clone — first run

1. Run `scripts/lib/bootstrap-dev-env.ps1` (installs prerequisites + provisions
   `%LOCALAPPDATA%\Deckle\` with native DLLs and Whisper models).
2. Open a **new terminal** (environment variables set by the bootstrap are
   only visible in new sessions).
3. Build & run via `scripts/deckle.ps1` (interactive menu), or directly:
   ```powershell
   scripts/lib/build-run.ps1 -Configuration Release
   ```
4. Alternatively, open the solution in Visual Studio 2026 and press F5.

### Scripts

The single interactive entry point is `scripts/deckle.ps1` — F5 in
VSCodium points there. Worker scripts live under `scripts/lib/` and
stay callable on their own CLI:

| Script | Purpose |
|--------|---------|
| `scripts/deckle.ps1` | Interactive menu: build, clean, stats, setup, bootstrap, publish. |
| `scripts/lib/build-run.ps1` | Build via VS MSBuild + launch. Resolves MSBuild automatically. |
| `scripts/lib/clean.ps1` | Remove `bin/` + `obj/` under every `src/<module>/`. |
| `scripts/lib/stats.ps1` | Per-module file + LOC stats (`.cs` / `.xaml` / `.resw`). |
| `scripts/lib/setup-assets.ps1` | Provision `%LOCALAPPDATA%\Deckle\` with native DLLs and Whisper models. |
| `scripts/lib/bootstrap-dev-env.ps1` | Probe + install dev dependencies (winget, Scoop, VS, .NET SDK). |
| `scripts/lib/publish-native-runtime.ps1` | Maintainer-only: zip + publish a `native-vX.Y.Z` GitHub release. |

See [`scripts/README.md`](scripts/README.md) for the full menu structure
and per-script switches.

---

## Run at startup

In the app, go to **Settings → General → Launch at startup** and toggle it
on. Two related options:

- **Start minimized** — the app lives in the system tray.
- **Warm up on launch** — runs a short dummy transcription so the first
  real hotkey press skips the cold-start cost.

Under the hood this writes a user-scope entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Deckle`. No UAC, no
service, nothing machine-wide.

---

## Repository layout

```
<repo-root>/
├── src/
│   ├── Deckle/                 WinUI 3 app host — entry point, windows, tray
│   ├── Deckle.Core/            Foundations (AppPaths, JsonSettingsStore, Win32 interop)
│   ├── Deckle.Logging/         Central telemetry hub (TelemetryService + sinks)
│   ├── Deckle.Catalog/    ResourceLoader facade (x:Uid pattern)
│   ├── Deckle.Audio/           Microphone capture (WASAPI, RMS, calibration)
│   ├── Deckle.Chrono/          Timer primitive (no UI)
│   ├── Deckle.Chrono.Hud/      UserControl for the stopwatch HUD
│   ├── Deckle.Composition/     Direct2D + Composition primitives (ColorSpace, easing)
│   ├── Deckle.Vision/          Screen capture (DXGI Output Duplication)
│   ├── Deckle.Lighting/        LED driver abstraction (ILightOutput)
│   ├── Deckle.Lighting.Ambient/ Ambient lighting consumer (Vision + Lighting → Hue)
│   ├── Deckle.Shell/           System shell (tray, hotkeys, autostart, message-only host)
│   ├── Deckle.Settings/        Settings UI shell + per-module persistence
│   ├── Deckle.Llm/             LLM rewrite via Ollama
│   └── Deckle.Whisp/           Whisper transcription pipeline
├── scripts/                    Build, publish, setup, launcher (deckle.ps1 + lib/)
├── docs/                       Reference sheets and research notes
├── benchmark/                  Python benchmark suite (optional, to be extracted)
└── LICENSE, README.md, SECURITY.md, NOTICE.md
```

Native DLLs and Whisper models do **not** live in the repository. They are
provisioned at dev time by `scripts/lib/setup-assets.ps1` into
`%LOCALAPPDATA%\Deckle\` and at user time by the first-run wizard.

---

## Security & privacy

- A **global hotkey** (`RegisterHotKey`, not a low-level keyboard hook) is
  active while the app runs.
- The clipboard is written and a `Ctrl+V` is injected via `SendInput` after
  a UI Automation check (refuses if the focused element is not text-editable).
- Screen capture via **DXGI Output Duplication** runs only while ambient
  lighting is active. Frames are processed in GPU memory and never written
  to disk.
- An **autostart entry** (HKCU Run key) is written when the user enables it.
  User-scope only.
- Telemetry is **strictly opt-in** — explicit consent dialogs gate each
  channel. All artifacts stay local.

For the full security posture, see [SECURITY.md](SECURITY.md). For the audit
that preceded the public release, see
[`docs/security-review--pre-publication--0.1.md`](docs/security-review--pre-publication--0.1.md).

---

## Acknowledgements

- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) by Georgi Gerganov
  and contributors — speech recognition engine.
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and
  [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml) by Microsoft.
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
  for `SettingsCard` and friends.
- [Win2D](https://github.com/microsoft/Win2D) for the HUD's procedural
  graphics.
- [Vulkan SDK](https://www.lunarg.com/vulkan-sdk/) by LunarG for GPU
  inference.
- [Philips Hue](https://developers.meethue.com/) Entertainment API for
  ambient light streaming.

See [NOTICE.md](NOTICE.md) for full third-party attributions.

---

## License

MIT — see [LICENSE](LICENSE).
