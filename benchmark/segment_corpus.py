"""Segment a corpus into 4 bracket-keyed corpora for rewrite_bench.

Two source flavours, mixable in a single run :

1. **JSONL corpus** (``--source PATH``) — typically a fresh dictation
   log or the output of ``refresh_corpus.py``. Each line is a Corpus
   envelope, bucketed by ``payload.duration_seconds``.

2. **Exemples ``.txt`` corpus** (``--from-exemples-txt PATH``) —
   historical transcript files where blocks are separated by either
   ``---`` on a line by itself (independent samples) or blank-line
   runs (legacy format without explicit dashes). Within a ``---``
   block, ``+++`` on a line by itself joins fragments of the same
   recording (concatenated with a blank line). No audio file is
   recorded — duration is estimated from word count at 1.85 wps (≈
   slow-paced French dictation pace).

Both modes write to the same ``telemetry/corpus-<bracket>/corpus.jsonl``
buckets. ``rewrite_bench.py`` reads those four files directly.

Usage::

    # Single JSONL corpus, wipes existing corpus-<bracket>/ dirs first
    python segment_corpus.py --source telemetry/<slug>/corpus.jsonl

    # Combine real recordings + exemples .txt in one shot
    python segment_corpus.py \\
        --source telemetry/<slug>/corpus.jsonl \\
        --from-exemples-txt telemetry/exemples/exemples-transcripts--prompts.txt

    # Append to existing buckets instead of wiping them
    python segment_corpus.py --source <path> --append

Brackets and their bounds (left-exclusive, right-inclusive, except
relecture which is right-inclusive at 60 s)::

    relecture     d ≤ 60 s
    lissage       60 < d ≤ 300 s
    affinage     300 < d ≤ 600 s
    arrangement  600 < d (no upper cap on bench side, even though Deckle
                          enforces 1200 s on capture)
"""

from __future__ import annotations

import argparse
import datetime
import io
import json
import re
import shutil
import sys
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
TELEMETRY_DIR = BENCHMARK_DIR / "telemetry"

# Bracket bounds match lib/corpus.py:38-47 with the bench-side
# exception that arrangement has no upper cap (rewrite_bench can ingest
# anything that fits the LLM context).
BRACKETS: tuple[tuple[str, float, float], ...] = (
    ("relecture",     0.0,    60.0),
    ("lissage",      60.0,   300.0),
    ("affinage",    300.0,   600.0),
    ("arrangement", 600.0, float("inf")),
)

# Words-per-second used to estimate duration on text-only sources
# (no audio file behind). Calibrated on the project's typical
# dictation pace — ~111 wpm. Same convention as the prior
# build_exemples_corpus.py.
WPS_ESTIMATE = 1.85

# Patterns for the exemples .txt format (anchored on whole lines).
RE_BLOCK_SEP    = re.compile(r"^[ \t]*---[ \t]*$", re.MULTILINE)
RE_FRAGMENT_SEP = re.compile(r"^[ \t]*\+\+\+[ \t]*$", re.MULTILINE)


def bracket_for(duration_s: float) -> str | None:
    """Returns the bracket slug for a sample duration, or None if invalid."""
    if duration_s <= 0:
        return None
    if duration_s <= BRACKETS[0][2]:           # relecture is right-inclusive at 60 s
        return BRACKETS[0][0]
    for slug, lo, hi in BRACKETS[1:]:          # the others are left-exclusive
        if lo < duration_s <= hi:
            return slug
    return None


def load_jsonl(path: Path) -> list[dict]:
    """Read a corpus.jsonl, keep only ``kind=Corpus`` envelopes."""
    samples: list[dict] = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                env = json.loads(line)
            except json.JSONDecodeError:
                continue
            if env.get("kind", "").lower() != "corpus":
                continue
            samples.append(env)
    return samples


def parse_exemples_txt(path: Path) -> list[str]:
    """Parse an exemples .txt into a list of text blocks.

    Two flavours:

    - With ``---`` separators: split on those (whole lines), then
      within each chunk split on ``+++`` (lines that join fragments
      from the same recording) and re-join them with a blank line.
    - Without ``---`` separators (legacy): split on blank-line runs
      (two or more consecutive newlines). Each paragraph is a block.

    Empty / whitespace-only blocks are dropped.
    """
    text = path.read_text(encoding="utf-8")

    if RE_BLOCK_SEP.search(text):
        # Split on lines containing only `---`
        chunks = RE_BLOCK_SEP.split(text)
        blocks: list[str] = []
        for chunk in chunks:
            # Within each chunk, `+++` joins fragments of the same recording
            # — replace the marker with a paragraph break.
            joined = RE_FRAGMENT_SEP.sub("\n\n", chunk).strip()
            if joined:
                blocks.append(joined)
        return blocks

    # Legacy: paragraphs separated by blank-line runs
    chunks = re.split(r"\n[ \t]*\n+", text)
    return [c.strip() for c in chunks if c.strip()]


