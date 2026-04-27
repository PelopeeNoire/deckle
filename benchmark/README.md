# Benchmark — WhispUI experimentation suite

Closed-loop suite for measuring and optimising the WhispUI transcription
stack: the Whisper inference parameters (initial prompt first, then the
rest), and the LLM rewrite prompts that turn raw transcription into
clean text. Iteration happens in conversation with an LLM agent
(typically Claude Code), driven by the [autoresearch
skill](https://github.com/karpathy/autoresearch) pattern — propose,
measure, keep or revert.

> **Read [`AGENT.md`](AGENT.md) first if you're an agent.** It documents
> the conventions, the no-API-key rule, and how to add a new bench. This
> README only gives the lay of the land for humans browsing the folder.

## How to launch

```powershell
pwsh launch.ps1
```

Arrow keys navigate, Enter runs, Escape / Q quits, type to filter.
The launcher auto-discovers any `*_bench.py` plus the legacy
`benchmark.py` / `autoresearch.py` aliases — drop a new bench in the
folder and it shows up. See [`AGENT.md`](AGENT.md) for the
`*_bench.py` template.

## Folder layout

```
benchmark/
├── AGENT.md                            ← agent-facing conventions (read first)
├── README.md                           ← this file
├── launch.ps1                          ← interactive launcher
│
├── whisper_bench.py                    ← Whisper transcription bench
├── rewrite_bench.py                    ← Rewrite bench across the 4 brackets
├── benchmark.py                        ← legacy unitary runner (one prompt × one corpus)
├── autoresearch.py                     ← legacy rewrite-prompt optimisation loop
├── _template_bench.py                  ← skeleton for new benches (excluded from launcher)
│
│   # utilities
├── refresh_corpus.py                   ← swap raw.text with a fresh whisper_bench dump
├── segment_corpus.py                   ← bucket a corpus into corpus-<bracket>/ folders
├── compare_runs.py                     ← side-by-side digest of last_rewrite_run.json
│
├── config/
│   ├── config.ini                      ← per-bench defaults
│   └── prompts/
│       ├── whisper_initial_prompt.txt  ← active Whisper initial prompt
│       ├── relecture_system_prompt.txt    \
│       ├── lissage_system_prompt.txt      / 4 bracket prompts — source of
│       ├── affinage_system_prompt.txt     \   truth for the AppSettings.cs
│       ├── arrangement_system_prompt.txt  /  defaults in WhispUI
│       ├── system_prompt.txt           ← legacy autoresearch target
│       └── judge_system_prompt.txt     ← legacy 6-criteria grid
│
├── lib/
│   ├── corpus.py                       ← WhispUI JSONL reader + bracket bucketing
│   ├── ollama.py                       ← HTTP client, sanitize
│   ├── metrics.py                      ← rule-based pre-filters
│   ├── judge.py                        ← legacy 6-criteria contract
│   ├── judge_claude.py                 ← legacy Claude judge (rewrite)
│   └── judge_ollama.py                 ← legacy Ollama judge (rewrite)
│
├── telemetry/  (gitignored)            ← all runtime artifacts
│   ├── <slug>/corpus.jsonl             ← raw transcriptions per profile
│   ├── <slug>/audio/*.wav              ← matching WAV captures (opt-in)
│   ├── latency.jsonl                   ← per-recording perf stream (opt-in)
│   ├── microphone.jsonl                ← dBFS calibration (opt-in)
│   ├── app.jsonl                       ← full app log (opt-in, rotates at 5000 lines)
│   ├── reports/                        ← bench outputs (last_<name>_run.{json,txt})
│   ├── exemples/                       ← reference transcripts
│   └── legacy/                         ← archived CSVs
│
└── logs/  (gitignored)                 ← live execution traces
```

## Audio duration brackets

Every audio sample falls into one of four named tiers, derived from
`payload.duration_seconds` at read time. The bracket name describes the
**level of cleanup permitted on the rewritten text**, not the raw audio
length on its own. Used by `whisper_bench.py --bracket <slug>` and
exposed via `lib.corpus.bracket_of()` / `group_by_bracket()`.

| Slug          | Duration            | Allowed cleanup                                                                  |
|---------------|---------------------|----------------------------------------------------------------------------------|
| `relecture`   | ≤ 60 s              | Surface fixes only: punctuation, accents, missing periods                        |
| `lissage`     | 60 s < d ≤ 300 s    | Light smoothing: flow, transitions, subtler typos                                |
| `affinage`    | 300 s < d ≤ 600 s   | More precise detail work; never touches the substance                            |
| `arrangement` | 600 s < d ≤ 1200 s  | Regroup repeated passages **only when nuance is identical**; otherwise keep all |

Invariant across all brackets: keep every nuance. Only `arrangement`
allows merging passages that say the same thing the same way; if the
nuances differ, both stay.

The `MaxRecordingDurationSeconds = 1200` cap on the WhispUI side
matches the upper bound; samples above the cap should never reach the
corpus and bucket to `None` if they somehow do.

## Whisper bench (current focus)

`whisper_bench.py` re-runs `whisper.cpp` on the corpus with a candidate
parameter set and dumps the transcriptions to
`reports/last_whisper_run.{json,txt}`. **No automated LLM
judge** — the agent (or the human reviewer) reads the TXT and judges
qualitatively.

Quick commands:

```powershell
# Re-transcribe one short sample with the active prompt
python whisper_bench.py --bracket lissage --limit 1 --verbose

# Full lissage tier (20 samples) with a custom prompt file
python whisper_bench.py --bracket lissage --initial-prompt-file my_prompt.txt
```

Iteration pattern: see [`AGENT.md`](AGENT.md) → "Autoresearch
workflow".

## Rewrite bench (4 brackets)

`rewrite_bench.py` runs the 4 cleanup-bracket prompts (relecture /
lissage / affinage / arrangement) against the matching corpora
(`telemetry/corpus-<bracket>/corpus.jsonl`) and dumps everything to
`reports/last_rewrite_run.{json,txt}`. **No automated LLM judge** — the
bench runs `--skip-judge` and the agent (Claude session, you) reads the
output and scores qualitatively against the 6-criteria grid in
`judge_system_prompt.txt`, with C5 (thematic regrouping) inverted on
relecture / lissage / affinage where regrouping is a regression.

Quick commands:

```powershell
# All 4 axes with the canonical prompts
python rewrite_bench.py --verbose

# Single bracket, custom temperature
python rewrite_bench.py --bracket affinage --temperature 0.15

# Try a variant: save it as <bracket>_system_prompt_v2.txt
python rewrite_bench.py --bracket affinage --prompt-suffix _v2

# Build a side-by-side digest after the run
python compare_runs.py
```

The 4 canonical prompts in `config/prompts/<bracket>_system_prompt.txt`
are the source of truth for the `Profiles` defaults in
`src/WhispUI/Settings/AppSettings.cs`. Edit one place, re-run the bench,
port to AppSettings.cs — that's the loop.

## Reusable pipeline (when corpus is enriched)

```powershell
python whisper_bench.py --bracket all --slug <new-slug>
python refresh_corpus.py --source-corpus telemetry/<new-slug>/corpus.jsonl
python segment_corpus.py --source telemetry/<new-slug>/corpus.jsonl
python rewrite_bench.py --verbose
python compare_runs.py
```

## Legacy rewrite bench

`benchmark.py` and `autoresearch.py` predate the bracket-aligned work.
`benchmark.py` is a unitary runner — one prompt × one corpus glob —
and `rewrite_bench.py` calls into it for each axis. `autoresearch.py`
is an auto-loop driver that mutates `system_prompt.txt` with a
designer LLM and keeps the best variant. Both still work as-is; the
6-criteria grid still lives at `config/prompts/judge_system_prompt.txt`
for when an automated judge run makes sense again.

## Dependencies

- Python 3.11+
- Ollama running locally with `ministral-3:14b` loaded (target,
  designer, and legacy local judge)
- whisper.cpp built with the Vulkan backend at
  `whisper.cpp/build/bin/whisper-cli.exe` (used by `whisper_bench.py`)
- `anthropic` Python SDK only if you opt into the legacy Claude judge:
  `pip install anthropic`. Even then, the API key never enters this
  folder — export it in your shell, run the legacy bench, and exit.
