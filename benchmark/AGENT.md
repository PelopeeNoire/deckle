# Benchmark — agent guide

This folder holds Louis's iterative benchmarking suite for the WhispUI
transcription stack. Any LLM agent working here should read this file
first; everything below is intentionally model-agnostic (no Claude-only
shortcuts), so the same instructions apply whether you're Claude,
GPT, GLM, Mistral, or anything else.

---

## Purpose

Run controlled experiments on Whisper inference parameters (initial
prompt, beam size, …) and on LLM rewrite prompts, **without ever
calling a remote API from inside the bench scripts**. The benches
produce text artifacts (transcriptions, rewrites, runtime stats); a
human or an agent (you) reads those artifacts and judges
qualitatively. That's the whole pattern.

Hard rules:

- **No automated LLM judge in the code.** Don't add one. Don't quietly
  call `anthropic`, `openai`, `mistral`, or any cloud SDK. The bench
  outputs are designed to be read.
- **No API key on disk.** Ever. No `.env`, no hard-coded secrets, no
  helper that loads them. If a future bench really needs an external
  call, it must read from the user's already-exported environment and
  fail loudly when the variable is absent.
- **Local LLMs are fine** (Ollama-hosted Ministral 14B is what
  `benchmark.py` / `autoresearch.py` legacy already use). Anything that
  speaks HTTP to `localhost` is in scope.

---

## Layout

```
benchmark/
├── AGENT.md                  ← you are here
├── README.md                 ← user-facing tour
├── launch.ps1                ← single entry point, auto-discovers benches
├── results.tsv               ← autoresearch journal (gitignored)
│
├── whisper_bench.py          ← bench: Whisper transcription quality
├── rewrite_bench.py          ← bench: rewrite quality across the 4 brackets
├── benchmark.py              ← legacy unitary runner (one prompt × one corpus)
├── autoresearch.py           ← legacy autoresearch loop on rewrite prompt
├── _template_bench.py        ← skeleton — copy when adding a new bench
│
│   # ── utilities (no _bench suffix; not exposed in launcher) ──
├── refresh_corpus.py         ← pre-bench: substitute raw.text in a corpus.jsonl
│                                 with the latest whisper_bench transcription
├── segment_corpus.py         ← pre-bench: bucket a corpus.jsonl into 4
│                                 telemetry/corpus-<bracket>/corpus.jsonl files
├── compare_runs.py           ← post-bench: side-by-side digest from
│                                 reports/last_rewrite_run.json
│
├── config/
│   ├── config.ini            ← per-bench defaults
│   └── prompts/
│       ├── relecture_system_prompt.txt    ← bracket prompts (4×)
│       ├── lissage_system_prompt.txt           tunés sur Ministral 14B Q4,
│       ├── affinage_system_prompt.txt          source-of-truth pour les
│       ├── arrangement_system_prompt.txt       defaults dans AppSettings.cs
│       ├── whisper_initial_prompt.txt     ← active Whisper prompt
│       ├── system_prompt.txt              ← legacy (autoresearch.py target)
│       └── judge_system_prompt.txt        ← legacy 6-criteria grid
│
├── lib/
│   ├── corpus.py             ← reads telemetry corpus, bracket bucketing
│   ├── judge*.py             ← legacy rewrite judges (Ministral / Claude)
│   ├── metrics.py            ← rule-based pre-filters
│   └── ollama.py             ← Ollama HTTP client
│
├── telemetry/                ← gitignored runtime data (WhispUI side)
│   ├── <slug>/corpus.jsonl   ← WhispUI's dictation corpus
│   └── <slug>/audio/*.wav    ← matching audio
│
├── reports/                  ← gitignored bench artifacts
│   ├── last_<name>_run.json    ← structured dump per bench (latest)
│   ├── last_<name>_run.txt     ← human-readable companion (latest)
│   ├── exp<NN>_<slug>.{json,txt} ← archived per-experiment snapshots
│   └── autoresearch_report.txt   ← narrative summary
│
└── logs/                     ← gitignored step-by-step run logs
```

---

## How to launch

