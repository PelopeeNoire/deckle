"""
Autoresearch — boucle autonome d'optimisation du prompt de nettoyage.

Utilise le 14B local comme "cerveau" pour proposer des variantes de prompt,
benchmark.py pour scorer, et garde/jette automatiquement.

Un seul lancement, aucune intervention humaine.

Usage :
    python autoresearch.py [--max-experiments 10] [--runs-per-experiment 3]
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request

BENCHMARK_DIR = os.path.dirname(os.path.abspath(__file__))
PROMPT_FILE = os.path.join(BENCHMARK_DIR, "system_prompt.txt")
CORPUS_FILE = os.path.join(BENCHMARK_DIR, "corpus.json")
RESULTS_FILE = os.path.join(BENCHMARK_DIR, "results.tsv")
REPORT_FILE = os.path.join(BENCHMARK_DIR, "autoresearch_report.txt")

OLLAMA_ENDPOINT = "http://localhost:11434/api/generate"
DESIGNER_MODEL = "ministral-3:14b"  # le 14B génère les variantes de prompt

# ─── Appel Ollama (pour le designer) ──────────────────────────────────────────

def call_ollama_raw(system: str, user: str, model: str, temperature: float = 0.7) -> str:
    """Appelle Ollama en format Mistral, retourne le texte brut."""
    merged = f"{system}\n\n{user}" if system.strip() else user
    prompt = f"[INST] {merged} [/INST]"

    body = json.dumps({
        "model": model,
        "prompt": prompt,
        "raw": True,
        "stream": False,
        "keep_alive": "10m",
        "options": {
            "temperature": temperature,
            "num_ctx": 8192,
            "stop": ["[INST]", "[/INST]", "</s>"]
        }
    }).encode("utf-8")

    req = urllib.request.Request(
        OLLAMA_ENDPOINT,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read().decode("utf-8"))

    text = data.get("response", "")
    for stop in ["[INST]", "[/INST]", "</s>", "<s>"]:
        text = text.replace(stop, "")
    return text.strip()


# ─── Lancer le benchmark ─────────────────────────────────────────────────────

def run_benchmark(runs: int = 1) -> tuple[float, str]:
    """
    Lance benchmark.py N fois et retourne (score_moyen, détails).
    Plusieurs runs pour réduire la variance du 3B.
    """
    scores = []
    all_output = []

    for i in range(runs):
        result = subprocess.run(
            [sys.executable, "benchmark.py"],
            cwd=BENCHMARK_DIR,
            capture_output=True,
            text=True,
            timeout=600
        )
        output = result.stdout + result.stderr
        all_output.append(f"--- Run {i+1}/{runs} ---\n{output}")

        match = re.search(r"SCORE=(\d+\.\d+)", output)
        if match:
            scores.append(float(match.group(1)))
        else:
            scores.append(1.0)  # échec = pire score

    avg = sum(scores) / len(scores)
    detail = "\n".join(all_output)
    return round(avg, 4), detail


# ─── Générer une variante de prompt ──────────────────────────────────────────

DESIGNER_SYSTEM = """Tu es un expert en prompt engineering pour petits modèles de langue (3B paramètres).

Tu dois proposer une VARIANTE AMÉLIORÉE d'un system prompt qui sert à nettoyer minimalement des transcriptions vocales françaises.

Le modèle cible est un Ministral 3B instruct. Il a tendance à :
- Halluciner des mots ou concepts absents de l'entrée
- Restructurer le texte en paragraphes alors qu'on veut garder l'ordre original
- Changer le registre oral en style écrit formel
- Parfois ajouter un préambule ("Voici", "Bien sûr")
- Parfois utiliser du markdown (bullets, bold)

CONTRAINTES pour le prompt que tu proposes :
- En français
- Maximum 500 mots
- Doit produire du texte brut en sortie (pas de markdown)
- Le prompt doit être COMPLET et autonome (pas un diff, pas un patch)

