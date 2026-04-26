"""Build a side-by-side digest from last_rewrite_run.json for human review.

Reads ``reports/last_rewrite_run.json`` (the structured output of
``rewrite_bench.py``) and emits ``reports/comparison.txt`` with one
block per source sample::

    [bracket  sample_id  duration  input_chars]
    INPUT (raw Whisper):
        <raw text>
    --- OUTPUT  chars=…  novel=…  len_ratio=…  preamble=…  lists=…
        <rewritten text>

Per-bracket organisation: for each of the 4 axes, all samples in that
axis appear in one section. Use this when you want the verbose,
output-side-by-input view for qualitative scoring against the 6-criteria
grid in ``judge_system_prompt.txt`` (C5 inverted on relecture / lissage /
affinage where thematic regrouping is a regression).

Raw input text is recovered by re-reading the per-bracket corpora at
``telemetry/corpus-<bracket>/corpus.jsonl`` — they're the same files
``rewrite_bench.py`` consumed.

Usage::

    python compare_runs.py                       # writes reports/comparison.txt
    python compare_runs.py --output other.txt
"""

from __future__ import annotations

import argparse
import io
import json
import sys
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

from lib.corpus import BRACKET_SLUGS, load_corpus                   # noqa: E402

REPORTS_DIR = BENCHMARK_DIR / "reports"
RUN_REPORT  = REPORTS_DIR / "last_rewrite_run.json"


def corpus_inputs_for(bracket: str) -> dict[str, dict]:
    """Return ``{sample_id → {raw_text, duration_seconds}}`` for the bracket."""
    glob = f"telemetry/corpus-{bracket}/corpus.jsonl"
    samples = load_corpus(str(BENCHMARK_DIR / glob))
    return {
        s.id: {
            "raw_text":         s.raw_text,
            "duration_seconds": s.duration_seconds,
        }
        for s in samples
    }


def indent(text: str, prefix: str = "    ") -> str:
    return "\n".join(prefix + line for line in text.splitlines())


def build_block(bracket: str, entry: dict, source: dict | None) -> str:
    sid    = entry.get("id", "?")
    rule   = entry.get("rule") or {}
    output = entry.get("output_text", "")
    cata   = " CATASTROPHE" if entry.get("catastrophe") else ""

    if source:
        dur     = source["duration_seconds"]
        raw     = source["raw_text"]
    else:
        dur     = entry.get("duration_sec", 0)
        raw     = "(raw input not found in corpus)"

    header = (
        f"[{bracket}  {sid}  {dur:.0f}s  in={len(raw)}c]{cata}"
    )
    stats = (
        f"chars={len(output)}  "
        f"novel={rule.get('novel_words', 0):.3f}  "
        f"len_ratio={rule.get('length_ratio', 0):.3f}  "
        f"preamble={int(rule.get('preamble', 0))}  "
        f"lists={int(rule.get('lists', 0))}"
    )
    return "\n".join([
        header,
        "INPUT (raw Whisper):",
        indent(raw),
        "",
        f"--- OUTPUT  {stats}",
        indent(output),
        "",
    ])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--report", type=Path, default=RUN_REPORT,
        help="Path to last_rewrite_run.json (default: reports/last_rewrite_run.json).",
    )
    parser.add_argument(
        "--output", type=Path, default=REPORTS_DIR / "comparison.txt",
        help="Destination path (default: reports/comparison.txt).",
    )
    args = parser.parse_args()

    if not args.report.exists():
        sys.exit(
            f"Run report not found: {args.report}\n"
            f"Run `python rewrite_bench.py` first."
        )

    run = json.loads(args.report.read_text(encoding="utf-8"))
    results = run.get("results") or []

    # Pre-load corpora for all brackets we care about
    inputs_by_bracket: dict[str, dict[str, dict]] = {}
    for r in results:
        bracket = r.get("bracket")
        if bracket and bracket in BRACKET_SLUGS and bracket not in inputs_by_bracket:
            inputs_by_bracket[bracket] = corpus_inputs_for(bracket)

    out_lines: list[str] = [
        "Side-by-side comparison — 4 cleanup-bracket rewrite axes",
        "=" * 70,
        f"Source: {args.report.relative_to(BENCHMARK_DIR)}",
        f"Generated: {run.get('timestamp', '?')}",
        "",
    ]

    for r in results:
        bracket = r.get("bracket", "?")
        out_lines.append("=" * 70)
        if "error" in r:
            out_lines.append(f"## {bracket}: SKIPPED ({r['error']})")
            out_lines.append("")
            continue
        if "warning" in r:
            out_lines.append(f"## {bracket}: {r['warning']}")
            out_lines.append("")
            continue

        out_lines.append(
            f"## {bracket}  ({r.get('samples', 0)} samples, "
            f"med={r.get('composite_median')}, cata={r.get('catastrophes')})"
        )
        out_lines.append(
            f"   prompt: {r.get('prompt_file')}  "
            f"model: {r.get('model')}  "
            f"T={r.get('temperature')}  "
            f"ctx={r.get('num_ctx_k')}K"
        )
        out_lines.append("")
        sources = inputs_by_bracket.get(bracket, {})
        for entry in r.get("details", []):
            out_lines.append(build_block(bracket, entry, sources.get(entry.get("id"))))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(out_lines), encoding="utf-8")
    print(f"  → {args.output.relative_to(BENCHMARK_DIR)}")


if __name__ == "__main__":
    main()
