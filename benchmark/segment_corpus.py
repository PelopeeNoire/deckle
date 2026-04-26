"""Segment a corpus.jsonl into 4 bracket-keyed corpora for rewrite_bench.

Reads a source ``corpus.jsonl`` (typically a fresh dictation log or the
output of ``refresh_corpus.py``), buckets each sample by ``payload.
duration_seconds`` into the four cleanup brackets defined in
``lib/corpus.py``, and writes one JSONL per bracket under
``telemetry/corpus-<bracket>/corpus.jsonl``. ``rewrite_bench.py`` reads
those four files directly.

Each emitted envelope is a deep copy of the source — only the
``payload.slug`` / ``payload.profile`` / ``payload.profile_id`` fields
are rewritten so the segmented corpora are self-identifying. Sample
ordering inside a bucket follows the source order.

Usage::

    # Single source corpus, wipes existing corpus-<bracket>/ dirs first
    python segment_corpus.py --source telemetry/<slug>/corpus.jsonl

    # Merge several sources into the same buckets
    python segment_corpus.py --source A.jsonl --source B.jsonl

    # Keep existing corpus-<bracket>/ dirs (append rather than replace)
    python segment_corpus.py --source <path> --append

Brackets and their bounds (left-exclusive, right-inclusive, except
relecture which is right-inclusive at 60 s)::

    relecture     d ≤ 60 s
    lissage       60 < d ≤ 300 s
    affinage     300 < d ≤ 600 s
    arrangement  600 < d (no upper cap on bench side, even though WhispUI
                          enforces 1200 s on capture)
"""

from __future__ import annotations

import argparse
import io
import json
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


def relabel_envelope(env: dict, bracket: str) -> dict:
    """Deep-copy and rewrite the slug/profile fields to identify the bucket."""
    out = json.loads(json.dumps(env))
    payload = out.setdefault("payload", {})
    payload["slug"]       = f"corpus-{bracket}"
    payload["profile_id"] = f"corpus-{bracket}"
    payload["profile"]    = bracket.capitalize()
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--source", action="append", required=True,
        help="Source corpus.jsonl (relative to benchmark/ or absolute). "
             "Repeat to merge several sources into the same buckets.",
    )
    parser.add_argument(
        "--append", action="store_true",
        help="Keep existing corpus-<bracket>/corpus.jsonl files and append "
             "to them. Default behaviour is to wipe and rewrite each bucket.",
    )
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    # Resolve source paths
    source_paths: list[Path] = []
    for s in args.source:
        p = Path(s)
        if not p.is_absolute():
            p = BENCHMARK_DIR / p
        if not p.exists():
            sys.exit(f"Source not found: {p}")
        source_paths.append(p)

    # Collect samples
    samples: list[dict] = []
    for p in source_paths:
        loaded = load_jsonl(p)
        samples.extend(loaded)
        print(f"  source: {p.relative_to(BENCHMARK_DIR)}  → {len(loaded)} samples")
    print(f"  total : {len(samples)} samples")

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
