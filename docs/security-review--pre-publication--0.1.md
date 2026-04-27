# Security review — pre-publication audit (v0.1)

Type : `specification` (prescriptive — drives the cleanup pass).
Date : 2026-04-27.
Branche : `chore/security-review-pre-publication`.

This document is the audit pivot for moving the repository from a private
working state to a public GitHub repository. It covers two angles :

1. **Repository safety** — making sure no personal data, secrets, or
   working-context leakage ships in the public repo (HEAD or git
   history).
2. **Application safety** — making sure the code itself does not pose
   undue risk to people who later install and run the app.

The findings below are the verbatim outcome of the audit performed on
2026-04-27. The action plan describes the remediations applied on this
branch. Each finding ends with a **Status** line.

---

## Executive summary

**Go/no-go for public publication after this branch lands : GO**, with
the remediations below applied and a one-shot history rewrite to purge
a leaked Anytype API token from the initial commit.

| Category | Verdict | Severity of worst finding |
|---|---|---|
| Hardcoded secrets in HEAD | Clean | — |
| Hardcoded secrets in git history | **One leaked token** | Critical (revoked, history rewrite required) |
| Personal paths / identifiers | Several occurrences in docs | Medium |
| Tracked files that should be gitignored | 3 candidates | Low |
| Personal-workflow Claude skills | 1 to remove | Low |
| Application code (Win32, network, fs) | Largely defensive | High (1 finding — env var validation) |
| Meta-publication files (LICENSE, README, SECURITY) | Missing | Blocking for publication |

No `Critical` finding remains open after the remediations described in
sections 3 and 4.

---

## 1. Findings — secrets, personal data, history

### F1 — Anytype API token leaked in initial commit (Critical → Resolved)

- **Location** — `.mcp.json`, present in commit `cceb090` (initial
  commit), removed in commit `3a39ac7`. Not present in HEAD.
- **Token** — Anytype Bearer token (`byeglbuj...JE=`), giving
  read/write access to the maintainer's Anytype workspace.
- **Discovery** — `git log --all -p | grep -E 'Bearer\s+...'`.
- **Impact** — Until revoked, anyone cloning the public repo and
  running `git log -p` could exfiltrate the workspace.
- **Remediation** — Token was revoked in Anytype settings on 2026-04-27
  prior to any code change. The history is then rewritten with
  `git filter-repo --path .mcp.json --invert-paths` to remove the
  file from every commit, so even the dead token does not appear in
  the published history.
- **Status** — `documented` (token revoked) → `fixed` after history
  rewrite phase.

### F2 — Personal paths in tracked documentation (Medium → To fix)

Hardcoded Windows paths under the maintainer's profile (`C:\Users\<user>\…`)
and machine-specific dev-tool layout (`<dev-drive>\<dev-tools>\…`) are
present in tracked docs. They leak user account name and machine layout,
and are useless to anyone else.

| File | Lines (count) | Nature |
|---|---|---|
| `src/WhispUI/CLAUDE.md` | 2 | Memory plan path + MSBuild VS path |
| `src/WhispUI/docs/brief--audit-robustesse--0.1.md` | 1 | Plan path |
| `src/WhispUI/docs/brief--packaging-foundation--0.1.md` | 1 | Plan path |
| `.claude/skills/benchmark-loop/SKILL.md` | 4 | Plan path + absolute cwd to the benchmark folder |

- **Remediation** — Replace with documentary placeholders
  (`<repo-root>/...`, `<msbuild-path>`, etc.) or restructure the
  passage so the path is not needed. The `benchmark-loop` skill is
  removed entirely (see F4).
- **Status** — `open` → `fixed`.

### F3 — Maintainer's first name in tracked text (Low → Soft-fix)

Multiple files used the maintainer's first name as a working-context cue
("X builds via...", "X observes..."). Acceptable as attribution (LICENSE,
copyright lines), but not as documentation cue. Replaced by neutral terms
("the user", "the maintainer", "we") everywhere except attribution.

Files affected (tracked) : `CLAUDE.md`, `src/WhispUI/CLAUDE.md`,
`benchmark/AGENT.md`, `benchmark/README.md`,
`src/WhispUI/docs/reference--audit-robustesse--0.1.md`,
`src/WhispUI/docs/brief--latency-instrumentation--0.1.md`. C# code
comments in `src/WhispUI/Controls/HudChrono.xaml.cs` (×6) similarly
substituted.

