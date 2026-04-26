"""Side-by-side report assembler over the 4 cleanup-level axes.

Reads ``reports/run_{relecture,lissage,affinage,arrangement}.json`` and
emits a ``reports/comparison.txt`` with one block per source sample::

    [sample_id  duration  bracket  input_chars]
    INPUT (raw Whisper):
        <raw text>
    --- RELECTURE   (chars=…)  novel=…  len=…
        <output>
    --- LISSAGE     (chars=…)  novel=…  len=…
        <output>
    --- AFFINAGE    (chars=…)  novel=…  len=…
        <output>
    --- ARRANGEMENT (chars=…)  novel=…  len=…
        <output>

This is the file the agent reads to score qualitatively against the
6-criteria grid (judge_system_prompt.txt). The composite axis gets its
own block at the bottom (only one sample, no comparison across axes).

Usage:
    python compare_runs.py [--output PATH]
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
REPORTS_DIR   = BENCHMARK_DIR / "reports"

CORE_AXES = ("relecture", "lissage", "affinage", "arrangement")
COMPOSITE_AXES = ("arrangement_composite",)


def load_report(axis: str) -> dict | None:
    path = REPORTS_DIR / f"run_{axis}.json"
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None


def load_corpus_inputs(corpus_glob: str) -> dict[str, dict]:
    """Re-read corpus to recover raw input text by sample id ({slug}:{line_no})."""
    import glob as _glob
    inputs: dict[str, dict] = {}
    for path in sorted(_glob.glob(str(BENCHMARK_DIR / corpus_glob))):
        with open(path, "r", encoding="utf-8") as f:
            for line_no, line in enumerate(f, start=1):
                line = line.strip()
                if not line: continue
                try:
                    env = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if env.get("kind", "").lower() != "corpus": continue
                payload = env.get("payload") or {}
                slug    = payload.get("slug") or ""
                sid     = f"{slug}:{line_no}"
                inputs[sid] = {
                    "raw_text":         (payload.get("raw") or {}).get("text", ""),
                    "duration_seconds": payload.get("duration_seconds", 0.0),
                    "audio_file":       payload.get("audio_file"),
                }
    return inputs


def bracket_of(duration_seconds: float) -> str:
    if duration_seconds <= 60:    return "relecture"
    if duration_seconds <= 300:   return "lissage"
    if duration_seconds <= 600:   return "affinage"
    if duration_seconds <= 1200:  return "arrangement"
    return "—"


def build_core_blocks(inputs: dict[str, dict], reports: dict[str, dict]) -> list[str]:
    """One block per sample id present in the inputs, with side-by-side outputs."""
    blocks: list[str] = []
    sample_ids = sorted(inputs.keys(), key=lambda s: int(s.split(":")[-1]))

    for sid in sample_ids:
        ip = inputs[sid]
        dur = ip["duration_seconds"]
        bracket = bracket_of(dur)
        raw_text = ip["raw_text"]

        header = f"[{sid}  {dur:.0f}s  {bracket}  in={len(raw_text)}c]"
        block_lines = [header, "INPUT (raw Whisper):", _indent(raw_text), ""]

        for axis in CORE_AXES:
            report = reports.get(axis)
            entry  = _find_entry(report, sid)
            if entry is None:
                block_lines.append(f"--- {axis.upper():12s} (missing)")
                block_lines.append("")
                continue
            output = entry.get("output_text", "")
            rule   = entry.get("rule", {})
            novel  = rule.get("novel_words", float("nan"))
            lr     = rule.get("length_ratio", float("nan"))
            preamb = rule.get("preamble", 0)
            lists  = rule.get("lists", 0)
            cat    = " CATA" if entry.get("catastrophe") else ""
            stats  = f"chars={len(output)}  novel={novel:.3f}  len={lr:.3f}  preamble={int(preamb)}  lists={int(lists)}{cat}"

            block_lines.append(f"--- {axis.upper():12s}  {stats}")
            block_lines.append(_indent(output))
            block_lines.append("")
        blocks.append("\n".join(block_lines))
    return blocks


def build_composite_blocks(reports: dict[str, dict]) -> list[str]:
    blocks: list[str] = []
    for axis in COMPOSITE_AXES:
        report = reports.get(axis)
        if report is None:
            blocks.append(f"=== {axis.upper()} (no report)")
            continue
        for entry in report.get("details", []):
            sid    = entry.get("id", "?")
            inc    = entry.get("input_chars", 0)
            outc   = entry.get("output_chars", 0)
            rule   = entry.get("rule", {})
            output = entry.get("output_text", "")

            header = f"=== {axis.upper()}  [{sid}  in={inc}c  out={outc}c  novel={rule.get('novel_words', 0):.3f}  len={rule.get('length_ratio', 0):.3f}]"
            block_lines = [header, output, ""]
            blocks.append("\n".join(block_lines))
    return blocks


def _indent(text: str, prefix: str = "    ") -> str:
    return "\n".join(prefix + line for line in text.splitlines())


def _find_entry(report: dict | None, sid: str) -> dict | None:
    if not report: return None
    for entry in report.get("details", []):
        if entry.get("id") == sid:
            return entry
    return None


def main() -> None:
    parser = argparse.ArgumentParser(description="Side-by-side comparison of the 4 cleanup-level runs")
    parser.add_argument("--output", type=Path, default=REPORTS_DIR / "comparison.txt")
    args = parser.parse_args()

    reports = {axis: load_report(axis) for axis in CORE_AXES + COMPOSITE_AXES}
    missing = [a for a, r in reports.items() if r is None]
    if missing:
        print(f"  warning: {len(missing)} axis report(s) missing: {missing}")

    # Use the corpus glob from any available core report.
    corpus_inputs: dict[str, dict] = {}
    if reports.get("relecture") or reports.get("lissage"):
        corpus_inputs = load_corpus_inputs("telemetry/nettoyage-69b8e91208d4/corpus.refreshed.jsonl")

    out_lines = [
        "Side-by-side comparison — 4 cleanup-level rewrite axes",
        "=" * 70,
        f"Reports loaded: {[a for a, r in reports.items() if r is not None]}",
        f"Samples in inputs: {len(corpus_inputs)}",
        "",
    ]

    if corpus_inputs:
        out_lines.append("CORE CORPUS (refreshed, 27 samples)")
        out_lines.append("=" * 70)
        out_lines.extend(build_core_blocks(corpus_inputs, reports))

    out_lines.append("")
    out_lines.append("LONG-FORM COMPOSITE (samples 3 + 11)")
    out_lines.append("=" * 70)
    out_lines.extend(build_composite_blocks(reports))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(out_lines), encoding="utf-8")
    print(f"\n→ {args.output}")
    print(f"  {len([r for r in reports.values() if r is not None])}/{len(reports)} axes loaded")


if __name__ == "__main__":
    main()
