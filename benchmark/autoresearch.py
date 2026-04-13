"""
Autoresearch — boucle autonome d'optimisation du prompt de nettoyage.

Utilise le 14B local comme "cerveau" pour proposer des variantes de prompt,
benchmark.py pour scorer, et garde/jette automatiquement.

Un seul lancement, aucune intervention humaine.

Usage :
    python autoresearch.py [--max-experiments 10] [--runs-per-experiment 3]
"""

import argparse
import configparser
import io
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request

# Force UTF-8 stdout/stderr on Windows (sinon CP1252 crash sur les accents en redirection)
if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(BENCHMARK_DIR, "config.ini")

def load_config() -> configparser.ConfigParser:
    """Charge config.ini avec les défauts."""
    cfg = configparser.ConfigParser()
    cfg["benchmark"] = {
        "profile": "nettoyage",
        "model": "ministral:3b-instruct-q8",
        "judge_model": "ministral-3:14b",
        "temperature": "0.15",
        "num_ctx_k": "32",
        "endpoint": "http://localhost:11434/api/generate",
        "corpus": "corpus.json",
        "prompt": "system_prompt.txt",
    }
    cfg["autoresearch"] = {
        "designer_model": "ministral-3:14b",
        "max_experiments": "10",
        "runs_per_experiment": "3",
    }
    if os.path.exists(CONFIG_FILE):
        cfg.read(CONFIG_FILE, encoding="utf-8")
    return cfg

_CFG = load_config()

PROMPT_FILE = os.path.join(BENCHMARK_DIR, _CFG["benchmark"]["prompt"])
CORPUS_FILE = os.path.join(BENCHMARK_DIR, _CFG["benchmark"]["corpus"])
RESULTS_FILE = os.path.join(BENCHMARK_DIR, "results.tsv")
REPORT_FILE = os.path.join(BENCHMARK_DIR, "autoresearch_report.txt")

OLLAMA_ENDPOINT = _CFG["benchmark"]["endpoint"]
DESIGNER_MODEL = _CFG["autoresearch"]["designer_model"]


# ─── Logging ─────────────────────────────────────────────────────────────────

def sanitize(text: str) -> str:
    """Supprime les caractères de contrôle et séquences ANSI d'un texte.
    Empêche un LLM de générer des séquences que le terminal interprète
    comme des entrées clavier ou des commandes d'échappement."""
    # Supprimer les séquences ANSI escape (CSI, OSC, etc.)
    text = re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', text)   # CSI sequences
    text = re.sub(r'\x1b\][^\x07]*\x07', '', text)       # OSC sequences
    text = re.sub(r'\x1b[^[\]0-9;a-zA-Z]', '', text)     # autres ESC
    # Supprimer tous les caractères de contrôle sauf \n \r \t
    text = re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]', '', text)
    return text


def log(level: str, msg: str):
    """Log avec timestamp et niveau. Flush immédiat pour suivi en temps réel."""
    ts = time.strftime("%H:%M:%S")
    print(sanitize(f"[{ts}] [{level}] {msg}"), flush=True)

def log_info(msg: str):
    log("INFO", msg)

def log_warn(msg: str):
    log("WARN", msg)

def log_error(msg: str):
    log("ERROR", msg)

def log_step(exp_num: int, total: int, step: str):
    """Log une étape numérotée dans une expérience."""
    log("INFO", f"[EXP {exp_num}/{total}] {step}")

def log_separator():
    print(f"\n{'='*70}", flush=True)


# ─── Health checks ───────────────────────────────────────────────────────────

