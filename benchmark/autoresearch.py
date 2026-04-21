"""Autoresearch loop — optimize the rewrite system prompt iteratively.

Uses an Ollama-hosted designer model to propose variants of the system
prompt, runs ``benchmark.py`` to score each variant, and keeps the
best one. Each experiment is git-committed so the history can be
replayed or reverted cleanly.

Usage:
    python autoresearch.py [--max-experiments N] [--runs-per-experiment N]

Paths (all relative to ``benchmark/``):
    config/prompts/system_prompt.txt           — the prompt being optimized.
    telemetry/reports/results.tsv              — one row per experiment.
    telemetry/reports/autoresearch_report.txt  — human-readable narrative.
    logs/autoresearch-YYYYMMDD-HHMMSS.log      — live step log (if logging hook).
"""

from __future__ import annotations

import argparse
import configparser
import io
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

from lib import ollama as ollama_client                 # noqa: E402

CONFIG_FILE   = BENCHMARK_DIR / "config" / "config.ini"
PROMPT_FILE   = BENCHMARK_DIR / "config" / "prompts" / "system_prompt.txt"
REPORTS_DIR   = BENCHMARK_DIR / "telemetry" / "reports"
REPORT_FILE   = REPORTS_DIR / "autoresearch_report.txt"
RESULTS_FILE  = REPORTS_DIR / "results.tsv"
LAST_REPORT   = REPORTS_DIR / "last_report.json"


def load_config() -> configparser.ConfigParser:
    cfg = configparser.ConfigParser()
    cfg["benchmark"] = {
        "profile":       "restructuration",
        "model":         "ministral-3:14b",
        "temperature":   "0.3",
        "num_ctx_k":     "32",
        "endpoint":      "http://localhost:11434/api/generate",
        "judge_backend": "claude",
    }
    cfg["autoresearch"] = {
        "designer_model":      "ministral-3:14b",
        "max_experiments":     "10",
        "runs_per_experiment": "3",
    }
    if CONFIG_FILE.exists():
        cfg.read(CONFIG_FILE, encoding="utf-8")
    return cfg


# ── Logging ────────────────────────────────────────────────────────────────

def _log(level: str, msg: str) -> None:
    ts = time.strftime("%H:%M:%S")
    print(ollama_client.sanitize(f"[{ts}] [{level}] {msg}"), flush=True)


def log_info(msg: str)  -> None: _log("INFO",  msg)
def log_warn(msg: str)  -> None: _log("WARN",  msg)
def log_error(msg: str) -> None: _log("ERROR", msg)


def log_sep() -> None:
    print(f"\n{'='*70}", flush=True)


# ── Preflight ──────────────────────────────────────────────────────────────

def preflight(endpoint: str) -> bool:
    log_sep()
    log_info("PREFLIGHT")
    log_sep()

    models = ollama_client.check_endpoint(endpoint)
    if not models:
        log_error("Ollama unreachable — start the daemon, then retry.")
        return False
    log_info(f"Ollama OK — {len(models)} models loaded")

    if not PROMPT_FILE.exists():
        log_error(f"Missing prompt file: {PROMPT_FILE}")
        log_error("Seed it manually before running autoresearch.")
        return False
    if PROMPT_FILE.stat().st_size == 0:
        log_error(f"Empty prompt file: {PROMPT_FILE}")
        return False
    log_info(f"  system_prompt.txt: {PROMPT_FILE.stat().st_size} bytes")

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    log_info("PREFLIGHT OK")
    return True


# ── benchmark.py driver ───────────────────────────────────────────────────