IMPORTANT : Réponds UNIQUEMENT avec le nouveau prompt, rien d'autre. Pas d'explication, pas de commentaire, pas de "Voici le prompt amélioré". Juste le texte du prompt directement."""


def generate_variant(current_prompt: str, experiment_num: int,
                     history: list[dict]) -> str:
    """Demande au 14B une variante du prompt basée sur l'historique."""

    # Construire le contexte historique
    history_text = ""
    if history:
        history_text = "\n\nHISTORIQUE DES EXPÉRIENCES :\n"
        for h in history[-5:]:  # 5 dernières
            history_text += (
                f"- Exp {h['num']}: score={h['score']} ({h['status']}) — {h['description']}\n"
            )

    # Stratégies à suggérer selon la progression
    strategies = [
        "Essaie de reformuler les contraintes de manière plus courte et impérative, comme des règles numérotées.",
        "Essaie d'ajouter un exemple court (few-shot) montrant une entrée et sa sortie attendue.",
        "Essaie de mettre les interdictions EN PREMIER, avant les instructions positives.",
        "Essaie de réduire le prompt au strict minimum — moins de mots, plus direct.",
        "Essaie d'ajouter un rôle très concret ('Tu es un correcteur de dictée') et de simplifier le reste.",
        "Essaie de reformuler en utilisant 'INTERDIT :' suivi d'une liste, puis 'AUTORISÉ :' suivi d'une liste courte.",
        "Essaie d'ajouter deux exemples courts : un bon et un mauvais (avec 'NE FAIS PAS ça' / 'FAIS ça').",
        "Essaie de combiner les meilleures idées des expériences précédentes.",
        "Essaie une approche radicalement différente : prompt très court (3-4 lignes max).",
        "Essaie d'être extrêmement répétitif sur la contrainte la plus violée (pas d'invention).",
    ]
    strategy = strategies[min(experiment_num - 1, len(strategies) - 1)]

    user_msg = f"""PROMPT ACTUEL (score={history[-1]['score'] if history else 'baseline'}) :
---
{current_prompt}
---

STRATÉGIE À EXPLORER : {strategy}
{history_text}
Propose une variante améliorée. Réponds UNIQUEMENT avec le texte du nouveau prompt."""

    return call_ollama_raw(DESIGNER_SYSTEM, user_msg, DESIGNER_MODEL, temperature=0.7)


# ─── Git helpers ──────────────────────────────────────────────────────────────

def git_commit(message: str) -> str:
    """Commit et retourne le hash court."""
    subprocess.run(["git", "add", PROMPT_FILE], cwd=BENCHMARK_DIR, check=True,
                   capture_output=True)
    subprocess.run(["git", "commit", "-m", message], cwd=BENCHMARK_DIR, check=True,
                   capture_output=True)
    result = subprocess.run(["git", "rev-parse", "--short", "HEAD"],
                           cwd=BENCHMARK_DIR, capture_output=True, text=True, check=True)
    return result.stdout.strip()


def git_revert():
    """Revert le dernier commit."""
    subprocess.run(["git", "reset", "--hard", "HEAD~1"], cwd=BENCHMARK_DIR,
                   check=True, capture_output=True)


