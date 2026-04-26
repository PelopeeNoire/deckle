"""Aggregate stats for the bracket × temperature sweep.

Reads every ``reports/run_<axis>_t<temp>.json`` produced by
``run_all_profiles.py --temperatures …`` and prints a side-by-side
table per (axis, temperature).

Usage:
    python analyze_sweep.py
"""

from __future__ import annotations

import io
import json
import re
import sys
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BENCHMARK_DIR = Path(__file__).resolve().parent
REPORTS_DIR   = BENCHMARK_DIR / "reports"

PATTERN = re.compile(r"^run_(?P<axis>[a-z_]+)_t(?P<temp>\d+(?:\.\d+)?)\.json$")


def stat_block(report: dict) -> dict:
    details = report.get("details", [])
    ratios  = sorted(d.get("length_ratio", 0) for d in details)
    novels  = sorted(d.get("rule", {}).get("novel_words", 0) for d in details)
    if not details:
        return {"n": 0}
    return {
        "n":         len(details),
        "cata":      sum(1 for d in details if d.get("catastrophe")),
        "lists":     sum(1 for d in details if d.get("rule", {}).get("lists", 0)),
        "preamble":  sum(1 for d in details if d.get("rule", {}).get("preamble", 0)),
        "ratio_min": ratios[0],
        "ratio_med": ratios[len(ratios)//2],
        "ratio_max": ratios[-1],
        "novel_med": novels[len(novels)//2],
        "novel_max": novels[-1],
    }


def main() -> None:
    runs: dict[tuple[str, str], dict] = {}
    for path in REPORTS_DIR.glob("run_*_t*.json"):
        m = PATTERN.match(path.name)
        if not m: continue
        axis = m.group("axis")
        temp = m.group("temp")
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            continue
        runs[(axis, temp)] = stat_block(data)

    if not runs:
        sys.exit("No bracket-temp sweep reports found in reports/.")

    # Group by axis
    by_axis: dict[str, list[tuple[str, dict]]] = {}
    for (axis, temp), st in sorted(runs.items()):
        by_axis.setdefault(axis, []).append((temp, st))

    AXIS_ORDER = ["relecture", "lissage", "affinage", "arrangement"]
    for axis in AXIS_ORDER:
        if axis not in by_axis: continue
        rows = by_axis[axis]
        print(f"\n=== {axis.upper()} ({rows[0][1].get('n', 0)} samples) ===")
        print(f"  {'temp':<6}  {'cata':<5} {'lists':<6} {'preamble':<9} {'ratio min/med/max':<22} {'novel med/max':<14}")
        print(f"  {'-'*6}  {'-'*5} {'-'*6} {'-'*9} {'-'*22} {'-'*14}")
        for temp, st in rows:
            if st.get("n", 0) == 0:
                print(f"  {temp:<6}  (empty)")
                continue
            print(
                f"  {temp:<6}  "
                f"{st['cata']:<5} {st['lists']:<6} {st['preamble']:<9} "
                f"{st['ratio_min']:.2f}/{st['ratio_med']:.2f}/{st['ratio_max']:.2f}    "
                f"{st['novel_med']:.2f}/{st['novel_max']:.2f}"
            )


if __name__ == "__main__":
    main()