def run_benchmark(runs: int) -> tuple[float, str]:
    """Run benchmark.py ``runs`` times and return (avg score, combined log)."""
    scores: list[float] = []
    combined:  list[str] = []

    for i in range(runs):
        log_info(f"  benchmark run {i + 1}/{runs}")
        t0 = time.time()
        result = subprocess.run(
            [sys.executable, str(BENCHMARK_DIR / "benchmark.py")],
            cwd            = BENCHMARK_DIR,
            capture_output = True,
            text           = True,
            timeout        = 1800,
        )
        elapsed = time.time() - t0
        output  = (result.stdout or "") + (result.stderr or "")
        combined.append(f"--- run {i + 1}/{runs} ---\n{output}")

        match = re.search(r"SCORE=(\d+\.\d+)", output)
        if match:
            score = float(match.group(1))
            scores.append(score)
            log_info(f"  SCORE={score:.4f} ({elapsed:.0f}s)")
        else:
            scores.append(1.0)
            log_error(f"  run {i + 1} returned no SCORE ({elapsed:.0f}s)")
            for line in output.strip().splitlines()[-5:]:
                log_error(f"    {line}")

    avg    = sum(scores) / len(scores)
    spread = max(scores) - min(scores) if len(scores) > 1 else 0.0
    log_info(f"  avg={avg:.4f} spread={spread:.4f} scores={[round(s, 4) for s in scores]}")
    return round(avg, 4), "\n".join(combined)


# ── Designer ───────────────────────────────────────────────────────────────

DESIGNER_SYSTEM = """Tu es un expert en prompt engineering pour modèles instruct 14B.

Tu proposes une VARIANTE AMÉLIORÉE d'un system prompt utilisé pour RESTRUCTURER des transcriptions vocales françaises longues en texte écrit clair, sans rien perdre du fond.

Contraintes de la tâche :
- Transformer le discours oral décousu en texte écrit structuré par paragraphes.
- Conserver TOUTES les idées principales et les nuances (alternatives rejetées, auto-corrections, justifications, exemples).
- Ne pas inventer de contenu absent de l'entrée.
- Préserver le registre, le vocabulaire et le niveau d'abstraction du locuteur.
- Densité préservée : sortie d'au moins 70 % de la longueur de l'entrée, sauf redondance manifeste.

Critères évalués par le juge (juge Claude Sonnet ou Ministral 14B, même grille) :
1. Complétude macro (25 %)
2. Préservation des nuances (25 %)
3. Densité préservée (15 %)
4. Non-invention (15 %)
5. Structure thématique (10 %)
6. Clarté et fidélité du registre (10 %)

Contraintes du prompt que tu proposes :
- En français, texte brut (pas de markdown, pas de listes).
- Complet et autonome (pas un diff).
- MAXIMUM 200 MOTS, plus court si possible.
- La COMPLÉTUDE des idées et la PRÉSERVATION DES NUANCES dominent : mieux vaut un peu plus long que perdre un détail.

IMPORTANT : Réponds UNIQUEMENT avec le nouveau prompt. Rien avant, rien après."""


def load_last_details() -> list[dict]:
    if not LAST_REPORT.exists():
        return []
    try:
        return json.loads(LAST_REPORT.read_text(encoding="utf-8")).get("details", [])
    except Exception:
        return []


def format_sample_feedback(details: list[dict]) -> str:
    if not details:
        return ""
    lines = ["\nDIAGNOSTIC PAR SAMPLE (dernier run) :"]
    for d in details:
        composite = d.get("judge_composite")
        status    = "N/A" if composite is None else (
            "OK" if composite < 0.25 else "MOYEN" if composite < 0.50 else "MAUVAIS"
        )
        rule = d.get("rule", {})
        issues: list[str] = []
        if rule.get("novel_words", 0) > 0.05:
            issues.append(f"mots inventés={rule['novel_words']:.0%}")
        lr = d.get("length_ratio", 0)
        if lr < 0.7:
            issues.append(f"compression x{lr:.2f}")
        elif lr > 1.1:
            issues.append(f"expansion x{lr:.2f}")
        if rule.get("preamble", 0) > 0:
            issues.append("préambule")
        if rule.get("lists", 0) > 0:
            issues.append("listes")
        if d.get("catastrophe"):
            issues.append("CATASTROPHE")

        scores = d.get("judge_scores") or {}
        worst  = ""
        if scores:
            low = min(scores.items(), key=lambda kv: kv[1])
            worst = f" worst={low[0]}={low[1]}"

        issue_str = ", ".join(issues) if issues else "aucun problème"
        score_str = f"{composite:.3f}" if composite is not None else "—"
        lines.append(
            f"  {d['id']} ({d.get('input_chars', 0)} chars): {status} "
            f"({score_str}){worst} — {issue_str}"
        )
    return "\n".join(lines)