# ─── Boucle principale ───────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Autoresearch — prompt optimization loop")
    parser.add_argument("--max-experiments", type=int, default=10)
    parser.add_argument("--runs-per-experiment", type=int, default=3,
                        help="Nombre de runs par expérience (réduit la variance)")
    args = parser.parse_args()

    print("=" * 70)
    print("AUTORESEARCH — Optimisation du prompt Nettoyage")
    print(f"Max expériences: {args.max_experiments}")
    print(f"Runs par expérience: {args.runs_per_experiment}")
    print("=" * 70)

    # Lire le prompt initial
    with open(PROMPT_FILE, "r", encoding="utf-8") as f:
        current_prompt = f.read().strip()

    # Baseline (moyenne de N runs pour stabilité)
    print("\n[BASELINE] Mesure du score de référence...")
    best_score, baseline_detail = run_benchmark(args.runs_per_experiment)
    print(f"[BASELINE] Score = {best_score}")

    best_prompt = current_prompt
    history = [{"num": 0, "score": best_score, "status": "baseline",
                "description": "prompt original"}]

    # Logger le baseline
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
        print(f"\n{'='*60}")
        print(f"[EXP {exp_num}/{args.max_experiments}]")

        # 1. Générer une variante
        print("  Génération de variante via 14B...", end=" ", flush=True)
        try:
            new_prompt = generate_variant(best_prompt, exp_num, history)
        except Exception as e:
            print(f"ERREUR: {e}")
            history.append({"num": exp_num, "score": best_score,
                           "status": "crash", "description": f"designer error: {e}"})
            continue

        # Validation basique
        word_count = len(new_prompt.split())
        if word_count > 500:
            print(f"TROP LONG ({word_count} mots), tronqué")
            new_prompt = " ".join(new_prompt.split()[:500])
        if word_count < 10:
            print(f"TROP COURT ({word_count} mots), skip")
            history.append({"num": exp_num, "score": best_score,
                           "status": "crash", "description": "prompt trop court"})
            continue

        print(f"OK ({word_count} mots)")
        print(f"  Aperçu: {new_prompt[:120]}...")

        # 2. Écrire le nouveau prompt
        with open(PROMPT_FILE, "w", encoding="utf-8") as f:
            f.write(new_prompt)

        # 3. Commit
        try:
            commit_hash = git_commit(
                f"experiment {exp_num}: {new_prompt[:60].replace(chr(10), ' ')}"
            )
        except Exception as e:
            print(f"  Git commit erreur: {e}")
            # Restaurer le meilleur prompt
            with open(PROMPT_FILE, "w", encoding="utf-8") as f:
                f.write(best_prompt)
            continue

        # 4. Benchmark
        print(f"  Benchmark ({args.runs_per_experiment} runs)...", flush=True)
        try:
            new_score, detail = run_benchmark(args.runs_per_experiment)
        except Exception as e:
            print(f"  Benchmark crash: {e}")
            git_revert()
            with open(PROMPT_FILE, "w", encoding="utf-8") as f:
                f.write(best_prompt)
            history.append({"num": exp_num, "score": 1.0,
                           "status": "crash", "description": str(e)})
            with open(RESULTS_FILE, "a", encoding="utf-8") as f:
                f.write(f"{exp_num}\t{commit_hash}\t1.0000\tcrash\t{str(e)[:80]}\n")
            continue

        # 5. Décision
        improved = new_score < best_score
        status = "keep" if improved else "discard"
        delta = best_score - new_score
        desc = f"score={new_score} (delta={delta:+.4f})"

        if improved:
            print(f"  ✓ AMÉLIORÉ: {best_score} → {new_score} (delta={delta:+.4f})")
            best_score = new_score
            best_prompt = new_prompt
        else:
            print(f"  ✗ PAS MIEUX: {new_score} >= {best_score} — revert")
            git_revert()
            with open(PROMPT_FILE, "w", encoding="utf-8") as f:
                f.write(best_prompt)

        history.append({"num": exp_num, "score": new_score,
                       "status": status, "description": desc})

        with open(RESULTS_FILE, "a", encoding="utf-8") as f:
            f.write(f"{exp_num}\t{commit_hash}\t{new_score}\t{status}\t{desc}\n")

        report_lines.extend([
            f"\n[EXP {exp_num}] {status.upper()} — score={new_score} (best={best_score})",
            f"  Prompt ({len(new_prompt)} chars):",
            f"  {new_prompt[:200]}...",
        ])

    # ─── Rapport final ────────────────────────────────────────────────────

    print("\n" + "=" * 70)
    print("AUTORESEARCH TERMINÉ")
    print("=" * 70)

    kept = [h for h in history if h["status"] == "keep"]
    discarded = [h for h in history if h["status"] == "discard"]
    crashed = [h for h in history if h["status"] == "crash"]

    baseline_score = history[0]["score"]
    improvement = ((baseline_score - best_score) / baseline_score * 100) if baseline_score > 0 else 0

    print(f"\nTotal expériences: {len(history) - 1}")
    print(f"  Gardées:   {len(kept)}")
    print(f"  Jetées:    {len(discarded)}")
    print(f"  Crashées:  {len(crashed)}")
    print(f"\nScore baseline: {baseline_score}")
    print(f"Score final:    {best_score}")
    print(f"Amélioration:   {improvement:.1f}%")

    print(f"\nMeilleur prompt ({len(best_prompt)} chars):")
    print("-" * 40)
    print(best_prompt)
    print("-" * 40)

    print(f"\nHistorique complet:")
    for h in history:
        marker = "→" if h["status"] == "keep" else "×" if h["status"] == "discard" else "!"
        print(f"  {marker} Exp {h['num']}: {h['score']:.4f} ({h['status']}) — {h['description']}")

    # Sauvegarder le rapport
    report_lines.extend([
        "",
        "=" * 60,
        "RÉSUMÉ FINAL",
        f"Baseline: {baseline_score}",
        f"Final:    {best_score}",
        f"Amélioration: {improvement:.1f}%",
        f"Gardées/Jetées/Crashées: {len(kept)}/{len(discarded)}/{len(crashed)}",
        "",
        "MEILLEUR PROMPT:",
        best_prompt,
    ])

    with open(REPORT_FILE, "w", encoding="utf-8") as f:
        f.write("\n".join(report_lines))

    print(f"\nRapport sauvegardé dans {REPORT_FILE}")
    print(f"Résultats TSV dans {RESULTS_FILE}")


if __name__ == "__main__":
    main()