- **Status** — `open` → `fixed`.

### F4 — Personal-workflow skill in `.claude/skills/` (Low → Untracked)

`.claude/skills/benchmark-loop/SKILL.md` describes a one-off iterative
optimization workflow tied to a specific dated branch
(`autoresearch/llm-rewrite-pivot-lissage-20260427`). It references
private plan files and cannot be made meaningful for a public
audience without a rewrite that is not worth the effort.

- **Remediation** — Removed from git tracking
  (`git rm --cached`), added to `.gitignore` so the file remains on
  the maintainer's local copy without being published. The other
  five skills (`debug-logging-design`, `git-commit`, `microsoft-docs`,
  `refactor`, `winui3-migration-guide`) are fully generic, license
  clean (most declare MIT inline), and stay tracked.
- **Status** — `open` → `fixed`.

### F5 — Tracked files that match `.gitignore` (Low → Untracked)

The following are listed in `.gitignore` but were committed before the
gitignore entry was added, so git keeps tracking them :

- `.claude/settings.local.json` (gitignore line 41) — local
  permission allowlist.
- `.claude/scheduled_tasks.lock` (gitignore line 65) — runtime lock
  file.

(`memory.lnk` was *only* present on the maintainer's primary worktree,
not in HEAD. No action needed for it.)

- **Remediation** — `git rm --cached <path>` for both, kept on disk.
- **Status** — `open` → `fixed`.

### F6 — Ephemeral session briefs in `src/WhispUI/docs/` (Low → Removed)

Three `brief--*.md` files document past internal sessions
(audit-robustesse, latency-instrumentation, packaging-foundation).
They reference orchestrator workflows, internal worktree names, and
the maintainer's plan paths. They have no value for a future
contributor — what they describe is already fully reflected in the
corresponding `reference--*.md` documents and in commit messages.

- **Remediation** — Deleted from the worktree. The corresponding
  `reference--*.md` documents stay (they describe the code, not the
  session that produced it).
- **Status** — `open` → `fixed`.

---

## 2. Findings — application code

### F7 — `WHISP_MODEL_PATH` env var unvalidated (High → Fixed)

- **Location** — `src/WhispUI/WhispEngine.cs`, model path resolution
  early in initialization.
- **Description** — A user-controlled environment variable is read
  and passed directly to whisper.cpp's
  `whisper_init_from_file_with_params`. No `Path.IsPathRooted` check
  and no `File.Exists` check. A relative path or non-existent path
  reaches the native layer.
- **Impact** — Information disclosure if an attacker who controls
  the process environment points the variable at a readable file —
  whisper.cpp opens it for reading and rejects on the GGUF magic
  header. No code execution (the magic check stops non-GGUF files
  from being executed as such), no escalation. The env-var threat
  model is thin to begin with (an attacker who controls process env
  already has local user access), but defending the input is cheap.
- **Remediation** — Validate the env var value : `Path.IsPathRooted`
  and `File.Exists` ; if either fails, log a warning and fall back
  to the standard `SettingsService` resolution. Done in this branch.
- **Status** — `fixed`.

### F8 — `OllamaEndpoint` scheme not constrained (Medium → Fixed)

- **Location** — `src/WhispUI/Llm/OllamaService.cs`, `BaseUrl`
  derivation.
- **Description** — The Ollama endpoint is user-configurable via
  Settings. The current parser accepts any URI scheme that
  `Uri.TryCreate` allows (including `file://`, `ftp://`, etc.) and
  any host (including non-loopback addresses inside a corporate
  intranet).
- **Impact** — Mild SSRF posture. The intended endpoint is
  `http://localhost:11434`. A malicious or accidental config could
  cause the app to issue HTTP requests to internal services.
- **Remediation** — Restrict scheme to `http` / `https`. Log a
  one-shot warning when the configured host is not loopback
  (`localhost`, `127.0.0.1`, `::1`) — not a hard block, since some
  users legitimately run Ollama on a separate machine on their LAN.
- **Status** — `fixed`.

### F9 — Prompt injection on LLM rewrite (Medium → Documented as accepted)

- **Location** — `src/WhispUI/Llm/LlmService.cs`, transcribed text is
  passed verbatim into the prompt template.
- **Description** — Ill-formed transcribed text containing the
  model's stop tokens or template delimiters could escape the
  template and steer the rewrite.
