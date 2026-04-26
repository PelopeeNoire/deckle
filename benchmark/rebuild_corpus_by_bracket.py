"""Rebuild bench corpus segmented by duration bracket.

Replaces the per-source corpora (`telemetry/exemples-*/`,
`telemetry/restructuration-composite-*/`) with four bracket-keyed
corpora aligned on lib/corpus.py:38-47 :

    telemetry/corpus-relecture/corpus.jsonl     (≤ 60 s)
    telemetry/corpus-lissage/corpus.jsonl       (60 < d ≤ 300 s)
    telemetry/corpus-affinage/corpus.jsonl      (300 < d ≤ 600 s)
    telemetry/corpus-arrangement/corpus.jsonl   (600 < d ≤ 1200 s)

Sources merged :
    1. The existing ``corpus.refreshed.jsonl`` from
       ``telemetry/nettoyage-69b8e91208d4/`` (27 samples, durées exactes).
    2. The blocks split from ``telemetry/exemples/*.txt`` (durée estimée
       à 1.85 mots/sec — same convention as build_exemples_corpus.py).

Existing buckets get cleaned: any pre-existing
``telemetry/{exemples-*,restructuration-composite-*}`` folders are
deleted to keep the layout coherent.

Beyond the strict 1200 s ceiling, samples are dropped (hors capacité
WhispUI native).
"""

from __future__ import annotations

import io
import json
import re
import shutil
import sys
import datetime
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BENCHMARK_DIR  = Path(__file__).resolve().parent
TELEMETRY_DIR  = BENCHMARK_DIR / "telemetry"
EXEMPLES_DIR   = TELEMETRY_DIR / "exemples"
REFRESHED_PATH = TELEMETRY_DIR / "nettoyage-69b8e91208d4" / "corpus.refreshed.jsonl"
WPS            = 1.85

BRACKETS = (
    ("relecture",   0,    60),
    ("lissage",    60,   300),
    ("affinage",  300,   600),
    # Cap 1200s = WhispUI runtime cap, mais ne s'applique pas au bench :
    # on accepte tous les samples ≥600s pour avoir un corpus utile (ctx 16K
    # largement suffisant pour les blocks 28-46 min de exemples-prompts).
    ("arrangement", 600, 99999),
)


def bracket_for(duration_s: float) -> str | None:
    for slug, lo, hi in BRACKETS:
        if lo < duration_s <= hi:
            return slug
    if duration_s <= 0:
        return None
    if duration_s <= BRACKETS[0][2]:  # ≤60 inclusive (relecture)
        return "relecture"
    return None  # past 1200 s ceiling


def envelope_from_block(block: str, source_label: str, idx: int) -> dict:
    words      = len(block.split())
    duration   = round(words / WPS, 4)
    ts = (
        datetime.datetime(2026, 4, 25, 22, 0, 0)
        + datetime.timedelta(minutes=10 * idx)
    ).astimezone().isoformat(timespec="microseconds")
    return {
        "timestamp": ts,
        "kind":      "Corpus",
        "session":   f"exemples-{source_label}-{idx:02d}",
        "payload": {
            "profile":          "Reconstructed",
            "profile_id":       f"exemples-{source_label}",
            "slug":             "TBD",
            "duration_seconds": duration,
            "whisper": {
                "model":          "ggml-large-v3.bin",
                "language":       "fr",
                "elapsed_ms":     0,
                "initial_prompt": "(historic — duration estimated 1.85 wps)",
            },
            "raw": {
                "text":       block,
                "word_count": words,
                "char_count": len(block),
            },
            "metrics": {"words_per_second": WPS},
            "audio_file":           None,
            "exemples_source":      source_label,
            "exemples_block_index": idx,
        },
    }


def collect_blocks_from_exemples() -> list[dict]:
    samples = []
    sources = (
        ("nettoyage",        "exemples-transcripts--nettoyage.txt"),
        ("prompts",          "exemples-transcripts--prompts.txt"),
        ("restructuration",  "exemples-transcripts--restructuration.txt"),
    )
    for label, fname in sources:
        path = EXEMPLES_DIR / fname
        if not path.exists():
            continue
        text   = path.read_text(encoding="utf-8")
        blocks = [b.strip() for b in re.split(r"\n---+\n", text) if b.strip()]
        for i, block in enumerate(blocks, start=1):
            words = len(block.split())
            if words < 50:           # too short to be a meaningful sample
                continue
            samples.append(envelope_from_block(block, label, i))
    return samples


def collect_refreshed_samples() -> list[dict]:
    samples = []
    if not REFRESHED_PATH.exists():
        return samples
    with REFRESHED_PATH.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line: continue
            env = json.loads(line)
            if env.get("kind", "").lower() != "corpus": continue
            samples.append(env)
    return samples


def main() -> None:
    # -- 1. Cleanup obsolete folders ---------------------------------
    for d in TELEMETRY_DIR.iterdir() if TELEMETRY_DIR.exists() else []:
        if not d.is_dir(): continue
        if d.name.startswith("exemples-") or d.name.startswith("restructuration-composite-") or d.name.startswith("corpus-"):
            print(f"  cleanup: removing {d}")
            shutil.rmtree(d)

    # -- 2. Collect samples ------------------------------------------
    refreshed_samples = collect_refreshed_samples()
    exemples_samples  = collect_blocks_from_exemples()
    all_samples       = refreshed_samples + exemples_samples
    print(f"\n  refreshed samples  : {len(refreshed_samples)}")
    print(f"  exemples samples   : {len(exemples_samples)}")
    print(f"  total samples      : {len(all_samples)}")

    # -- 3. Bucket per bracket and emit -----------------------------
    bucketed: dict[str, list[dict]] = {slug: [] for slug, _, _ in BRACKETS}
    dropped = 0
    for env in all_samples:
        d = env["payload"]["duration_seconds"]
        slug = bracket_for(d)
        if slug is None:
            dropped += 1
            continue
        env_copy = json.loads(json.dumps(env))
        env_copy["payload"]["slug"]       = f"corpus-{slug}"
        env_copy["payload"]["profile_id"] = f"corpus-{slug}"
        env_copy["payload"]["profile"]    = slug.capitalize()
        bucketed[slug].append(env_copy)

    if dropped:
        print(f"  dropped (>1200s)   : {dropped}")

    # -- 4. Write bucketed corpora ----------------------------------
    print("\n  Bucket  | n  | duration range          | source mix")
    print("  --------|----|-------------------------|----------------------")
    for slug, _, _ in BRACKETS:
        samples = bucketed[slug]
        out_dir = TELEMETRY_DIR / f"corpus-{slug}"
        out_dir.mkdir(parents=True, exist_ok=True)
        out_path = out_dir / "corpus.jsonl"
        with out_path.open("w", encoding="utf-8") as f:
            for env in samples:
                f.write(json.dumps(env, ensure_ascii=False) + "\n")
        if not samples:
            print(f"  {slug:11s} | 0  | (vide)")
            continue
        durs = sorted(s["payload"]["duration_seconds"] for s in samples)
        sources = {}
        for s in samples:
            src = s["payload"].get("exemples_source") or "refreshed"
            sources[src] = sources.get(src, 0) + 1
        src_str = ", ".join(f"{k}:{v}" for k, v in sorted(sources.items()))
        print(f"  {slug:11s} | {len(samples):2d} | {durs[0]:6.1f}-{durs[-1]:6.1f}s ({durs[len(durs)//2]:.0f}s med) | {src_str}")

    print("\n  → telemetry/corpus-{relecture,lissage,affinage,arrangement}/corpus.jsonl")


if __name__ == "__main__":
    main()
