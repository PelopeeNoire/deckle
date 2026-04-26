"""Run benchmark.py across prompt variants for a single axis.

Pattern : 10 textes × 3 variantes par profil. For affinage / arrangement
each variant prompt lives at ``config/prompts/{axis}_v_{a,b,c}.txt``.
Archives reports to ``reports/run_{axis}_v{letter}.{json,txt}``.

Usage:
    python run_variants.py --axis affinage --variants a,b,c --temperature 0.15
    python run_variants.py --axis arrangement --variants a,b,c --temperature 0.0
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

CORPUS_BY_AXIS = {
    "relecture":   "telemetry/corpus-lissage/corpus.jsonl",   # out-of-band
    "lissage":     "telemetry/corpus-lissage/corpus.jsonl",
    "affinage":    "telemetry/corpus-affinage/corpus.jsonl",
    "arrangement": "telemetry/corpus-arrangement/corpus.jsonl",
}

MODEL_BY_AXIS = {
    "relecture":   "ministral-3:14b",
    "lissage":     "ministral-3:14b",
    "affinage":    "ministral-3:14b",
    "arrangement": "ministral-3:14b",
}


def run_variant(axis: str, variant: str, temperature: float) -> dict:
    prompt_file = f"config/prompts/{axis}_v_{variant}.txt"
    if not (BENCHMARK_DIR / prompt_file).exists():
        return {"variant": variant, "exit_code": -1, "error": f"missing {prompt_file}"}

    last_json    = REPORTS_DIR / "last_report.json"
    last_txt     = REPORTS_DIR / "last_report.txt"
    archive_json = REPORTS_DIR / f"run_{axis}_v{variant}.json"
    archive_txt  = REPORTS_DIR / f"run_{axis}_v{variant}.txt"

    cmd = [
        sys.executable, "benchmark.py",
        "--corpus-glob", CORPUS_BY_AXIS[axis],
        "--prompt-file", prompt_file,
        "--model",       MODEL_BY_AXIS[axis],
        "--temperature", str(temperature),
        "--skip-judge",
        "--verbose",
    ]

    print(f"\n{'='*70}")
    print(f"  AXIS: {axis} | variant: {variant} | temp: {temperature}")
    print(f"  prompt: {prompt_file}")
    print(f"  corpus: {CORPUS_BY_AXIS[axis]}")
    print(f"  model:  {MODEL_BY_AXIS[axis]}")
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
        "variant":      variant,
        "exit_code":    result.returncode,
        "elapsed_sec":  round(elapsed, 1),
        "archived":     archived,
        "archive_json": str(archive_json) if archived else None,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Run prompt variants on a single axis")
    parser.add_argument("--axis", required=True, choices=list(CORPUS_BY_AXIS))
    parser.add_argument("--variants", default="a,b,c",
                        help="Comma-separated variant letters (default: a,b,c)")
    parser.add_argument("--temperature", type=float, required=True)
    args = parser.parse_args()

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    variants = [v.strip().lower() for v in args.variants.split(",")]

    print(f"Will run {len(variants)} variants on axis={args.axis} at T={args.temperature}:")
    for v in variants:
        print(f"  - config/prompts/{args.axis}_v_{v}.txt")

    summaries = []
    t_start = time.time()
    for v in variants:
        summaries.append(run_variant(args.axis, v, args.temperature))
    total = time.time() - t_start

    print(f"\n{'='*70}")
    print(f"  SUMMARY ({total/60:.1f} min total)")
    print(f"{'='*70}\n")
    for s in summaries:
        flag = "OK" if s.get("exit_code") == 0 and s.get("archived") else "FAIL"
        print(f"  [{flag}] variant {s['variant']}  {s.get('elapsed_sec', 0):7.1f}s  → {s.get('archive_json') or s.get('error')}")


if __name__ == "__main__":
    main()
