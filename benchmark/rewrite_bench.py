"""Score the 4 cleanup-bracket rewrite prompts on the matching corpus.

One bench, four axes (relecture / lissage / affinage / arrangement). Each
axis has a canonical prompt at ``config/prompts/<bracket>_system_prompt.txt``
and runs against the bracket-keyed corpus at
``telemetry/corpus-<bracket>/corpus.jsonl``.

The bench drives the legacy ``benchmark.run()`` once per axis with
``--skip-judge`` (no automated LLM judge — the agent reads the output and
scores qualitatively against the grid in ``judge_system_prompt.txt``,
adapted per bracket : C5 inverted on relecture/lissage/affinage where
thematic regrouping is a regression).

Usage::

    # All four axes with the canonical prompts
    python rewrite_bench.py --verbose

    # Single bracket, custom temperature
    python rewrite_bench.py --bracket affinage --temperature 0.15

    # Try a variant by suffix (config/prompts/affinage_system_prompt_v2.txt)
    python rewrite_bench.py --bracket affinage --prompt-suffix _v2

Outputs::

    reports/last_rewrite_run.json   ← structured per-axis details
    reports/last_rewrite_run.txt    ← human-readable side-by-side digest

The pipeline is reusable as-is when the corpus gets new samples :

    1. python whisper_bench.py --bracket all --slug <new-slug>
    2. python refresh_corpus.py --source-corpus telemetry/<new-slug>/corpus.jsonl
    3. python rewrite_bench.py --verbose
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import time
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

import benchmark                                                    # noqa: E402
from lib.corpus import BRACKET_SLUGS, load_corpus                   # noqa: E402

PROMPTS_DIR = BENCHMARK_DIR / "config" / "prompts"
REPORTS_DIR = BENCHMARK_DIR / "reports"

DEFAULT_MODEL       = "ministral-3:14b"
DEFAULT_TEMPERATURE = 0.30
DEFAULT_NUM_CTX_K   = 16
DEFAULT_ENDPOINT    = "http://localhost:11434/api/generate"

# Per-bracket corpus glob — corpora are produced by ``refresh_corpus.py``
# from the Deckle dictation telemetry, then segmented by bracket.
def corpus_glob_for(bracket: str) -> str:
    return f"telemetry/corpus-{bracket}/corpus.jsonl"


def prompt_path_for(bracket: str, suffix: str) -> Path:
    """``relecture`` + ``"_v2"`` → ``relecture_system_prompt_v2.txt``."""
    return PROMPTS_DIR / f"{bracket}_system_prompt{suffix}.txt"


def run_axis(
    *,
    bracket:       str,
    prompt_suffix: str,
    model:         str,
    temperature:   float,
    num_ctx_k:     int,
    endpoint:      str,
    limit:         int | None,
    verbose:       bool,
) -> dict:
    """Run one bracket axis. Returns a structured dict, never raises."""
    prompt_path = prompt_path_for(bracket, prompt_suffix)
    if not prompt_path.exists():
        return {
            "bracket": bracket,
            "error":   f"missing prompt file: {prompt_path.relative_to(BENCHMARK_DIR)}",
        }

    corpus_path = BENCHMARK_DIR / corpus_glob_for(bracket)
    samples = load_corpus(str(corpus_path), bracket=bracket)
    if limit is not None:
        samples = samples[:limit]

    if not samples:
        return {
            "bracket":     bracket,
            "prompt_file": prompt_path.name,
            "n_samples":   0,
            "warning":     f"no samples found at {corpus_path.relative_to(BENCHMARK_DIR)}",
        }

    system_prompt = prompt_path.read_text(encoding="utf-8").strip()

    print(f"\n{'='*70}")
    print(f"  bracket : {bracket}")
    print(f"  prompt  : {prompt_path.name} ({len(system_prompt)} chars)")
    print(f"  corpus  : {corpus_path.relative_to(BENCHMARK_DIR)} ({len(samples)} samples)")
    print(f"  model   : {model}  T={temperature}  ctx={num_ctx_k}K")
    print(f"{'='*70}\n", flush=True)

    t0 = time.time()
    report = benchmark.run(
        samples       = samples,
        system_prompt = system_prompt,
        model         = model,
        temperature   = temperature,
        num_ctx       = num_ctx_k * 1024,
        endpoint      = endpoint,
        judge         = None,            # always --skip-judge for bracket bench
        verbose       = verbose,
    )
    elapsed = time.time() - t0

    return {
        "bracket":     bracket,
        "prompt_file": prompt_path.name,
        "elapsed_sec": round(elapsed, 1),
        **report,
    }


def write_reports(results: list[dict]) -> tuple[Path, Path]:
    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    json_path = REPORTS_DIR / "last_rewrite_run.json"
    txt_path  = REPORTS_DIR / "last_rewrite_run.txt"

    json_path.write_text(
        json.dumps({"timestamp": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                    "results":   results},
                   ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    lines: list[str] = [f"Rewrite bench — {time.strftime('%Y-%m-%dT%H:%M:%S%z')}", ""]
    for r in results:
        lines.append("─" * 70)
        if "error" in r:
            lines.append(f"## {r['bracket']}: SKIPPED ({r['error']})")
            lines.append("")
            continue
        if "warning" in r:
            lines.append(f"## {r['bracket']}: {r['warning']}")
            lines.append(f"   prompt: {r['prompt_file']}")
            lines.append("")
            continue

        lines.append(
            f"## {r['bracket']}  ({r['samples']} samples, {r['elapsed_sec']}s)"
        )
        lines.append(
            f"   prompt: {r['prompt_file']}  "
            f"model: {r['model']}  T={r['temperature']}  ctx={r['num_ctx_k']}K"
        )
        lines.append(
            f"   composite_median: {r['composite_median']}  "
            f"catastrophes: {r['catastrophes']}/{r['samples']}"
        )
        lines.append("")
        for d in r["details"]:
            if "error" in d:
                lines.append(f"  · {d['id']}: ERROR {d['error']}")
                continue
            rule  = d["rule"]
            tag   = " [!]" if d.get("catastrophe") else ""
            lines.append(
                f"  · {d['id']}  "
                f"len_ratio={d['length_ratio']:.2f}  "
                f"novel={rule['novel_words']:.2f}  "
                f"lists={rule['lists']:.0f}  "
                f"preamble={rule['preamble']:.0f}{tag}"
            )
        lines.append("")
    txt_path.write_text("\n".join(lines), encoding="utf-8")

    return json_path, txt_path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--bracket", default="all", choices=("all", *BRACKET_SLUGS),
        help="Single bracket axis or ``all`` (default).",
    )
    parser.add_argument(
        "--prompt-suffix", default="",
        help="Append a suffix when locating the prompt file. e.g. ``_v2`` "
             "loads ``<bracket>_system_prompt_v2.txt``. Empty = canonical.",
    )
    parser.add_argument("--model",        default=DEFAULT_MODEL)
    parser.add_argument("--temperature",  type=float, default=DEFAULT_TEMPERATURE)
    parser.add_argument("--num-ctx-k",    type=int,   default=DEFAULT_NUM_CTX_K)
    parser.add_argument("--endpoint",     default=DEFAULT_ENDPOINT)
    parser.add_argument("--limit",        type=int, default=None,
                        help="Process only the first N samples per axis (debug).")
    parser.add_argument("--verbose",      action="store_true")
    args = parser.parse_args()

    brackets = list(BRACKET_SLUGS) if args.bracket == "all" else [args.bracket]

    t_start = time.time()
    results: list[dict] = []
    for bracket in brackets:
        results.append(run_axis(
            bracket       = bracket,
            prompt_suffix = args.prompt_suffix,
            model         = args.model,
            temperature   = args.temperature,
            num_ctx_k     = args.num_ctx_k,
            endpoint      = args.endpoint,
            limit         = args.limit,
            verbose       = args.verbose,
        ))
    total = time.time() - t_start

    json_path, txt_path = write_reports(results)

    print(f"\n{'='*70}")
    print(f"  SUMMARY ({total/60:.1f} min total)")
    print(f"{'='*70}\n")
    for r in results:
        if "error" in r:
            tag = "SKIP"
            extra = r["error"]
        elif "warning" in r:
            tag = "WARN"
            extra = r["warning"]
        else:
            tag = "OK"
            extra = (
                f"{r['samples']} samples  "
                f"med={r['composite_median']}  "
                f"cata={r['catastrophes']}"
            )
        print(f"  [{tag:4}] {r['bracket']:<12} {extra}")

    print(f"\n  JSON  → {json_path}")
    print(f"  TXT   → {txt_path}")


if __name__ == "__main__":
    main()
