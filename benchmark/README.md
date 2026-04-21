# Benchmark — prompt optimization for WhispUI rewriting

Closed-loop suite that measures the quality of a candidate **rewrite
system prompt** on Louis' real dictation corpus, and iterates on it
automatically via an Ollama-hosted designer model.

The corpus and telemetry are produced by WhispUI itself (see the
JSONL sink under `src/WhispUI/Logging/Sinks/`). Nothing here records
audio or transcriptions — it only consumes what WhispUI has already
written.

## Folder layout

```
benchmark/
├── config/
│   ├── config.ini                           — knobs (model, endpoint, judge)
│   └── prompts/
│       ├── judge_system_prompt.txt          — 6-criteria grid for the judge
│       └── system_prompt.txt                — prompt under optimization
├── lib/                                     — shared helpers (imported)
│   ├── ollama.py                            — HTTP client, sanitize
│   ├── corpus.py                            — WhispUI JSONL reader
│   ├── metrics.py                           — rule-based pre-filters
│   ├── judge.py                             — 6-criteria contract + scoring
│   ├── judge_claude.py                      — Anthropic SDK backend (default)
│   └── judge_ollama.py                      — local Ministral fallback
├── benchmark.py                             — one-shot scorer
├── autoresearch.py                          — iterative optimizer
├── launch.ps1                               — interactive PowerShell menu
├── README.md
├── telemetry/  (gitignored — runtime artifacts written by WhispUI)
│   ├── <slug>/corpus.jsonl                  — raw transcriptions, one folder per profile
│   ├── <slug>/audio/*.wav                   — matching WAV captures (opt-in)
│   ├── latency.jsonl                        — per-transcription perf stream (opt-in)
│   ├── app.jsonl                            — full application log (opt-in, rotates at 5000 lines)
│   ├── reports/                             — benchmark.py + autoresearch.py outputs
│   ├── exemples/                            — reference transcripts
│   └── legacy/                              — archived telemetry.csv
└── logs/    (gitignored — live benchmark execution traces)
```

## Quick commands

```powershell
# One-shot scoring of the current system_prompt.txt, verbose per sample.
python benchmark.py --verbose

# Same, but local-only (Ministral 14B as judge — no Anthropic API needed).
python benchmark.py --judge ollama

# Skip the judge entirely: emit only the rule-based median.
python benchmark.py --skip-judge

# Filter the corpus by duration or profile.
python benchmark.py --duration-min 60 --duration-max 600
python benchmark.py --slug restructuration

# Autoresearch loop — produces telemetry/reports/results.tsv incrementally.
python autoresearch.py --max-experiments 5 --runs-per-experiment 2
```

Louis' interactive launcher: `.\launch.ps1` (Windows PowerShell menu).

## Scoring model

Each sample: WhispUI's raw Whisper text → candidate prompt applied
via the target Ollama model → judge scores the output on six
criteria (1..5 each):

| Criterion                   | Weight |
|-----------------------------|--------|
| Complétude macro            | 25 %   |
| Préservation des nuances    | 25 %   |
| Densité préservée           | 15 %   |
| Non-invention               | 15 %   |
| Structure thématique        | 10 %   |
| Clarté et fidélité registre | 10 %   |

The composite is a penalty in `[0.0, 1.0]` — **lower is better**
(`0.0` = all criteria at 5/5). `benchmark.py` reports the median
composite as `SCORE=X.XXXX` on stdout; `autoresearch.py` parses that
line to drive the loop.

Rule-based signals (`novel_words`, `length_ratio`, `preamble`, `lists`)
stay wired in as cheap pre-filters: a catastrophic output (mass
hallucination or runaway length) is flagged before spending a judge
call.

## Configuration keys

`config/config.ini`:

| Section         | Key                  | Meaning                                           |
|-----------------|----------------------|---------------------------------------------------|
| `benchmark`     | `profile`            | Label for report naming.                          |
| `benchmark`     | `model`              | Target Ollama model under test.                   |
| `benchmark`     | `temperature`        | Sampling temperature for the target.              |
| `benchmark`     | `num_ctx_k`          | Context window in thousands of tokens.            |
| `benchmark`     | `endpoint`           | Ollama HTTP endpoint.                             |
| `benchmark`     | `corpus_glob`        | Glob for WhispUI JSONL files (relative).          |
| `benchmark`     | `prompt`             | Path of the prompt under optimization.            |
| `benchmark`     | `judge_backend`      | `claude` (default) or `ollama`.                   |
| `benchmark`     | `judge_model`        | Override — empty uses the backend default.        |
| `autoresearch`  | `designer_model`     | Ollama model that proposes variants.              |
| `autoresearch`  | `max_experiments`    | Upper bound per run.                              |
| `autoresearch`  | `runs_per_experiment`| Repeats per variant to dampen variance.           |

## Dependencies

- Python 3.11+
- `anthropic` (only for `judge_backend = claude`): `pip install anthropic`
- Ollama running locally with `ministral-3:14b` (or another model of
  your choosing) loaded — used for the target, the designer, and the
  local-judge fallback.

The Claude judge reads `ANTHROPIC_API_KEY` from the environment and
enables ephemeral prompt caching on the judge system block, so the
grid is paid for once per session instead of once per sample.