```powershell
pwsh benchmark/launch.ps1
```

The launcher scans the folder, lists every script that ends in
`_bench.py` (plus `benchmark.py` and `autoresearch.py` aliases), shows
their docstring summaries, and lets you pick one with the arrow keys.
Press Enter to run, Escape or `Q` to quit, type characters to filter
the list as you go.

You can also run any bench directly:

```powershell
python whisper_bench.py --bracket lissage --verbose
python rewrite_bench.py --bracket affinage --temperature 0.15
```

All scripts must accept `--verbose` and `--limit N`; bench-specific
flags live alongside.

### Reusable pipeline (when corpus is enriched)

When Louis records new samples in the missing duration brackets (<60 s
native, >10 min native with audio preserved), the same four commands
reproduce a full evaluation cycle:

```powershell
# 1. Re-transcribe with the locked Whisper initial prompt
python whisper_bench.py --bracket all --slug <new-slug>

# 2. Refresh corpus.jsonl in-place (clones the envelope, swaps raw.text)
python refresh_corpus.py --source-corpus telemetry/<new-slug>/corpus.jsonl

# 3. Segment by bracket (one corpus.jsonl per bracket)
python segment_corpus.py --source telemetry/<new-slug>/corpus.jsonl

# 4. Run all 4 bracket axes against the canonical prompts
python rewrite_bench.py --verbose

# 5. (optional) Build a side-by-side digest for qualitative scoring
python compare_runs.py
```

Step 3 segments by `payload.duration_seconds` into
`telemetry/corpus-{relecture,lissage,affinage,arrangement}/corpus.jsonl`
— that's exactly what `rewrite_bench.py` reads. You can merge several
sources by repeating `--source` or by passing `--append` on a second
run.

`rewrite_bench.py --bracket <name>` runs a single axis. To iterate on
a prompt without touching the canonical, save the variant as
`<bracket>_system_prompt_v2.txt` and pass `--prompt-suffix _v2`.

### When to add a bench vs. an utility

Two patterns coexist on purpose:

- A **bench** (`*_bench.py`) is a measurement that produces
  `reports/last_<name>_run.{json,txt}`. The launcher picks it up.
  Anything that turns inputs into a scored / inspectable artifact
  belongs here.

- An **utility** (any other `*.py` at the top level) is a step in the
  pipeline that doesn't measure — it transforms data so a bench can
  consume it (`refresh_corpus.py`, `segment_corpus.py`) or post-
  processes a bench result (`compare_runs.py`). The launcher ignores
  it. Keep them small, single-purpose, idempotent, and don't pile up
  one-shot scripts that are tied to a specific dataset — write the
  utility so it works on any source path.

---

## Conventions for adding a new bench

1. **Copy** `_template_bench.py` to `<name>_bench.py`. Drop the leading
   underscore — `_*.py` files are skipped by `launch.ps1` discovery on
   purpose so templates don't show up.
2. **Edit the docstring**: the first line becomes the launcher menu
   entry. Keep it under ~80 chars and start with a verb (e.g.
   `"Score English-prompt translations against the French baseline."`).
3. **Replace the placeholder iteration**: the `run()` function ships
   with three lorem-ipsum items so the wiring is visible. Plug in your
   real corpus loader (`from lib.corpus import load_corpus`) or
   whatever else you need.
4. **Output to `reports/last_<name>_run.{json,txt}`** where
   `<name>` is the script filename without the `_bench` suffix. Both
   files are mandatory: JSON for structured analysis, TXT for human /
   agent reading. When archiving an experiment snapshot, copy the
   `last_*` pair to `reports/exp<NN>_<slug>.{json,txt}` before the
   next run overwrites them.
5. **Don't import any cloud LLM SDK**. If you need a local model, use
   `lib.ollama` like the existing benches do.

The launcher will pick up the new file at the next run, no
registration step needed.

### Worked example — adding an English-rewrite bench

Goal: same Whisper transcription, but rewritten by an English LLM
prompt instead of the French Nettoyage prompt. We want to see whether
a translation-then-rewrite pipeline is viable.

