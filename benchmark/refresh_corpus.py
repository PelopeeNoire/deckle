"""Fuse a corpus.jsonl with a whisper_bench report into corpus.refreshed.jsonl.

The original ``corpus.jsonl`` is the WhispUI telemetry source of truth and
must stay immutable. When the Whisper initial prompt changes, the LLM
rewrite bench should run on the *new* raw text — but the rest of the
envelope (timestamp, session, audio_file, duration, profile, slug,
metrics) stays valid. This script clones each envelope and overwrites
only the four Whisper-derived fields::

    payload.raw.text
    payload.raw.word_count
    payload.raw.char_count
    payload.whisper.initial_prompt

Match key between the JSONL and the report is ``Sample.id`` from
``lib/corpus.py`` — i.e. ``"{slug}:{line_no}"``, line numbers 1-indexed
across the source file. ``whisper_bench.py`` writes that exact id into
each ``details[i]["id"]``.

Usage:
    python refresh_corpus.py [--source-corpus PATH] [--report PATH]
                             [--output PATH] [--verbose]
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
DEFAULT_REPORT = BENCHMARK_DIR / "reports" / "last_whisper_run.json"


def load_report(path: Path) -> tuple[str, dict[str, str]]:
    """Return ``(initial_prompt, {sample_id: output_text})`` from a report."""
    data = json.loads(path.read_text(encoding="utf-8"))
    initial_prompt = data.get("initial_prompt", "")
    mapping: dict[str, str] = {}
    for entry in data.get("details", []):
        if "error" in entry:
            continue
        sample_id = entry.get("id")
        text      = entry.get("output_text")
        if sample_id and text is not None:
            mapping[sample_id] = text
    return initial_prompt, mapping


def refresh_envelope(envelope: dict, slug: str, line_no: int,
                     mapping: dict[str, str], initial_prompt: str) -> tuple[dict, bool]:
    """Return ``(refreshed, was_refreshed)``. The original is not mutated."""
    sample_id = f"{slug}:{line_no}"
    new_text  = mapping.get(sample_id)
    if new_text is None:
        return envelope, False

    refreshed = json.loads(json.dumps(envelope))
    payload   = refreshed.setdefault("payload", {})
    raw       = payload.setdefault("raw", {})
    whisper   = payload.setdefault("whisper", {})

    raw["text"]               = new_text
    raw["word_count"]         = len(new_text.split())
    raw["char_count"]         = len(new_text)
    whisper["initial_prompt"] = initial_prompt
    return refreshed, True


def refresh_file(source: Path, report_path: Path, output: Path, verbose: bool) -> None:
    initial_prompt, mapping = load_report(report_path)
    if not mapping:
        sys.exit(f"Report {report_path} has no usable details — aborting.")

    refreshed_count = 0
    skipped_count   = 0
    total_lines     = 0

    with source.open("r", encoding="utf-8") as src, output.open("w", encoding="utf-8") as dst:
        for line_no, line in enumerate(src, start=1):
            stripped = line.strip()
            if not stripped:
                dst.write(line)
                continue
            total_lines += 1
            try:
                envelope = json.loads(stripped)
            except json.JSONDecodeError:
                if verbose:
                    print(f"  line {line_no}: malformed JSON — passthrough")
                dst.write(line)
                continue

            kind = str(envelope.get("kind", "")).lower()
            if kind != "corpus":
                dst.write(line)
                continue

            slug = str((envelope.get("payload") or {}).get("slug") or "")
            new_envelope, did_refresh = refresh_envelope(
                envelope, slug, line_no, mapping, initial_prompt
            )
            if did_refresh:
                refreshed_count += 1
                if verbose:
                    new_text = new_envelope["payload"]["raw"]["text"]
                    print(f"  line {line_no} ({slug}): refreshed ({len(new_text)} chars)")
            else:
                skipped_count += 1
                if verbose:
                    print(f"  line {line_no} ({slug}): no match in report — passthrough")

            dst.write(json.dumps(new_envelope, ensure_ascii=False) + "\n")

    print(f"\n→ {output}")
    print(f"  refreshed: {refreshed_count} / {total_lines} corpus envelopes")
    if skipped_count:
        print(f"  skipped:   {skipped_count} (no matching id in report)")


def main() -> None:
    parser = argparse.ArgumentParser(description="Refresh a corpus.jsonl with a whisper_bench report")
    parser.add_argument("--source-corpus", type=Path, required=True,
                        help="Path to the corpus.jsonl to refresh.")
    parser.add_argument("--report", type=Path, default=DEFAULT_REPORT,
                        help=f"whisper_bench report (default: {DEFAULT_REPORT.relative_to(BENCHMARK_DIR)})")
    parser.add_argument("--output", type=Path, default=None,
                        help="Output JSONL (default: <source>.refreshed.jsonl in the same folder)")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    source = args.source_corpus.resolve()
    if not source.exists():
        sys.exit(f"Source corpus not found: {source}")
    report = args.report.resolve()
    if not report.exists():
        sys.exit(f"Report not found: {report}")

    output = args.output
    if output is None:
        output = source.with_name(source.stem + ".refreshed" + source.suffix)
    output = output.resolve()

    refresh_file(source, report, output, args.verbose)


if __name__ == "__main__":
    main()