def check_ollama() -> bool:
    """Vérifie qu'Ollama répond. Retourne True si OK."""
    try:
        req = urllib.request.Request(
            "http://localhost:11434/api/tags",
            method="GET"
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        models = [m["name"] for m in data.get("models", [])]
        log_info(f"Ollama OK — {len(models)} modèles chargés")
        return True
    except Exception as e:
        log_error(f"Ollama inaccessible : {e}")
        return False


def check_files() -> bool:
    """Vérifie que les fichiers critiques existent et sont lisibles."""
    ok = True
    for path, label in [
        (PROMPT_FILE, "system_prompt.txt"),
        (CORPUS_FILE, "corpus.json"),
    ]:
        if not os.path.exists(path):
            log_error(f"Fichier manquant : {label} ({path})")
            ok = False
        else:
            size = os.path.getsize(path)
            if size == 0:
                log_error(f"Fichier vide : {label}")
                ok = False
            else:
                log_info(f"  {label} : {size} octets")
    return ok


def check_git_clean() -> bool:
    """Vérifie que system_prompt.txt n'a pas de modifications non committées."""
    result = subprocess.run(
        ["git", "diff", "--name-only", "benchmark/system_prompt.txt"],
        cwd=BENCHMARK_DIR, capture_output=True, text=True
    )
    if result.stdout.strip():
        log_warn("system_prompt.txt a des modifications non committées")
        return False
    return True


def preflight_checks() -> bool:
    """Tous les checks avant de commencer. Retourne True si tout est OK."""
    log_separator()
    log_info("PREFLIGHT CHECKS")
    log_separator()

    ok = True

    log_info("1/3 — Ollama")
    if not check_ollama():
        ok = False

    log_info("2/3 — Fichiers")
    if not check_files():
        ok = False

    log_info("3/3 — Git")
    result = subprocess.run(
        ["git", "branch", "--show-current"],
        cwd=BENCHMARK_DIR, capture_output=True, text=True
    )
    branch = result.stdout.strip()
    log_info(f"  Branche : {branch}")
    check_git_clean()

    # Charger et valider le corpus
    try:
        with open(CORPUS_FILE, "r", encoding="utf-8") as f:
            corpus = json.load(f)
        log_info(f"  Corpus : {len(corpus)} samples")
    except Exception as e:
        log_error(f"Corpus invalide : {e}")
        ok = False

    if ok:
        log_info("PREFLIGHT OK — prêt à démarrer")
    else:
        log_error("PREFLIGHT FAILED — corriger les erreurs ci-dessus")

    return ok


# ─── Appel Ollama (pour le designer) ────────────────────────────────────────

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
    # Sanitize : virer les caractères de contrôle/ANSI que le LLM pourrait générer
    return sanitize(text.strip())


# ─── Lancer le benchmark ────────────────────────────────────────────────────

def run_benchmark(runs: int = 1) -> tuple[float, str]:
    """
    Lance benchmark.py N fois et retourne (score_médiane_des_runs, détails).
    Plusieurs runs pour réduire la variance du 3B.
    """
    scores = []
    all_output = []

    for i in range(runs):
        log_info(f"  Benchmark run {i+1}/{runs}...")
        t0 = time.time()

        result = subprocess.run(
            [sys.executable, "benchmark.py"],
            cwd=BENCHMARK_DIR,
            capture_output=True,
            text=True,
            timeout=600
        )

        elapsed = time.time() - t0
        output = result.stdout + result.stderr
        all_output.append(f"--- Run {i+1}/{runs} ---\n{output}")

        if result.returncode != 0:
            log_warn(f"  Run {i+1} exit code {result.returncode}")
            # Afficher les dernières lignes de stderr pour debug
            stderr_lines = result.stderr.strip().split("\n")
            for line in stderr_lines[-3:]:
                log_warn(f"    {line}")

        match = re.search(r"SCORE=(\d+\.\d+)", output)
        if match:
            score = float(match.group(1))
            scores.append(score)
            log_info(f"  Run {i+1} : SCORE={score:.4f} ({elapsed:.0f}s)")
        else:
            scores.append(1.0)  # échec = pire score
            log_error(f"  Run {i+1} : pas de SCORE trouvé dans la sortie ({elapsed:.0f}s)")
            # Afficher les dernières lignes pour debug
            output_lines = output.strip().split("\n")
            for line in output_lines[-5:]:
                log_error(f"    {line}")

    avg = sum(scores) / len(scores)
    spread = max(scores) - min(scores) if len(scores) > 1 else 0
    log_info(f"  Résultat : avg={avg:.4f} (spread={spread:.4f}, scores={[round(s,4) for s in scores]})")

    detail = "\n".join(all_output)
    return round(avg, 4), detail


# ─── Générer une variante de prompt ─────────────────────────────────────────

DESIGNER_SYSTEM = """Tu es un expert en prompt engineering pour petits modèles de langue (3B paramètres).

Tu dois proposer une VARIANTE AMÉLIORÉE d'un system prompt qui sert à nettoyer minimalement des transcriptions vocales françaises.

Le modèle cible est un Ministral 3B instruct. Ses défauts connus :
- Hallucine des mots ou concepts absents de l'entrée (score "novel_words" élevé)
- Tronque brutalement le texte OU explose la longueur (score "length_ratio" élevé)
- Restructure le texte en paragraphes alors qu'on veut garder l'ordre brut
- Change le registre oral en style écrit formel
- Ajoute parfois un préambule ("Voici", "Bien sûr") ou du markdown

LEÇONS DE LA PASSE PRÉCÉDENTE (à respecter impérativement) :
- Les prompts LONGS (>200 mots) ont TOUS empiré le score. Le 3B se perd dans les instructions longues.
- Les prompts avec beaucoup de markdown/bold/listes numérotées confondent le 3B qui reproduit le formatage en sortie.
- Le seul prompt qui a amélioré le score était plus structuré mais restait simple.
- Le problème principal est la TRONCATURE (le 3B coupe le texte) et les HALLUCINATIONS (mots inventés).

CONTRAINTES STRICTES pour le prompt que tu proposes :
- En français
- MAXIMUM 150 MOTS. Un prompt de 50-80 mots bien choisis bat un prompt de 300 mots.
- Texte brut uniquement dans le prompt lui-même : pas de markdown, pas de **, pas de listes numérotées.
- Le prompt doit être COMPLET et autonome (pas un diff, pas un patch)
- Ne pas utiliser de mots inutiles, chaque mot compte pour un 3B.

IMPORTANT : Réponds UNIQUEMENT avec le nouveau prompt, rien d'autre. Pas d'explication, pas de commentaire. Juste le texte du prompt directement."""


def load_last_report() -> list[dict]:
    """Charge les détails par sample du dernier benchmark run."""
    report_path = os.path.join(BENCHMARK_DIR, "last_report.json")
    if not os.path.exists(report_path):
        return []
    try:
        with open(report_path, "r", encoding="utf-8") as f:
            report = json.load(f)
        return report.get("details", [])
    except Exception:
        return []


def format_sample_feedback(details: list[dict]) -> str:
    """Résume les échecs et réussites par sample pour guider le designer."""
    if not details:
        return ""

    lines = ["\nDIAGNOSTIC PAR SAMPLE (dernier run) :"]
    for d in details:
        comp = d["composite"]
        status = "OK" if comp < 0.15 else "MOYEN" if comp < 0.35 else "MAUVAIS"
        issues = []
        if d["rule"]["novel_words"] > 0.05:
            issues.append(f"mots inventés={d['rule']['novel_words']:.0%}")
        if d["rule"]["length_ratio"] > 0.3:
            ratio = d["output_len"] / d["input_len"] if d["input_len"] > 0 else 0
            direction = "tronqué" if ratio < 1 else "rallongé"
            issues.append(f"{direction} x{ratio:.1f}")
        if d["rule"]["preamble"] > 0:
            issues.append("préambule détecté")
        if d["rule"]["markdown"] > 0:
            issues.append("markdown en sortie")
        if d.get("catastrophe"):
            issues.append("CATASTROPHE")

        issue_str = ", ".join(issues) if issues else "aucun problème"
        lines.append(f"  Sample #{d['id']} ({d['input_len']} chars): {status} ({comp:.3f}) — {issue_str}")

    return "\n".join(lines)


def generate_variant(current_prompt: str, experiment_num: int,
                     history: list[dict]) -> str:
    """Demande au 14B une variante du prompt basée sur l'historique et le diagnostic."""

    # Construire le contexte historique
    history_text = ""
    if history:
        history_text = "\nHISTORIQUE DES EXPÉRIENCES :\n"
        for h in history[-5:]:
            history_text += (
                f"- Exp {h['num']}: score={h['score']} ({h['status']}) — {h['description']}\n"
            )

    # Diagnostic par sample du dernier run
    details = load_last_report()
    sample_feedback = format_sample_feedback(details)

    # Stratégies ciblées basées sur les résultats de la passe 1
    strategies = [
        "Réduis le prompt à 3-5 phrases impératives. Pas de rôle, pas d'exemple, juste les règles essentielles.",
        "Ajoute UN seul exemple court (entrée 15 mots, sortie 15 mots) pour ancrer le comportement attendu.",
        "Concentre-toi sur le problème de troncature : insiste pour que le modèle recopie TOUT le texte, mot par mot.",
        "Formule le prompt comme une tâche de copie, pas de correction. 'Recopie ce texte en ajoutant seulement la ponctuation.'",
        "Essaie sans rôle ni métaphore : instructions brutes et directes uniquement.",
        "Mets l'interdiction d'ajouter/supprimer des mots en PREMIÈRE phrase, avant toute autre instruction.",
        "Essaie un prompt ultra-minimaliste : 2-3 lignes max, zéro explication.",
        "Combine la meilleure approche précédente avec une phrase anti-troncature explicite.",
        "Reformule entièrement : approche 'copie fidèle + ponctuation' au lieu de 'nettoyage'.",
        "Essaie de rappeler au modèle que sa sortie doit avoir à peu près la même longueur que l'entrée.",
    ]
    strategy = strategies[min(experiment_num - 1, len(strategies) - 1)]

    user_msg = f"""PROMPT ACTUEL (score={history[-1]['score'] if history else 'baseline'}) :
---
{current_prompt}
---

STRATÉGIE À EXPLORER : {strategy}
{history_text}{sample_feedback}

Rappel : le prompt doit faire MAXIMUM 150 mots. Court et direct.
Propose une variante améliorée. Réponds UNIQUEMENT avec le texte du prompt."""

    return call_ollama_raw(DESIGNER_SYSTEM, user_msg, DESIGNER_MODEL, temperature=0.7)


# ─── Git helpers ─────────────────────────────────────────────────────────────

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


def restore_prompt(best_prompt: str):
    """Restaure le fichier prompt au meilleur connu. Filet de sécurité."""
    with open(PROMPT_FILE, "w", encoding="utf-8") as f:
        f.write(best_prompt)
    log_info("Prompt restauré au meilleur connu")


# ─── Boucle principale ──────────────────────────────────────────────────────

def main():
    ar_cfg = _CFG["autoresearch"]
    bm_cfg = _CFG["benchmark"]

    parser = argparse.ArgumentParser(description="Autoresearch — prompt optimization loop")
    parser.add_argument("--max-experiments", type=int,
                        default=ar_cfg.getint("max_experiments"))
    parser.add_argument("--runs-per-experiment", type=int,
                        default=ar_cfg.getint("runs_per_experiment"),
                        help="Nombre de runs par expérience (réduit la variance)")
    args = parser.parse_args()

    start_time = time.time()

    log_separator()
    log_info(f"AUTORESEARCH v2 — Profil: {bm_cfg['profile']}")
    log_info(f"Modèle cible: {bm_cfg['model']} | Designer: {ar_cfg['designer_model']}")
    log_info(f"Max expériences: {args.max_experiments}")
    log_info(f"Runs par expérience: {args.runs_per_experiment}")
    log_separator()

    # ── Preflight ──
    if not preflight_checks():
        log_error("Abandon — corriger les erreurs ci-dessus puis relancer.")
        sys.exit(1)

    # ── Lire le prompt initial ──
    with open(PROMPT_FILE, "r", encoding="utf-8") as f:
        current_prompt = f.read().strip()
    log_info(f"Prompt initial : {len(current_prompt)} chars, {len(current_prompt.split())} mots")

    # ── Baseline ──
    log_separator()
    log_info("BASELINE — mesure du score de référence")
    log_separator()

    best_score, baseline_detail = run_benchmark(args.runs_per_experiment)
    log_info(f"BASELINE = {best_score}")

    best_prompt = current_prompt
    history = [{"num": 0, "score": best_score, "status": "baseline",
                "description": "prompt original"}]

    with open(RESULTS_FILE, "a", encoding="utf-8") as f:
        f.write(f"0\tbaseline\t{best_score}\tbaseline\tprompt original ({args.runs_per_experiment} runs)\n")

    report_lines = [
        f"Autoresearch v2 started at {time.strftime('%Y-%m-%d %H:%M:%S')}",
        f"Baseline score: {best_score} ({args.runs_per_experiment} runs avg)",
        f"Baseline prompt ({len(current_prompt)} chars):",
        current_prompt,
        "",
        "=" * 60,
    ]

    # ── Boucle d'expériences ──
    for exp_num in range(1, args.max_experiments + 1):
        exp_start = time.time()
        log_separator()
        elapsed_total = time.time() - start_time
        log_info(f"EXPÉRIENCE {exp_num}/{args.max_experiments} (temps total: {elapsed_total/60:.0f}min)")
        log_info(f"Meilleur score actuel : {best_score}")
        log_separator()

        # ── Étape 1 : Health check rapide ──
        log_step(exp_num, args.max_experiments, "Health check Ollama")
        if not check_ollama():
            log_error("Ollama ne répond plus — attente 30s puis retry")
            time.sleep(30)
            if not check_ollama():
                log_error("Ollama toujours down — abandon de cette expérience")
                history.append({"num": exp_num, "score": best_score,
                               "status": "crash", "description": "ollama down"})
                continue

        # ── Étape 2 : Générer une variante ──
        log_step(exp_num, args.max_experiments, "Génération variante via 14B...")
        try:
            t0 = time.time()
            new_prompt = generate_variant(best_prompt, exp_num, history)
            gen_time = time.time() - t0
            log_info(f"  Designer a répondu en {gen_time:.0f}s")
        except Exception as e:
            log_error(f"Designer crash : {e}")
            history.append({"num": exp_num, "score": best_score,
                           "status": "crash", "description": f"designer error: {e}"})
            continue

        # ── Étape 3 : Validation du prompt ──
        log_step(exp_num, args.max_experiments, "Validation du prompt généré")
        word_count = len(new_prompt.split())
        char_count = len(new_prompt)
        log_info(f"  {word_count} mots, {char_count} chars")

        if word_count > 200:
            log_warn(f"  Trop long ({word_count} mots) — tronqué à 200")
            new_prompt = " ".join(new_prompt.split()[:200])
            word_count = 200

        if word_count < 10:
            log_warn(f"  Trop court ({word_count} mots) — skip")
            history.append({"num": exp_num, "score": best_score,
                           "status": "crash", "description": "prompt trop court"})
            continue

        log_info(f"  Aperçu : {new_prompt[:120]}...")

        # ── Étape 4 : Écrire + commit ──
        log_step(exp_num, args.max_experiments, "Écriture prompt + git commit")
        with open(PROMPT_FILE, "w", encoding="utf-8") as f:
            f.write(new_prompt)

        try:
            commit_hash = git_commit(
                f"experiment {exp_num}: {new_prompt[:60].replace(chr(10), ' ')}"
            )
            log_info(f"  Commit : {commit_hash}")
        except Exception as e:
            log_error(f"Git commit failed : {e}")
            restore_prompt(best_prompt)
            history.append({"num": exp_num, "score": best_score,
                           "status": "crash", "description": f"git error: {e}"})
            continue

        # ── Étape 5 : Benchmark ──
        log_step(exp_num, args.max_experiments, f"Benchmark ({args.runs_per_experiment} runs)")
        try:
            new_score, detail = run_benchmark(args.runs_per_experiment)
        except Exception as e:
            log_error(f"Benchmark crash : {e}")
            log_info("  Revert git + restauration prompt")
            git_revert()
            restore_prompt(best_prompt)
            history.append({"num": exp_num, "score": 1.0,
                           "status": "crash", "description": str(e)})
            with open(RESULTS_FILE, "a", encoding="utf-8") as f:
                f.write(f"{exp_num}\t{commit_hash}\t1.0000\tcrash\t{str(e)[:80]}\n")
            continue

        # ── Étape 6 : Décision ──
        log_step(exp_num, args.max_experiments, "Décision keep/discard")
        improved = new_score < best_score
        status = "keep" if improved else "discard"
        delta = best_score - new_score
        desc = f"score={new_score} (delta={delta:+.4f})"
        exp_elapsed = time.time() - exp_start

        if improved:
            log_info(f"  AMÉLIORÉ : {best_score} -> {new_score} (delta={delta:+.4f})")
            best_score = new_score
            best_prompt = new_prompt
        else:
            log_info(f"  PAS MIEUX : {new_score} >= {best_score} — revert")
            git_revert()
            restore_prompt(best_prompt)

        # ── Étape 7 : Log résultats ──
        history.append({"num": exp_num, "score": new_score,
                       "status": status, "description": desc})

        with open(RESULTS_FILE, "a", encoding="utf-8") as f:
            f.write(f"{exp_num}\t{commit_hash}\t{new_score}\t{status}\t{desc}\n")

        report_lines.extend([
            f"\n[EXP {exp_num}] {status.upper()} — score={new_score} (best={best_score})",
            f"  Prompt ({len(new_prompt)} chars, {word_count} mots):",
            f"  {new_prompt[:200]}...",
        ])

        # ── Résumé de l'expérience ──
        log_info(f"  Durée expérience : {exp_elapsed/60:.1f}min")
        kept_so_far = sum(1 for h in history if h["status"] == "keep")
        log_info(f"  Bilan provisoire : {kept_so_far} gardée(s) sur {exp_num} expérience(s)")

        # ── Vérification post-expérience ──
        with open(PROMPT_FILE, "r", encoding="utf-8") as f:
            prompt_on_disk = f.read().strip()
        if prompt_on_disk != best_prompt:
            log_error("INCOHÉRENCE : le prompt sur disque ne correspond pas au meilleur connu")
            restore_prompt(best_prompt)

    # ─── Rapport final ───────────────────────────────────────────────────────
    total_time = time.time() - start_time

    log_separator()
    log_info("AUTORESEARCH v2 TERMINÉ")
    log_separator()

    kept = [h for h in history if h["status"] == "keep"]
    discarded = [h for h in history if h["status"] == "discard"]
    crashed = [h for h in history if h["status"] == "crash"]

    baseline_score = history[0]["score"]
    improvement = ((baseline_score - best_score) / baseline_score * 100) if baseline_score > 0 else 0

    log_info(f"Durée totale : {total_time/60:.0f}min")
    log_info(f"Expériences  : {len(history) - 1} (gardées={len(kept)}, jetées={len(discarded)}, crash={len(crashed)})")
    log_info(f"Baseline     : {baseline_score}")
    log_info(f"Final        : {best_score}")
    log_info(f"Amélioration : {improvement:.1f}%")
    log_info("")
    log_info(f"Meilleur prompt ({len(best_prompt)} chars, {len(best_prompt.split())} mots) :")
    log_info("-" * 40)
    # Afficher le prompt ligne par ligne pour lisibilité
    for line in best_prompt.split("\n"):
        log_info(f"  {line}")
    log_info("-" * 40)

    log_info("")
    log_info("Historique complet :")
    for h in history:
        marker = "->" if h["status"] == "keep" else "x" if h["status"] == "discard" else "!"
        log_info(f"  {marker} Exp {h['num']}: {h['score']:.4f} ({h['status']}) — {h['description']}")

    # Sauvegarder le rapport
    report_lines.extend([
        "",
        "=" * 60,
        "RÉSUMÉ FINAL",
        f"Durée totale: {total_time/60:.0f}min",
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

    log_info(f"Rapport : {REPORT_FILE}")
    log_info(f"Résultats TSV : {RESULTS_FILE}")

    # ── Vérification finale ──
    with open(PROMPT_FILE, "r", encoding="utf-8") as f:
        final_prompt = f.read().strip()
    if final_prompt == best_prompt:
        log_info("CHECK FINAL OK : prompt sur disque = meilleur prompt")
    else:
        log_error("CHECK FINAL FAILED : prompt sur disque incohérent — restauration forcée")
        restore_prompt(best_prompt)

    log_separator()
    log_info("FIN — le script peut être fermé.")
    log_separator()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        log_warn("Interrompu par l'utilisateur (Ctrl+C)")
        log_info("Le dernier prompt valide est dans system_prompt.txt")
        log_info("Les résultats partiels sont dans results.tsv")
        sys.exit(130)
    except Exception as e:
        log_error(f"CRASH INATTENDU : {e}")
        import traceback
        traceback.print_exc()
        log_info("Le dernier prompt valide devrait être dans system_prompt.txt")
        log_info("Vérifier la cohérence avec git log")
        sys.exit(1)
