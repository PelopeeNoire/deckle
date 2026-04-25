"""Template for a new benchmark — copy, rename, edit. Lorem ipsum inside.

Drop the leading underscore from the filename when you copy this — files
named ``_*.py`` are intentionally excluded from ``launch.ps1`` discovery.

The first line of this docstring (the line above) is what shows up in
the launcher menu. Keep it under ~80 chars and start with a verb.

Conventions a real benchmark must follow:
    - Filename ends in ``_bench.py`` (e.g. ``translate_bench.py``).
    - Outputs go to ``reports/last_<name>_run.{json,txt}``
      where ``<name>`` matches the script (without ``_bench`` suffix).
    - No automated LLM judge calls. The agent (Claude session, Louis)
      reads the JSON/TXT and judges qualitatively. Never put an API key
      in this file or in any sibling.
    - Optional ``--bracket`` flag if the bench filters by audio duration
      tier (relecture / lissage / affinage / arrangement).

See ``AGENT.md`` for the full pattern and a worked example.
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import time
from pathlib import Path

# Force UTF-8 stdout/stderr on Windows so accented output survives
# terminal redirection.
if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

# Real benches will import what they need from lib/. Examples:
#     from lib.corpus import BRACKET_SLUGS, load_corpus
#     from lib import ollama as ollama_client


REPORTS_DIR = BENCHMARK_DIR / "reports"

# Lorem ipsum placeholder so the template is illustrative but not a
# functional bench by accident. Replace with your real default config.
PLACEHOLDER_PROMPT = (
    "Lorem ipsum dolor sit amet, consectetur adipiscing elit. "
    "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua."
)


def run(*, prompt: str, limit: int | None, verbose: bool) -> dict:
    """Execute the bench. Replace this body with your actual logic.

    Expected pattern:
        - iterate over the items you bench (samples, prompts, configs…)
        - for each, run the system under test, record the outcome
        - aggregate runtime stats (duration, RTF, token counts, …)
        - dump a structured JSON + a human-readable TXT companion under
          ``reports/`` so the agent (and Louis) can read it
    """
    items = ["alpha", "beta", "gamma"]  # ← replace with real iteration
    if limit is not None:
        items = items[:limit]

    print(f"=== Template bench: prompt={len(prompt)} chars | items={len(items)} ===\n")

    details: list[dict] = []
    t_total0 = time.time()
    for i, item in enumerate(items, start=1):
        print(f"[{i}/{len(items)}] {item}...", end=" ", flush=True)
        # ← replace with the actual work
        time.sleep(0.05)
        details.append({"item": item, "result": f"placeholder for {item}"})
        print("OK")
        if verbose:
            print(f"  detail: placeholder for {item}")

    report = {
        "timestamp":     time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "prompt":        prompt,
        "items":         len(items),
        "elapsed_sec":   round(time.time() - t_total0, 1),
        "details":       details,
    }

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    name = Path(__file__).stem.removesuffix("_bench")  # e.g. "translate"
    json_path = REPORTS_DIR / f"last_{name}_run.json"
    txt_path  = REPORTS_DIR / f"last_{name}_run.txt"
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    txt_path.write_text(
        "\n".join(
            [f"Bench {name} — {report['timestamp']}",
             f"  prompt ({len(prompt)} chars): {prompt[:80]}…" if len(prompt) > 80 else f"  prompt ({len(prompt)} chars): {prompt}",
             f"  items  : {report['items']} (elapsed {report['elapsed_sec']}s)",
             "",
             "─" * 70,
             *[f"\n[{d['item']}] {d['result']}" for d in details]]
        ),
        encoding="utf-8",
    )

    print(f"\n  JSON  → {json_path}")
    print(f"  TXT   → {txt_path}")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--prompt",       default=PLACEHOLDER_PROMPT,
                        help="Prompt under test (replace placeholder with real content)")
    parser.add_argument("--prompt-file",  default=None,
                        help="Read prompt from a file (overrides --prompt)")
    parser.add_argument("--limit",        type=int, default=None,
                        help="Process only the first N items")
    parser.add_argument("--verbose",      action="store_true")
    args = parser.parse_args()

    prompt = args.prompt
    if args.prompt_file:
        path = Path(args.prompt_file)
        if not path.is_absolute():
            path = BENCHMARK_DIR / path
        prompt = path.read_text(encoding="utf-8").strip()

    run(prompt=prompt, limit=args.limit, verbose=args.verbose)


if __name__ == "__main__":
    main()
