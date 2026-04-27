"""Compute words-per-minute distribution from corpus.jsonl files.

Source: every benchmark/telemetry/**/corpus.jsonl that ships in the repo.
Output: per-bracket and global stats so we can decide whether the current
AutoRewriteRulesByWords thresholds (150/750/1500) match real usage or are
off-target.

Run: python wpm_stats.py
"""

from __future__ import annotations

import json
import re
import statistics
from pathlib import Path

# Brackets used by the LLM rewrite rules in the app:
#   ≤  60 s  → Relecture
#   60–300   → Lissage
#   300–600  → Affinage
#   600+     → Arrangement
DURATION_BUCKETS = [
    ("Relecture",    0,    60),
    ("Lissage",     60,   300),
    ("Affinage",   300,   600),
    ("Arrangement", 600, 10**9),
]

WORD_RE = re.compile(r"\S+")

def count_words(text: str) -> int:
    return len(WORD_RE.findall(text or ""))

def bucket(seconds: float) -> str:
    for name, lo, hi in DURATION_BUCKETS:
        if lo <= seconds < hi:
            return name
    return "?"

def main() -> None:
    root = Path(__file__).parent / "telemetry"
    rows: list[tuple[float, int, str]] = []  # (duration_s, words, bucket)

    for path in sorted(root.rglob("corpus.jsonl")):
        with path.open(encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                payload = obj.get("payload", {})
                duration = payload.get("duration_seconds")
                raw = payload.get("raw", {})
                text = raw.get("text") or ""
                if duration is None:
                    continue
                words = count_words(text)
                rows.append((float(duration), words, bucket(float(duration))))

    if not rows:
        print("No samples found.")
        return

    # Global wpm.
    durations = [r[0] for r in rows]
    words = [r[1] for r in rows]
    wpms = [w / (d / 60.0) for d, w, _ in rows if d > 0]

    print(f"Samples: {len(rows)}")
    print(f"Duration s: min {min(durations):.1f} | median {statistics.median(durations):.1f} | "
          f"mean {statistics.mean(durations):.1f} | max {max(durations):.1f}")
    print(f"Words:      min {min(words)}    | median {statistics.median(words):.0f}     | "
          f"mean {statistics.mean(words):.0f}     | max {max(words)}")
    print(f"WPM global: min {min(wpms):.0f}   | median {statistics.median(wpms):.0f}    | "
          f"mean {statistics.mean(wpms):.0f}    | max {max(wpms):.0f}")
    print()

    # Per-bracket aggregation.
    print(f"{'Bracket':<14}{'N':>4}{'dur_med_s':>11}{'words_med':>11}{'wpm_med':>10}{'wpm_p25':>10}{'wpm_p75':>10}")
    by_bucket: dict[str, list[tuple[float, int]]] = {b: [] for b, _, _ in DURATION_BUCKETS}
    for d, w, b in rows:
        if b in by_bucket:
            by_bucket[b].append((d, w))

    for name, lo, hi in DURATION_BUCKETS:
        lst = by_bucket.get(name, [])
        if not lst:
            print(f"{name:<14}{0:>4}")
            continue
        ds = [d for d, _ in lst]
        ws = [w for _, w in lst]
        wpm = [w / (d / 60.0) for d, w in lst if d > 0]
        if not wpm:
            continue
        sorted_wpm = sorted(wpm)
        n = len(sorted_wpm)
        p25 = sorted_wpm[n // 4]
        p75 = sorted_wpm[(3 * n) // 4]
        print(f"{name:<14}{len(lst):>4}{statistics.median(ds):>11.1f}"
              f"{statistics.median(ws):>11.0f}{statistics.median(wpm):>10.0f}"
              f"{p25:>10.0f}{p75:>10.0f}")

    print()
    # Suggested word thresholds — the median wpm of the bracket BELOW the
    # boundary times the boundary in minutes. So 60s boundary uses Relecture
    # wpm, 300s uses Lissage wpm, 600s uses Affinage wpm.
    print("Suggested word thresholds (median wpm of bracket below × boundary minutes):")
    boundaries = [(60, "Relecture", "Lissage"),
                  (300, "Lissage", "Affinage"),
                  (600, "Affinage", "Arrangement")]
    for sec, below, above in boundaries:
        lst = by_bucket.get(below, [])
        wpm = [w / (d / 60.0) for d, w in lst if d > 0]
        if not wpm:
            continue
        med = statistics.median(wpm)
        threshold = int(round(med * (sec / 60.0)))
        print(f"  >={threshold:>5} words -> {above:<12} (using {below} median wpm = {med:.0f})")

if __name__ == "__main__":
    main()
