"""Convert benchmark/telemetry/exemples/*.txt into corpus.jsonl envelopes.

The exemples/ folder holds long-form raw transcriptions Louis dictated
earlier (10 to 45 min each), separated by lines of '---'. They are not
in JSONL format — this script wraps each block as a corpus envelope so
benchmark.py can consume them through --corpus-glob.

Output slug per source file:
    exemples-transcripts--prompts.txt        → exemples-prompts/corpus.jsonl
    exemples-transcripts--restructuration.txt → exemples-restructuration/corpus.jsonl
    exemples-transcripts--nettoyage.txt       → exemples-nettoyage-long/corpus.jsonl

Duration estimated at 1.85 words / sec (matches Louis' observed rate
from the existing corpus telemetry).
"""

from __future__ import annotations

import datetime
import io
import json
import re
import sys
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BENCHMARK_DIR = Path(__file__).resolve().parent
EXEMPLES_DIR  = BENCHMARK_DIR / "telemetry" / "exemples"
WPS           = 1.85  # words per second (matches Louis' observed rate)

SOURCES = [
    ("exemples-transcripts--prompts.txt",         "exemples-prompts"),
    ("exemples-transcripts--restructuration.txt", "exemples-restructuration"),
    ("exemples-transcripts--nettoyage.txt",       "exemples-nettoyage-long"),
]


def main() -> None:
    for fname, slug in SOURCES:
        src = EXEMPLES_DIR / fname
        if not src.exists():
            print(f"  skip: {src} (not found)")
            continue

        text   = src.read_text(encoding="utf-8")
        blocks = [b.strip() for b in re.split(r"\n---+\n", text) if b.strip()]
        if not blocks:
            print(f"  skip: {src} (no blocks)")
            continue

        out_dir = BENCHMARK_DIR / "telemetry" / slug
        out_dir.mkdir(parents=True, exist_ok=True)
        out    = out_dir / "corpus.jsonl"

        kept = 0
        with out.open("w", encoding="utf-8") as f:
            for i, block in enumerate(blocks, start=1):
                words = len(block.split())
                if words < 200:
                    continue  # too short to bench (<2 min)
                duration = round(words / WPS, 4)
                ts = (
                    datetime.datetime(2026, 4, 25, 22, 0, 0)
                    + datetime.timedelta(minutes=10 * i)
                ).astimezone().isoformat(timespec="microseconds")
                envelope = {
                    "timestamp": ts,
                    "kind":      "Corpus",
                    "session":   f"exemples-{slug[-12:]}-{i:02d}",
                    "payload": {
                        "profile":          "Restructuration",
                        "profile_id":       slug,
                        "slug":             slug,
                        "duration_seconds": duration,
                        "whisper": {
                            "model":          "ggml-large-v3.bin",
                            "language":       "fr",
                            "elapsed_ms":     0,
                            "initial_prompt": "(historic, prior to refresh)",
                        },
                        "raw": {
                            "text":       block,
                            "word_count": words,
                            "char_count": len(block),
                        },
                        "metrics": {
                            "words_per_second": WPS,
                        },
                        "audio_file":           None,
                        "exemples_block_index": i,
                    },
                }
                f.write(json.dumps(envelope, ensure_ascii=False) + "\n")
                kept += 1

        print(f"  {fname:50s} → telemetry/{slug}/corpus.jsonl  ({kept} blocks ≥200 words)")


if __name__ == "__main__":
    main()
