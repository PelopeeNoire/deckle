"""Benchmark runner for WhispUI rewrite prompts.

Reads Louis' dictation corpus (produced by WhispUI under
``benchmark/telemetry/*/corpus.jsonl``), asks the target Ollama model to
rewrite each raw transcription with the candidate system prompt, then
scores the output with the chosen judge (Claude Sonnet by default,
Ollama Ministral as a fully-local fallback).

Usage:
    python benchmark.py [--model MODEL] [--temperature T] [--num-ctx-k N]
                        [--prompt-file FILE] [--corpus-glob GLOB]
                        [--judge {claude,ollama}] [--skip-judge]
                        [--slug SLUG] [--duration-min SECONDS]
                        [--duration-max SECONDS] [--verbose]

Emits a single ``SCORE=X.XXXX`` line on stdout so ``autoresearch.py``
can parse it. ``SCORE`` is the median judge composite across samples
(0.0 = perfect, 1.0 = terrible). With ``--skip-judge`` the score falls
back to a rule-based median built from the cheap pre-filters.
"""

from __future__ import annotations

import argparse
import configparser
import io
import os
import json
import statistics
import sys
import time
from pathlib import Path

# Force UTF-8 stdout/stderr on Windows so accented output survives
# terminal redirection (default cp1252 breaks autoresearch parsing).
if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

# Make ``lib/`` importable regardless of cwd.
BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

from lib import metrics, ollama as ollama_client              # noqa: E402
from lib.corpus import BRACKET_SLUGS, Sample, load_corpus     # noqa: E402
from lib.judge import CRITERIA, Judge, JudgeResult            # noqa: E402


CONFIG_FILE         = BENCHMARK_DIR / "config" / "config.ini"
JUDGE_PROMPT_FILE   = BENCHMARK_DIR / "config" / "prompts" / "judge_system_prompt.txt"
REPORTS_DIR         = BENCHMARK_DIR / "reports"
DEFAULT_CORPUS_GLOB = str(BENCHMARK_DIR / "telemetry" / "*" / "corpus.jsonl")
DEFAULT_PROMPT_FILE = BENCHMARK_DIR / "config" / "prompts" / "system_prompt.txt"


def load_config() -> configparser.ConfigParser:
    cfg = configparser.ConfigParser()
    cfg["benchmark"] = {
        "profile":        "restructuration",
        "model":          "ministral-3:14b",
        "temperature":    "0.3",
        "num_ctx_k":      "32",
        "endpoint":       "http://localhost:11434/api/generate",
        "corpus_glob":    DEFAULT_CORPUS_GLOB,
        "prompt":         str(DEFAULT_PROMPT_FILE),
        "judge_backend":  "claude",
        "judge_model":    "",          # empty = backend default
    }
    if CONFIG_FILE.exists():
        cfg.read(CONFIG_FILE, encoding="utf-8")
    return cfg


def build_judge(backend: str, model_override: str) -> Judge:
    """Instantiate the judge selected by ``judge_backend``."""
    system_prompt = JUDGE_PROMPT_FILE.read_text(encoding="utf-8").strip()

    if backend == "claude":
        from lib.judge_claude import ClaudeJudge, DEFAULT_MODEL
        return ClaudeJudge(
            system_prompt = system_prompt,
            model         = model_override or DEFAULT_MODEL,
        )

    if backend == "ollama":
        from lib.judge_ollama import OllamaJudge
        cfg = load_config()["benchmark"]
        return OllamaJudge(
            system_prompt = system_prompt,
            model         = model_override or "ministral-3:14b",
            endpoint      = cfg["endpoint"],
        )

    raise ValueError(f"Unknown judge backend: {backend!r}")


def rewrite(sample: Sample, system_prompt: str, *, model: str, temperature: float,
            num_ctx: int, endpoint: str) -> tuple[str, float, int]:
    """Run the target model on one sample. Returns (text, elapsed_s, tokens)."""
    t0 = time.time()
    text, m = ollama_client.call_ollama(
        system      = system_prompt,
        user        = sample.raw_text,
        model       = model,
        temperature = temperature,
        num_ctx     = num_ctx,
        endpoint    = endpoint,
    )
    return text, time.time() - t0, m.eval_count


