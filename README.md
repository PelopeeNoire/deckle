# whisp-ui

Local-first voice-to-clipboard transcription utility for Windows. Press a
hotkey, talk, release — the transcription lands in the clipboard, ready to
paste anywhere.

Built on [whisper.cpp](https://github.com/ggerganov/whisper.cpp) with Vulkan
GPU acceleration. WinUI 3 UI, .NET 10, Windows App SDK 1.8. No cloud
dependency, no account, no telemetry leaving the machine.

> Status — **personal project, early public release.** Tested on a single
> Windows 11 machine. Not packaged for the Microsoft Store. Build from source
> with the instructions below.

---

## What it does

- **Hotkey-driven recording.** Three configurable global hotkeys (default:
  the key just left of `1`) toggle audio capture from the system microphone.
- **Local Whisper transcription.** Audio is decoded by `libwhisper` (whisper.cpp)
  with the user's chosen model (`ggml-base`, `ggml-large-v3`, etc.). Vulkan
  backend by default; CPU fallback if no GPU is available.
- **Clipboard write + optional auto-paste.** The raw transcription is written
  to the clipboard. If the foreground window has a text-editable focus, a
  `Ctrl+V` is injected. If not, the text just sits on the clipboard for
  manual paste.
- **Optional LLM rewrite via Ollama.** A configurable profile can post-process
  the raw transcription through a local Ollama instance (Mistral/Ministral,
  Llama, Qwen, Gemma, Phi families supported). Off by default.
- **HUD.** A small overlay window shows recording state, elapsed time, and
  microphone level while the hotkey is held.
- **System tray.** Quit, open settings, open the live log window — all from
  the tray.

---

## What it does *not* do

- It does not upload audio anywhere. Everything stays on the machine.
- It does not auto-update. Builds are manual.
- It does not run on Windows 10. Targets Windows 11 (Windows App SDK 1.8
  minimums apply).
- It does not work on macOS or Linux. The whole stack is Win32 / WinUI 3 /
  Windows App SDK.

---

## Building from source

### Prerequisites

- **Windows 11**.
- **.NET 10 SDK** (`10.0.104` or newer ; pinned by `global.json`).
- **Visual Studio 2026 Community** with the *WinUI application development*
  workload — the build relies on its `MSBuild.exe` (Framework runtime). The
  Build CLI in `dotnet build` currently breaks WinUI 3 XAML compilation, see
  the *Build* section in [`CLAUDE.md`](CLAUDE.md) for the technical detail.
- **whisper.cpp DLLs** in `native/whisper/` and **MinGW runtime DLLs** in
  `native/mingw/`. These are not redistributed in this repo. Rebuild
  whisper.cpp locally with the Vulkan backend and copy
  `libwhisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`,
  `ggml-vulkan.dll` from its `build/bin/` into `native/whisper/`. Copy the
  three MinGW runtime DLLs (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`,
  `libwinpthread-1.dll`) into `native/mingw/`. The script
  `scripts/restore-assets.ps1` automates this once you have whisper.cpp
  cloned and built next to the repo.
- **A Whisper model** in `models/`. Download `ggml-base.bin` from
  HuggingFace (`ggerganov/whisper.cpp`) for a small starting model.
- **Vulkan SDK** for GPU acceleration. Optional — whisper.cpp falls back to
  CPU if no Vulkan runtime is present.
- **Ollama** for LLM rewrite. Optional — the rewrite feature is off by
  default and the app works without it.

### Build & run

From `src/WhispUI/`, in PowerShell (no admin needed) :

```powershell
& "<msbuild-path>" -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

The output is a self-contained executable at
`src/WhispUI/bin/x64/Release/net10.0-windows10.0.19041.0/WhispUI.exe`.

The `scripts/build-run.ps1` helper resolves `MSBuild.exe` automatically
(via `$env:WHISPUI_MSBUILD` or `vswhere`) and launches the resulting exe.

---

## Repository layout

```
<repo-root>/
├── src/WhispUI/        WinUI 3 app — single entry point. Per-subsystem
│                       documentation in docs/reference--*.md.
├── scripts/            build-run.ps1, publish.ps1, restore-assets.ps1
├── benchmark/          Python benchmark suite for prompt tuning. Optional.
├── docs/               Top-level docs (security review, etc.)
├── native/             whisper.cpp + MinGW runtime DLLs (gitignored, see
│                       the Building section above).
├── models/             Whisper models (gitignored, large binary files).
├── whisper.cpp/        whisper.cpp source clone, used to build the
│                       native DLLs (gitignored).
└── LICENSE, README.md, SECURITY.md, NOTICE.md
```

---

## Security & privacy

This app uses several Windows surfaces that are worth knowing about :

- A **global hotkey** (`RegisterHotKey`, not a low-level keyboard hook) is
  active while the app runs.
- The clipboard is written and a `Ctrl+V` is **injected** via `SendInput`
  on the foreground window after a UI Automation check (refuses to paste
  if the focused element is not text-editable, refuses to paste into
  whisp-ui itself).
- An **autostart entry** (HKCU `\Software\Microsoft\Windows\CurrentVersion\Run`)
  is written when the user enables the option. No UAC, user-scope only.
- Telemetry is **strictly opt-in** — four explicit consent dialogs gate
  application logs, audio corpus, microphone summaries, and corpus
  collection. All artifacts stay local. Nothing is uploaded anywhere.

For the full security posture, see [SECURITY.md](SECURITY.md). For the
audit that preceded the public release, see
[docs/security-review--pre-publication--0.1.md](docs/security-review--pre-publication--0.1.md).

To report a security issue, see [SECURITY.md](SECURITY.md).

---

## Acknowledgements

- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) by Georgi Gerganov
  and contributors — the speech recognition engine.
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and
  [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml) by Microsoft.
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
  for `SettingsCard` and friends.
- [Win2D](https://github.com/microsoft/Win2D) for the HUD's procedural
  graphics.
- [Vulkan SDK](https://www.lunarg.com/vulkan-sdk/) by LunarG for GPU
  inference.

See [NOTICE.md](NOTICE.md) for full third-party attributions.

---

## License

MIT — see [LICENSE](LICENSE).