STRATEGIES = (
    "Insiste sur la préservation des nuances : alternatives rejetées, auto-corrections, justifications, exemples doivent rester.",
    "Ajoute UN court exemple few-shot (3 phrases orales → 2 phrases écrites, zéro idée perdue).",
    "Formule comme un rédacteur rigoureux : 'Tu es un rédacteur qui transforme des notes orales en texte structuré SANS rien ajouter ni retirer.'",
    "Sépare les étapes : 1) identifier toutes les idées, 2) regrouper par thème, 3) rédiger.",
    "Insiste sur la densité : le texte de sortie doit garder au moins 70 % de la longueur de l'entrée, sauf redondance orale flagrante.",
    "Essaie un prompt ultra-direct : 3-5 phrases impératives, sans rôle, sans explication.",
    "Mets la non-invention en PREMIÈRE phrase : 'N'ajoute rien qui ne soit dans le texte. Pas d\"'en résumé\"'.",
    "Rappelle l'organisation thématique : regrouper les idées par sujet plutôt que suivre l'ordre oral.",
    "Approche 'prise de notes orales → compte-rendu écrit complet', avec rappel explicite que chaque détail compte.",
    "Combine les deux meilleures approches observées dans l'historique avec un rappel anti-préambule.",
)


def generate_variant(current_prompt: str, experiment_num: int,
                     history: list[dict], designer_model: str, endpoint: str) -> str:
    history_block = ""
    if history:
        history_block = "\nHISTORIQUE :\n" + "\n".join(
            f"- Exp {h['num']}: score={h['score']} ({h['status']}) — {h['description']}"
            for h in history[-5:]
        )

    sample_feedback = format_sample_feedback(load_last_details())
    strategy        = STRATEGIES[min(experiment_num - 1, len(STRATEGIES) - 1)]

    user_msg = (
        f"PROMPT ACTUEL (score={history[-1]['score'] if history else 'baseline'}):\n"
        f"---\n{current_prompt}\n---\n\n"
        f"STRATÉGIE À EXPLORER : {strategy}\n"
        f"{history_block}{sample_feedback}\n\n"
        f"Rappel : MAXIMUM 200 mots. Propose une variante améliorée. "
        f"Réponds UNIQUEMENT avec le texte du prompt."
    )
    return ollama_client.call_ollama_text(
        system      = DESIGNER_SYSTEM,
        user        = user_msg,
        model       = designer_model,
        temperature = 0.7,
        endpoint    = endpoint,
    )


# ── Git helpers ────────────────────────────────────────────────────────────

def _git(*args: str) -> str:
    result = subprocess.run(
        ["git", *args],
        cwd            = BENCHMARK_DIR,
        capture_output = True,
        text           = True,
        check          = True,
    )
    return result.stdout.strip()


def git_commit(message: str) -> str:
    _git("add", str(PROMPT_FILE))
    _git("commit", "-m", message)
    return _git("rev-parse", "--short", "HEAD")


def git_revert() -> None:
    _git("reset", "--hard", "HEAD~1")


# ── Main ───────────────────────────────────────────────────────────────────

