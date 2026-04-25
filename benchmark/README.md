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
├── whisper_bench.py                    ← Whisper transcription bench (no auto-judge)
├── benchmark.py                        ← legacy: rewrite quality scoring
├── autoresearch.py                     ← legacy: rewrite-prompt optimisation loop
├── _template_bench.py                  ← skeleton for new benches (excluded from launcher)
│
├── config/
│   ├── config.ini                      ← per-bench defaults
│   └── prompts/
│       ├── whisper_initial_prompt.txt  ← active Whisper initial prompt
│       ├── system_prompt.txt           ← active rewrite system prompt (legacy)
│       └── judge_system_prompt.txt     ← rewrite judge grid (legacy)
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
judge** — the agent (or Louis) reads the TXT and judges qualitatively.

Quick commands:

```powershell
# Re-transcribe one short sample with the active prompt
python whisper_bench.py --bracket lissage --limit 1 --verbose

# Full lissage tier (20 samples) with a custom prompt file
python whisper_bench.py --bracket lissage --initial-prompt-file my_prompt.txt
```

Iteration pattern: see [`AGENT.md`](AGENT.md) → "Autoresearch
workflow".

## Legacy rewrite bench

`benchmark.py` and `autoresearch.py` predate the Whisper-side work.
They score LLM rewrites of frozen Whisper text (no re-transcription)
on a six-criteria grid via a judge — Ministral 14B by default for a
fully local run, or Anthropic Claude if you export
`ANTHROPIC_API_KEY` in your shell.

Configuration sits in `config/config.ini` under `[benchmark]` and
`[autoresearch]`. Reports land in `reports/`. The full
six-criteria grid (Complétude macro, Préservation des nuances,
Densité, Non-invention, Structure thématique, Clarté/registre) lives
in `config/prompts/judge_system_prompt.txt`.

These are kept around because the rewrite optimisation isn't
finished, but the active focus is the Whisper side first — once the
raw transcription is stable, the rewrite numbers stop being noise.

## Dependencies

- Python 3.11+
- Ollama running locally with `ministral-3:14b` loaded (target,
  designer, and legacy local judge)
- whisper.cpp built with the Vulkan backend at
  `whisper.cpp/build/bin/whisper-cli.exe` (used by `whisper_bench.py`)
- `anthropic` Python SDK only if you opt into the legacy Claude judge:
  `pip install anthropic`. Even then, the API key never enters this
  folder — export it in your shell, run the legacy bench, and exit.