def envelope_from_text_block(
    block:        str,
    source_label: str,
    block_index:  int,
) -> dict:
    """Build a Corpus envelope from a raw text block.

    No audio behind — duration is estimated from word count at
    ``WPS_ESTIMATE``. Slug / profile_id / profile are rewritten by
    ``relabel_envelope`` later, so we leave them as placeholders.
    """
    word_count = len(block.split())
    duration   = round(word_count / WPS_ESTIMATE, 2)
    timestamp  = (
        datetime.datetime.now(datetime.timezone.utc)
        .isoformat(timespec="microseconds")
    )
    return {
        "timestamp":  timestamp,
        "kind":       "Corpus",
        "session":    f"exemples-{source_label}-{block_index:02d}",
        "payload": {
            "profile":          "Reconstructed",
            "profile_id":       f"exemples-{source_label}",
            "slug":             "TBD",
            "duration_seconds": duration,
            "whisper": {
                "model":          "exemples-historic",
                "language":       "fr",
                "elapsed_ms":     0,
                "initial_prompt": f"(historic .txt — duration estimated at {WPS_ESTIMATE} wps)",
            },
            "raw": {
                "text":       block,
                "word_count": word_count,
                "char_count": len(block),
            },
            "metrics": {"words_per_second": WPS_ESTIMATE},
            "audio_file":           None,
            "exemples_source":      source_label,
            "exemples_block_index": block_index,
        },
    }


def relabel_envelope(env: dict, bracket: str) -> dict:
    """Deep-copy and rewrite the slug/profile fields to identify the bucket."""
    out = json.loads(json.dumps(env))
    payload = out.setdefault("payload", {})
    payload["slug"]       = f"corpus-{bracket}"
    payload["profile_id"] = f"corpus-{bracket}"
    payload["profile"]    = bracket.capitalize()
    return out


def collect_samples_from_args(args: argparse.Namespace) -> list[dict]:
    """Combine all sources (--source JSONL + --from-exemples-txt) into one list."""
    samples: list[dict] = []

    for s in (args.source or []):
        p = Path(s)
        if not p.is_absolute():
            p = BENCHMARK_DIR / p
        if not p.exists():
            sys.exit(f"Source not found: {p}")
        loaded = load_jsonl(p)
        samples.extend(loaded)
        print(f"  source jsonl    : {p.relative_to(BENCHMARK_DIR)}  → {len(loaded)} samples")

    for s in (args.from_exemples_txt or []):
        p = Path(s)
        if not p.is_absolute():
            p = BENCHMARK_DIR / p
        if not p.exists():
            sys.exit(f"Exemples .txt not found: {p}")
        blocks = parse_exemples_txt(p)
        # Source label = filename stem, with the boring "exemples-transcripts--"
        # prefix stripped if present, for nicer slug output.
        label = p.stem
        prefix = "exemples-transcripts--"
        if label.startswith(prefix):
            label = label[len(prefix):]
        for i, block in enumerate(blocks, start=1):
            samples.append(envelope_from_text_block(block, label, i))
        print(f"  source txt      : {p.relative_to(BENCHMARK_DIR)}  → {len(blocks)} blocks ({label})")

    return samples


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--source", action="append", default=[],
        help="Source corpus.jsonl (relative to benchmark/ or absolute). "
             "Repeat to merge several sources into the same buckets.",
    )
    parser.add_argument(
        "--from-exemples-txt", action="append", default=[],
        dest="from_exemples_txt",
        help="Source exemples .txt (project format with --- between blocks "
             "and +++ joining fragments of the same recording). Duration "
             "is estimated at 1.85 wps. Repeat to merge several .txt sources.",
    )
    parser.add_argument(
        "--append", action="store_true",
        help="Keep existing corpus-<bracket>/corpus.jsonl files and append "
             "to them. Default behaviour is to wipe and rewrite each bucket.",
    )
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    if not args.source and not args.from_exemples_txt:
        sys.exit("At least one --source or --from-exemples-txt must be provided.")

    samples = collect_samples_from_args(args)
    print(f"  total           : {len(samples)} samples")

    # Bucket
    bucketed: dict[str, list[dict]] = {slug: [] for slug, _, _ in BRACKETS}
    dropped = 0
    for env in samples:
        d = float(env.get("payload", {}).get("duration_seconds", 0))
        slug = bracket_for(d)
        if slug is None:
            dropped += 1
            if args.verbose:
                print(f"  drop  : duration={d}s ({env.get('session', '?')})")
            continue
        bucketed[slug].append(relabel_envelope(env, slug))
    if dropped:
        print(f"  dropped (invalid duration): {dropped}")

    # Wipe existing buckets unless --append
    if not args.append:
        for slug, _, _ in BRACKETS:
            d = TELEMETRY_DIR / f"corpus-{slug}"
            if d.exists():
                shutil.rmtree(d)

    # Emit
    print()
    print("  Bucket       | n  | duration range")
    print("  -------------|----|----------------")
    for slug, _, _ in BRACKETS:
        out_dir = TELEMETRY_DIR / f"corpus-{slug}"
        out_dir.mkdir(parents=True, exist_ok=True)
        out_path = out_dir / "corpus.jsonl"
        mode = "a" if args.append else "w"
        with out_path.open(mode, encoding="utf-8") as f:
            for env in bucketed[slug]:
                f.write(json.dumps(env, ensure_ascii=False) + "\n")

        if not bucketed[slug]:
            print(f"  {slug:12s} |  0 | (vide)")
            continue
        durs = sorted(s["payload"]["duration_seconds"] for s in bucketed[slug])
        med = durs[len(durs) // 2]
        print(f"  {slug:12s} | {len(durs):2d} | {durs[0]:6.1f} – {durs[-1]:6.1f} s  (med {med:.0f} s)")

    print(f"\n  → {TELEMETRY_DIR.relative_to(BENCHMARK_DIR)}/corpus-<bracket>/corpus.jsonl")


if __name__ == "__main__":
    main()