def main() -> None:
    full_cfg = load_config()
    bm_cfg   = full_cfg["benchmark"]
    ar_cfg   = full_cfg["autoresearch"]

    parser = argparse.ArgumentParser(description="Autoresearch — rewrite-prompt optimization loop")
    parser.add_argument("--max-experiments",     type=int,
                        default=ar_cfg.getint("max_experiments"))
    parser.add_argument("--runs-per-experiment", type=int,
                        default=ar_cfg.getint("runs_per_experiment"))
    args = parser.parse_args()

    start_time = time.time()

    log_sep()
    log_info(f"AUTORESEARCH — profile: {bm_cfg['profile']}")
    log_info(f"Target: {bm_cfg['model']} | Designer: {ar_cfg['designer_model']} | Judge: {bm_cfg['judge_backend']}")
    log_info(f"Max experiments: {args.max_experiments} | runs/exp: {args.runs_per_experiment}")
    log_sep()

    if not preflight(bm_cfg["endpoint"]):
        sys.exit(1)

    current_prompt = PROMPT_FILE.read_text(encoding="utf-8").strip()
    log_info(f"Initial prompt: {len(current_prompt)} chars, {len(current_prompt.split())} words")

    # Baseline
    log_sep()
    log_info("BASELINE")
    log_sep()
    best_score, _ = run_benchmark(args.runs_per_experiment)
    log_info(f"BASELINE = {best_score}")

    best_prompt = current_prompt
    history = [{"num": 0, "score": best_score, "status": "baseline",
                "description": "prompt original"}]

    with open(RESULTS_FILE, "a", encoding="utf-8") as f:
        f.write(f"0\tbaseline\t{best_score}\tbaseline\tprompt original ({args.runs_per_experiment} runs)\n")

    report_lines = [
        f"Autoresearch started at {time.strftime('%Y-%m-%d %H:%M:%S')}",
        f"Baseline score: {best_score} ({args.runs_per_experiment} runs avg)",
        f"Baseline prompt ({len(current_prompt)} chars):",
        current_prompt,
        "",
        "=" * 60,
    ]

    for exp_num in range(1, args.max_experiments + 1):
        log_sep()
        elapsed_min = (time.time() - start_time) / 60
        log_info(f"EXPÉRIENCE {exp_num}/{args.max_experiments} (total: {elapsed_min:.0f}min)")
        log_info(f"Best so far: {best_score}")
        log_sep()

        if not ollama_client.check_endpoint(bm_cfg["endpoint"]):
            log_error("Ollama dropped — aborting")
            break

        log_info(f"Generating variant ({ar_cfg['designer_model']})...")
        try:
            variant = generate_variant(
                current_prompt = current_prompt,
                experiment_num = exp_num,
                history        = history,
                designer_model = ar_cfg["designer_model"],
                endpoint       = bm_cfg["endpoint"],
            )
        except Exception as e:
            log_error(f"Designer failed: {e}")
            continue

        words = len(variant.split())
        log_info(f"Variant: {len(variant)} chars, {words} words")
        if words > 250:
            log_warn("Variant too long — skipping")
            continue
        if not variant.strip():
            log_warn("Empty variant — skipping")
            continue

        PROMPT_FILE.write_text(variant, encoding="utf-8")

        try:
            score, _ = run_benchmark(args.runs_per_experiment)
        except Exception as e:
            log_error(f"Benchmark failed: {e}")
            PROMPT_FILE.write_text(best_prompt, encoding="utf-8")
            continue

        description = f"exp {exp_num} ({words}w)"
        if score < best_score:
            commit = git_commit(f"benchmark(exp {exp_num}): {score} < {best_score}")
            best_score  = score
            best_prompt = variant
            status      = f"KEEP (commit {commit})"
            log_info(f"  IMPROVEMENT {score} < prev — kept")
        else:
            status = "REJECT"
            PROMPT_FILE.write_text(best_prompt, encoding="utf-8")
            log_info(f"  no improvement ({score} ≥ {best_score}) — reverted")

        history.append({"num": exp_num, "score": score, "status": status,
                        "description": description})
        with open(RESULTS_FILE, "a", encoding="utf-8") as f:
            f.write(f"{exp_num}\t{variant[:40].replace(chr(10), ' ')}…\t{score}\t{status}\t{description}\n")

        report_lines.extend([
            f"\nExperiment {exp_num} — score={score} ({status})",
            f"Prompt ({len(variant)} chars):",
            variant,
            "=" * 60,
        ])

    # Final restore + report
    PROMPT_FILE.write_text(best_prompt, encoding="utf-8")
    REPORT_FILE.write_text("\n".join(report_lines), encoding="utf-8")

    total_min = (time.time() - start_time) / 60
    log_sep()
    log_info(f"DONE — best={best_score} in {total_min:.0f}min")
    log_sep()


if __name__ == "__main__":
    main()