```python
"""Score English-prompt rewrites against the French baseline."""

from __future__ import annotations
import argparse, json, sys, time
from pathlib import Path

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))
from lib.corpus import BRACKET_SLUGS, load_corpus
from lib import ollama as ollama_client

REPORTS_DIR = BENCHMARK_DIR / "reports"

ENGLISH_PROMPT = """You are a transcription editor. Translate the following
French dictation into clean English while preserving every nuance, every
idea, and the speaker's register. Do not summarise or restructure beyond
what is needed for English to read naturally."""

def run(samples, prompt, verbose):
    details = []
    for i, s in enumerate(samples, start=1):
        text, _ = ollama_client.call_ollama(
            system="(none)", user=ENGLISH_PROMPT + "\n\n" + s.raw_text,
            model="ministral-3:14b", temperature=0.3, num_ctx=32 * 1024,
            endpoint="http://localhost:11434/api/generate",
        )
        details.append({"id": s.id, "input_chars": len(s.raw_text),
                        "output_chars": len(text), "output_text": text})
    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    json_path = REPORTS_DIR / "last_english_rewrite_run.json"
    json_path.write_text(json.dumps({"prompt": prompt, "details": details},
                                    ensure_ascii=False, indent=2),
                         encoding="utf-8")
    print(f"JSON → {json_path}")

# … argparse + main() as in the template
```

Save as `english_rewrite_bench.py`. Run via the launcher; it'll show
`English-Rewrite` with the docstring summary.

---

## Autoresearch workflow

When the goal is to optimise a parameter (e.g. the Whisper initial
prompt, or a rewrite prompt), the agent runs an iterative loop —
inspired by the `autoresearch` skill (Karpathy-style). The loop is
**conducted by the agent in conversation** (no automation script is
required; legacy `autoresearch.py` automates it for the rewrite prompt
specifically, with a Ministral designer model).

Pattern:

```
THINK   → analyse the previous result, hypothesise a change
EDIT    → modify the parameter (prompt file, config, …)
COMMIT  → git commit on the autoresearch/<tag> branch
RUN     → invoke the bench script
MEASURE → read the JSON/TXT output, score qualitatively
DECIDE  → keep (advance the branch) or discard (git reset --hard HEAD~1)
LOG     → append a row to results.tsv
```

`results.tsv` lives at `benchmark/results.tsv` (gitignored), with
columns `experiment <tab> commit <tab> metric <tab> status <tab>
description`. The branch is named `autoresearch/<topic>-<YYYYMMDD>`,
created from the work-in-progress feature branch (typically
`bench/whisper-tuning` for Whisper-side work).

### Commit message conventions

- On `autoresearch/*` branches: `bench-<topic>: iteration N — <change>`
- Squash-merge to the feature branch when the run produces a winner
- Never commit directly to `main`

### Scoring without an automated judge

Since there's no scoring API in the code, you (the agent) read the
TXT output and emit a qualitative score yourself. For Whisper
transcription benches the reference grid is:

| Criterion             | What to look for                                                         |
|-----------------------|--------------------------------------------------------------------------|
| Cohérence interne     | Words that exist in French, syntax that holds                            |
| Hallucinations        | No loops, no phantom Amara.org credits, no obvious invention             |
| Ponctuation / accents | French diacritics correctly placed; missing accents = failure            |
| Registre / littéralité | Speech transcribed verbatim, not paraphrased into corporate prose       |
| Segmentation          | Reasonable chunking, no monolith blocks, no word-by-word fragments       |

Score each criterion 1–5, aggregate as a weighted composite if useful,
and write the verdict next to the experiment row in `results.tsv`.

---

## Don't

- Don't add automated LLM judging that calls a cloud API.
- Don't store secrets in the repo.
- Don't write outputs anywhere outside `reports/` or `logs/`.
- Don't import an LLM SDK on module load (`anthropic`, `openai`, …) —
  even if it's only for an optional code path. Lazy-import inside the
  function that uses it, and only after a clear opt-in flag.
- Don't run benches against unfiltered audio (>20 min cap) — the
  bracket bucketing exists to keep iteration fast.