- **Impact** — Bounded. The rewrite is opt-in (gated by a profile
  setting), the model is local (no API key, no shared session, no
  exfiltration target), and the original raw transcription remains
  on the clipboard regardless. Worst case is a degraded rewrite ;
  the user always has the raw text to fall back on.
- **Remediation** — Documented in `SECURITY.md`. No code change.
- **Status** — `accepted-risk` (documented).

### F10 — Sensitive surfaces — informational summary (Low / Info)

The application uses several Windows surfaces that warrant disclosure
to anyone installing it. None is a defect ; all are deliberate.
Documented in `SECURITY.md`.

| Surface | File | Posture |
|---|---|---|
| Global hotkeys | `Shell/HotkeyManager.cs` | `RegisterHotKey` (high-level), not `WH_KEYBOARD_LL`. No keylogger surface. |
| Clipboard write + paste injection | `WhispEngine.cs` (clipboard path) | Standard Win32 pattern with read-back verification. Paste only proceeds after UIA validates the focused element is `Edit`/`Document` and refuses if the foreground window is whisp-ui itself. |
| Autostart | `Shell/AutostartService.cs` | `HKCU\...\Run\WhispUI` with quoted path, no UAC, multi-install cohabitation. |
| Telemetry & corpus | `Logging/*` | Strictly opt-in via four explicit consent dialogs (`ApplicationLogConsentDialog`, `AudioCorpusConsentDialog`, `MicrophoneTelemetryConsentDialog`, `CorpusConsentDialog`). All artifacts stay local — no upload. |
| UI Automation read | `Interop/UIAutomation.cs` | Reads exactly one property (`UIA_ControlTypePropertyId`) on the focused element. No window enumeration, no content extraction. |

---

## 3. Action plan (this branch)

The order below is the order of execution on
`chore/security-review-pre-publication`. Each phase ends with a commit.

1. **Untrack files now in `.gitignore`** — `git rm --cached` on F5.
2. **Anonymize personal references** — F2 + F3 paths and names across
   tracked docs and C# comments.
3. **Cleanup `.claude/` and `src/WhispUI/docs/`** — untrack F4 skill,
   delete F6 briefs.
4. **Add meta-publication files** — `LICENSE` (MIT, copyright "2025
   Louis Fifre"), `README.md`, `SECURITY.md`, optional `NOTICE.md` for
   third-party attribution.
5. **Apply code fixes** — F7 (`WHISP_MODEL_PATH` validation) and F8
   (`OllamaEndpoint` scheme + loopback warning).
6. **Update `.gitignore`** — add `.claude/skills/benchmark-loop/` and
   any new exclusions surfaced during the pass.
7. **Verify** — `git status` clean, fresh grep for personal paths and
   for the leaked token both come back empty in HEAD.
8. **Rewrite history** — `git filter-repo --path .mcp.json
   --invert-paths` to purge the leaked token from every historical
   commit (F1). This step is non-reversible from inside the repo, so
   it is performed only after every other phase is committed and
   reviewed.

After step 8 the branch is ready to push to a fresh public GitHub
repository. The recommended push sequence is **private repo first**,
inspect the GitHub UI for any surprise, then flip to public.

---

## 4. Out of scope (deferred)

- Full NuGet / Python CVE audit — Dependabot will cover this once the
  repo is on GitHub. Versions are current and from official sources.
- Binary distribution (signed exe + bundled native DLLs in GitHub
  Releases) — separate workstream when the maintainer wants to publish
  a release artifact. Will require code signing, third-party DLL
  attribution, and an SBOM.
- Force-rewrite of every existing local branch — only the branches
  that will be pushed publicly need to be rewritten. Local
  experimental branches can stay as-is on disk.

---

## 5. Verification

Each finding's `Status` is updated as remediations land. The branch is
considered ready to merge when :

1. `git status` is clean.
2. `git ls-files | xargs grep -l '<maintainer-profile-path>'` returns
   nothing — i.e. no `C:\Users\<name>\…`, no machine-specific drive
   roots, no absolute paths leaking the developer's filesystem layout.
3. `git log -p | grep -E 'Bearer\s+byeglbuj'` returns nothing
   (post-rewrite).
4. `LICENSE`, `README.md`, `SECURITY.md` are present at the repo root
   and render correctly on GitHub.
5. Maintainer runs the app via `scripts/build-run.ps1` and confirms a
   complete record→transcribe→clipboard→paste cycle works, plus an
   autostart toggle round-trip and an Ollama rewrite if a profile is
   configured.
