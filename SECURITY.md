# Security policy

## Reporting a vulnerability

If you find a security issue in whisp-ui, please report it privately rather
than opening a public issue. Open a GitHub Security Advisory on this
repository (Security tab → "Report a vulnerability"). Please include :

- a description of the issue and the affected component,
- a minimal reproducer if you have one (steps, configuration, model used),
- the version (commit SHA) where you observed the issue.

There is no formal SLA — this is a personal project — but reports will be
acknowledged on a best-effort basis.

---

## Threat model and known surfaces

whisp-ui is a Windows desktop application that interacts with several
sensitive system surfaces deliberately. None of these are vulnerabilities ;
they are listed here so that anyone installing the app can understand
exactly what they are running.

### Global hotkey

The app registers up to three global hotkeys with `RegisterHotKey`. This is
the standard high-level Win32 hotkey API — the OS validates the combination
before delivering it. The app does **not** install a low-level keyboard
hook (`WH_KEYBOARD_LL`), so it cannot observe other key presses. There is
no keylogger surface.

### Clipboard write and synthetic paste

When a transcription completes, the app writes the resulting text to the
system clipboard (`SetClipboardData(CF_UNICODETEXT)`) and then synthesizes
a `Ctrl+V` keystroke via `SendInput` on the foreground window, after two
defenses :

- **Self-PID check** — the paste is refused if the foreground window
  belongs to whisp-ui itself.
- **UI Automation focus check** — the paste is refused if the focused
  element's `ControlType` is anything other than `Edit` or `Document`.

The clipboard write is verified by an immediate read-back; a mismatch is
logged.

### Autostart

The "Start with Windows" toggle writes
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Deckle` with the
quoted full path of the executable. Per-user (no UAC), no path traversal
(the value is the verified `Environment.ProcessPath`), and the entry is
removed only if it still points to the current install (so multiple
installs of the same binary cohabit cleanly).

### Telemetry and corpus collection (opt-in)

Four separate consent dialogs gate any persistent logging beyond the live
log window :

- `ApplicationLogConsentDialog` — application events to
  `<storage>/app.jsonl`.
- `AudioCorpusConsentDialog` — raw transcribed text to
  `<storage>/corpus.jsonl`, used by the benchmark suite.
- `MicrophoneTelemetryConsentDialog` — per-recording RMS summary to
  `<storage>/microphone.jsonl`.
- `CorpusConsentDialog` — additional benchmark-related corpus collection
  (audio WAV files saved to `<storage>/profiles/<slug>/`).

All of these stay strictly local. Nothing is ever uploaded, batched, or
sent off the machine. The user can revoke any consent at any time and
delete the artifacts directly from the file system (paths are shown in
the Settings page).

### LLM rewrite (opt-in)

When a profile with a model is configured, the raw transcription is sent
to a **local** Ollama instance (default `http://localhost:11434`). The app
posts the text to `/api/generate` with `raw=true`, applying its own prompt
template per model family.

Two limitations are documented as accepted risks :

- **Prompt injection.** The transcribed text is passed verbatim into the
  template. A malicious recording could in principle escape the template
  and steer the rewrite. Bounded — the rewrite is opt-in, the model is
  local with no shared state, and the original raw transcription is still
  on the clipboard when the rewrite runs.
- **SSRF posture.** The Ollama endpoint is user-configurable. The app
  enforces `http`/`https` schemes only, and warns once when the host is
  not loopback. It does *not* hard-block non-loopback hosts (some users
  legitimately run Ollama on a separate LAN machine).

### Native dependencies

Whisper inference runs in `libwhisper.dll` (a build of whisper.cpp). The
DLL is loaded by full path from `native/whisper/` next to the executable.
The Vulkan backend (`ggml-vulkan.dll`) loads the system Vulkan ICD as the
GPU driver does.

The model file is loaded with the magic-header check that whisper.cpp
performs internally — non-GGUF files are rejected without code execution.
The `DECKLE_MODEL_PATH` environment variable (if set) is validated for
absolute path + existence before being passed to whisper.

### What the app does not do

- It does not run as administrator. No elevation prompts, no `runas`.
- It does not write outside its app data folder, the configured corpus
  directory, and the autostart registry value mentioned above.
- It does not load arbitrary code from disk. Plugin-style extension is
  not supported.
- It does not phone home. There is no analytics endpoint, update check,
  or crash reporter.

---

## Pre-publication audit

A formal security review preceded this public release. See
[docs/security-review--pre-publication--0.1.md](docs/security-review--pre-publication--0.1.md)
for the full list of findings and remediations applied.
