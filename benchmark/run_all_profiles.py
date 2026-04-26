"""Run benchmark.py sequentially across the 4 cleanup-level rewrite prompts.

Aligns with the bracket-keyed corpora in `telemetry/corpus-{bracket}/`
rebuilt by `rebuild_corpus_by_bracket.py`. The relecture corpus is
naturally empty (no native sample <60 s in the available data) — for
that bracket we run the prompt on the lissage corpus as an out-of-band
sanity test (the relecture mandate is universal across durations).

Optional sweep over temperatures via ``--temperatures 0.0,0.15,0.30``.
Each (axis, temperature) pair archives its report under
``reports/run_<axis>_t{temp}.{json,txt}`` so multiple runs do not
overwrite each other.

No remote LLM judge — all runs use ``--skip-judge``. The agent reads
the archived reports and scores qualitatively.

Usage:
    python run_all_profiles.py [--axes a,b,c]
                               [--temperatures 0.0,0.15,0.30]
                               [--limit N]
"""

from __future__ import annotations

import argparse
import io
import shutil
import subprocess
import sys
import time
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
REPORTS_DIR   = BENCHMARK_DIR / "reports"


def axis_configs() -> list[dict]:
    return [
        {
            "axis":        "relecture",
            "prompt_file": "config/prompts/relecture_system_prompt.txt",
            # No native <60 s sample → out-of-band on lissage corpus
            "corpus_glob": "telemetry/corpus-lissage/corpus.jsonl",
            # 14B retained — 3B fails the strict-literalness mandate (translates
            # "a few hundred bytes" → "quelques centaines de bytes", drops
            # "des petites choses vraiment", inserts "Voici la version
            # corrigée :" preamble). Speed gain not worth the fidelity loss.
            "model":       "ministral-3:14b",
        },
        {
            "axis":        "lissage",
            "prompt_file": "config/prompts/lissage_system_prompt.txt",
            "corpus_glob": "telemetry/corpus-lissage/corpus.jsonl",
            "model":       "ministral-3:14b",
        },
        {
            "axis":        "affinage",
            "prompt_file": "config/prompts/affinage_system_prompt.txt",
            "corpus_glob": "telemetry/corpus-affinage/corpus.jsonl",
            "model":       "ministral-3:14b",
        },
        {
            "axis":        "arrangement",
            "prompt_file": "config/prompts/arrangement_system_prompt.txt",
            "corpus_glob": "telemetry/corpus-arrangement/corpus.jsonl",
            "model":       "ministral-3:14b",
        },
    ]


def temp_tag(temperature: float | None) -> str:
    if temperature is None:
        return "default"
    return f"t{temperature:.2f}".rstrip("0").rstrip(".") if temperature else "t0"


def run_axis(cfg: dict, temperature: float | None, limit: int | None) -> dict:
    last_json    = REPORTS_DIR / "last_report.json"
    last_txt     = REPORTS_DIR / "last_report.txt"
    tag          = temp_tag(temperature)
    archive_json = REPORTS_DIR / f"run_{cfg['axis']}_{tag}.json"
    archive_txt  = REPORTS_DIR / f"run_{cfg['axis']}_{tag}.txt"

    cmd = [
        sys.executable, "benchmark.py",
        "--corpus-glob", cfg["corpus_glob"],
        "--prompt-file", cfg["prompt_file"],
        "--skip-judge",
        "--verbose",
    ]
    if temperature is not None:
        cmd += ["--temperature", str(temperature)]
    if cfg.get("model"):
        cmd += ["--model", cfg["model"]]

    print(f"\n{'='*70}")
    print(f"  AXIS: {cfg['axis']:11s}  | temp = {tag}  | model = {cfg.get('model', '(config default)')}")
    print(f"  prompt:  {cfg['prompt_file']}")
    print(f"  corpus:  {cfg['corpus_glob']}")
    print(f"{'='*70}\n", flush=True)

    t0 = time.time()
    result = subprocess.run(cmd, cwd=BENCHMARK_DIR)
    elapsed = time.time() - t0

    archived = False
    if last_json.exists():
        shutil.copy2(last_json, archive_json)
        if last_txt.exists():
            shutil.copy2(last_txt, archive_txt)
        archived = True

    return {
        "axis":         cfg["axis"],
        "temperature":  temperature,
        "tag":          tag,
        "exit_code":    result.returncode,
        "elapsed_sec":  round(elapsed, 1),
        "archived":     archived,
        "archive_json": str(archive_json) if archived else None,
        "archive_txt":  str(archive_txt) if archived else None,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Run all 4 bracket axes sequentially, optionally sweeping temperatures")
    parser.add_argument("--axes", default=None,
                        help="Comma-separated subset of axes (default: all 4)")
    parser.add_argument("--temperatures", default=None,
                        help="Comma-separated temperatures to sweep (e.g. 0.0,0.15,0.30). "
                             "If unset, uses config.ini default.")
    parser.add_argument("--limit", type=int, default=None,
                        help="Limit each run to first N samples (smoke test)")
    args = parser.parse_args()

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)

    cfgs = axis_configs()
    if args.axes:
        wanted = {a.strip() for a in args.axes.split(",")}
        cfgs = [c for c in cfgs if c["axis"] in wanted]
        missing = wanted - {c["axis"] for c in cfgs}
        if missing:
            sys.exit(f"Unknown axes: {sorted(missing)}")

    temps: list[float | None]
    if args.temperatures:
        temps = [float(t.strip()) for t in args.temperatures.split(",")]
    else:
        temps = [None]   # use config.ini default

    print(f"Will run {len(cfgs)} axes × {len(temps)} temps = {len(cfgs)*len(temps)} runs:")
    for c in cfgs:
        print(f"  - {c['axis']:11s} ({c['corpus_glob']})")
    if temps != [None]:
        print(f"  temperatures: {', '.join(str(t) for t in temps)}")

    summaries = []
    t_start = time.time()
    for cfg in cfgs:
        for temp in temps:
            summary = run_axis(cfg, temp, args.limit)
            summaries.append(summary)

    total = time.time() - t_start

    print(f"\n{'='*70}")
    print(f"  SUMMARY ({total/60:.1f} min total)")
    print(f"{'='*70}\n")
    for s in summaries:
        flag = "OK" if s["exit_code"] == 0 and s["archived"] else "FAIL"
        print(f"  [{flag}] {s['axis']:11s} {s['tag']:10s} {s['elapsed_sec']:7.1f}s  → {s['archive_json'] or 'NOT ARCHIVED'}")

    failures = [s for s in summaries if s["exit_code"] != 0 or not s["archived"]]
    sys.exit(0 if not failures else 1)


if __name__ == "__main__":
    main()