def run(
    *,
    samples:       list[Sample],
    system_prompt: str,
    model:         str,
    temperature:   float,
    num_ctx:       int,
    endpoint:      str,
    judge:         Judge | None,
    verbose:       bool,
) -> dict:
    composites: list[float] = []
    catastrophes            = 0
    details:     list[dict] = []

    print(f"=== Benchmark: {model} | temp={temperature} | ctx={num_ctx // 1024}K ===")
    print(f"=== Prompt: {len(system_prompt)} chars | Corpus: {len(samples)} samples ===")
    if judge is not None:
        print(f"=== Judge: {judge.backend} / {judge.model} ===")
    print()

    for i, sample in enumerate(samples, start=1):
        print(f"[{i}/{len(samples)}] {sample.id} ({len(sample.raw_text)} chars)...", end=" ", flush=True)
        try:
            output, elapsed, tokens = rewrite(
                sample, system_prompt,
                model       = model,
                temperature = temperature,
                num_ctx     = num_ctx,
                endpoint    = endpoint,
            )
        except Exception as e:
            print(f"ERROR: {e}")
            composites.append(1.0)
            details.append({"id": sample.id, "error": str(e)})
            continue
        print(f"OK ({elapsed:.1f}s, {tokens} tokens)")

        rules = metrics.rule_diagnostic(sample.raw_text, output)
        catastrophe = metrics.is_catastrophe(sample.raw_text, output)

        j: JudgeResult | None = None
        if judge is not None and not catastrophe:
            j = judge.score(sample.raw_text, output)
            composites.append(j.composite())
        elif catastrophe:
            catastrophes += 1
            composites.append(1.0)
            print(f"  [CATASTROPHE] novel={rules['novel_words']:.2f} len_ratio={rules['length_ratio']:.2f}")

        details.append({
            "id":             sample.id,
            "duration_sec":   round(sample.duration_seconds, 2),
            "input_chars":    len(sample.raw_text),
            "output_chars":   len(output),
            "length_ratio":   rules["length_ratio"],
            "rule":           rules,
            "catastrophe":    catastrophe,
            "judge_backend":  j.backend if j else None,
            "judge_model":    j.model if j else None,
            "judge_scores":   j.scores if j else None,
            "judge_composite": j.composite() if j else (1.0 if catastrophe else None),
            "output_text":    output,
            "elapsed_sec":    round(elapsed, 1),
        })

        if verbose:
            rules_fmt = (
                f"  rule: novel={rules['novel_words']:.3f} "
                f"len={rules['length_ratio']:.3f} "
                f"preamble={rules['preamble']:.0f} "
                f"lists={rules['lists']:.0f}"
            )
            print(rules_fmt)
            if j:
                scored = " ".join(f"{key[:5]}={j.scores[key]}" for key, _ in CRITERIA)
                print(f"  judge: {scored} → {j.composite():.4f}")
            print(f"  output: {output[:120]}...")
            print()

    median = statistics.median(composites) if composites else 1.0
    mean   = sum(composites) / len(composites) if composites else 1.0

    report = {
        "model":              model,
        "temperature":        temperature,
        "num_ctx_k":          num_ctx // 1024,
        "prompt_chars":       len(system_prompt),
        "samples":            len(samples),
        "catastrophes":       catastrophes,
        "judge_backend":      judge.backend if judge else None,
        "judge_model":        judge.model if judge else None,
        "composite_median":   round(median, 4),
        "composite_mean":     round(mean, 4),
        "details":            details,
    }

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    (REPORTS_DIR / "last_report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    print(f"\n{'='*60}")
    print(f"RÉSULTATS: {len(samples)} samples")
    print(f"  Composite (0.0 = parfait, 1.0 = terrible)")
    print(f"    médiane : {median:.4f}")
    print(f"    moyenne : {mean:.4f}")
    if catastrophes:
        print(f"  {catastrophes} catastrophe(s) — judge skip")
    for d in details:
        tag = " [!]" if d.get("catastrophe") else ""
        if d.get("judge_composite") is not None:
            composite = d["judge_composite"]
            print(f"  {d['id']}: {composite:.4f} (len_ratio={d['length_ratio']:.2f}){tag}")
        else:
            print(f"  {d['id']}: skipped{tag}")
    print(f"{'='*60}")
    print(f"\nSCORE={median:.4f}")
    return report


def main() -> None:
    cfg = load_config()["benchmark"]

    parser = argparse.ArgumentParser(description="WhispUI rewrite prompt benchmark")
    parser.add_argument("--model",         default=cfg["model"])
    parser.add_argument("--temperature",   type=float, default=cfg.getfloat("temperature"))
    parser.add_argument("--num-ctx-k",     type=int,   default=cfg.getint("num_ctx_k"))
    parser.add_argument("--prompt-file",   default=cfg["prompt"])
    parser.add_argument("--corpus-glob",   default=cfg.get("corpus_glob", DEFAULT_CORPUS_GLOB))
    parser.add_argument("--endpoint",      default=cfg["endpoint"])
    parser.add_argument("--judge",         choices=["claude", "ollama"], default=cfg.get("judge_backend", "claude"))
    parser.add_argument("--judge-model",   default=cfg.get("judge_model", "") or "")
    parser.add_argument("--skip-judge",    action="store_true")
    parser.add_argument("--slug",          default=None, help="Filter samples by profile slug")
    parser.add_argument("--bracket",       choices=BRACKET_SLUGS, default=None,
                        help="Filter samples by audio duration bracket")
    parser.add_argument("--duration-min",  type=float, default=None)
    parser.add_argument("--duration-max",  type=float, default=None)
    parser.add_argument("--verbose",       action="store_true")
    args = parser.parse_args()

    prompt_path = Path(args.prompt_file)
    if not prompt_path.is_absolute():
        prompt_path = BENCHMARK_DIR / prompt_path
    if not prompt_path.exists():
        sys.exit(
            f"Prompt file not found: {prompt_path}\n"
            f"Create {prompt_path.name} manually or run autoresearch.py to seed it."
        )
    system_prompt = prompt_path.read_text(encoding="utf-8").strip()

    corpus_glob = args.corpus_glob
    if not os.path.isabs(corpus_glob):
        corpus_glob = str(BENCHMARK_DIR / corpus_glob)

    samples = load_corpus(
        corpus_glob,
        duration_min = args.duration_min,
        duration_max = args.duration_max,
        slug         = args.slug,
        bracket      = args.bracket,
    )
    if not samples:
        sys.exit(f"No corpus samples found at {corpus_glob}")

    judge: Judge | None = None
    if not args.skip_judge:
        judge = build_judge(args.judge, args.judge_model)

    run(
        samples       = samples,
        system_prompt = system_prompt,
        model         = args.model,
        temperature   = args.temperature,
        num_ctx       = args.num_ctx_k * 1024,
        endpoint      = args.endpoint,
        judge         = judge,
        verbose       = args.verbose,
    )


if __name__ == "__main__":
    main()
