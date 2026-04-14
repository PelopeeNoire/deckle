"""
Benchmark de qualité pour le prompt de restructuration de transcription.

Envoie chaque transcription du corpus à Ollama, score le résultat avec
des métriques rule-based (score brut) + optionnellement un LLM juge
(score séparé, non mélangé au brut).

Usage :
    python benchmark.py [--model MODEL] [--judge-model JUDGE] [--temperature T]
                        [--num-ctx-k N] [--prompt-file FILE] [--corpus FILE]
                        [--verbose] [--skip-judge]

Sortie : une seule ligne "SCORE=X.XXXX" + détails dans last_report.json.
Pour la RESTRUCTURATION, SCORE = médiane juge LLM (reformulation autorisée → novel_words
n'est plus un signal fiable, seul le juge peut évaluer complétude + sobriété).
Si --skip-judge, SCORE retombe sur la médiane rule-based.
"""

import argparse
import configparser
import io
import json
import os
import re
import statistics
import sys
import time
import urllib.request
import unicodedata

# Force UTF-8 stdout/stderr on Windows (sinon CP1252 crash sur les accents en redirection)
if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

# ─── Configuration ───────────────────────────────────────────────────────────

BENCHMARK_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(BENCHMARK_DIR, "config.ini")

def load_config() -> configparser.ConfigParser:
    """Charge config.ini, retourne le parser avec les défauts."""
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
    if os.path.exists(CONFIG_FILE):
        cfg.read(CONFIG_FILE, encoding="utf-8")
    return cfg

# ─── Prompt template (Mistral/Ministral) ──────────────────────────────────────

def format_mistral(system: str, user: str) -> tuple[str, list[str]]:
    """Format Mistral [INST] et tokens de stop."""
    merged = f"{system}\n\n{user}" if system.strip() else user
    return f"[INST] {merged} [/INST]", ["[INST]", "[/INST]", "</s>"]

def strip_stops(text: str, stops: list[str]) -> str:
    for s in stops:
        text = text.replace(s, "")
    return text.strip()

def sanitize(text: str) -> str:
    """Supprime les caractères de contrôle et séquences ANSI du texte LLM.
    Empêche le terminal d'interpréter des escape sequences comme des inputs."""
    text = re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', text)
    text = re.sub(r'\x1b\][^\x07]*\x07', '', text)
    text = re.sub(r'\x1b[^[\]0-9;a-zA-Z]', '', text)
    text = re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]', '', text)
    return text

# ─── Appel Ollama ─────────────────────────────────────────────────────────────

def call_ollama(prompt: str, stops: list[str], model: str, temperature: float,
                num_ctx: int, endpoint: str) -> tuple[str, dict]:
    """Appelle Ollama en raw mode, retourne (texte, métriques)."""
    body = json.dumps({
        "model": model,
        "prompt": prompt,
        "raw": True,
        "stream": False,
        "keep_alive": "5m",
        "options": {
            "temperature": temperature,
            "num_ctx": num_ctx,
            **({"stop": stops} if stops else {})
        }
    }).encode("utf-8")

    req = urllib.request.Request(
        endpoint,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read().decode("utf-8"))

    text = sanitize(strip_stops(data.get("response", ""), stops))
    metrics = {
        "total_duration_ms": data.get("total_duration", 0) / 1e6,
        "eval_count": data.get("eval_count", 0),
        "eval_duration_ms": data.get("eval_duration", 0) / 1e6,
    }
    return text, metrics

# ─── Métriques rule-based ─────────────────────────────────────────────────────

def normalize_words(text: str) -> set[str]:
    """Extrait un set de mots normalisés (minuscule, sans accents gardés)."""
    words = re.findall(r"[a-zàâäéèêëïîôùûüÿçœæ]+", text.lower())
    return set(words)

def score_novel_words(input_text: str, output_text: str) -> float:
    """Ratio de mots dans la sortie absents de l'entrée. 0.0 = parfait."""
    in_words = normalize_words(input_text)
    out_words = normalize_words(output_text)
    if not out_words:
        return 1.0  # sortie vide = mauvais
    novel = out_words - in_words
    return len(novel) / len(out_words)

def score_length_ratio(input_text: str, output_text: str) -> float:
    """Pénalité de longueur adaptée à la restructuration.
    La restructuration raccourcit le texte (ratio 0.4-0.8 = normal).
    On pénalise : trop court (<0.3 = perte d'info probable) ou trop long (>1.0 = bruit ajouté).
    0.0 = ratio dans la zone idéale, 1.0 = ratio extrême."""
    if not input_text:
        return 1.0
    ratio = len(output_text) / len(input_text)
    if 0.3 <= ratio <= 1.0:
        return 0.0  # zone acceptable pour la restructuration
    elif ratio < 0.3:
        return (0.3 - ratio) / 0.3  # 0.0→1.0 quand ratio descend de 0.3 à 0.0
    else:
        return min(1.0, (ratio - 1.0) / 1.0)  # pénalité croissante au-dessus de 1.0

FORBIDDEN_PREAMBLES = [
    "voici", "bien sûr", "d'accord", "je vais", "la transcription",
    "en voici", "certainement", "avec plaisir", "voilà la version",
    "la version corrigée", "version nettoyée"
]

def score_preamble(output_text: str) -> float:
    """1.0 si préambule interdit détecté, 0.0 sinon."""
    lower = output_text.lower().strip()
    for phrase in FORBIDDEN_PREAMBLES:
        if lower.startswith(phrase):
            return 1.0
    return 0.0

LIST_PATTERNS = [
    r"^\s*[-*]\s",     # bullets
    r"^\s*\d+\.\s",    # numbered lists
]

def score_lists(output_text: str) -> float:
    """1.0 si listes (bullet/numérotées) détectées, 0.0 sinon.
    Bold, italiques, titres sont OK pour la lisibilité."""
    for pattern in LIST_PATTERNS:
        if re.search(pattern, output_text, re.MULTILINE):
            return 1.0
    return 0.0

# ─── LLM Juge ────────────────────────────────────────────────────────────────

JUDGE_SYSTEM = """Tu es un évaluateur de qualité de restructuration de transcription vocale.

Tu reçois une transcription orale brute (ENTRÉE) et sa version restructurée (SORTIE).
La restructuration attendue transforme un discours oral décousu en texte écrit clair, organisé en paragraphes, en conservant TOUTES les idées.

Évalue la SORTIE sur 4 critères, chacun noté de 1 (très mauvais) à 5 (parfait) :

1. COMPLÉTUDE : toutes les idées, concepts et détails de l'entrée sont-ils présents dans la sortie ?
   5 = aucune idée perdue, 1 = idées importantes manquantes
2. CLARTÉ : le texte est-il bien écrit, fluide, facile à lire ?
   5 = prose claire et naturelle, 1 = confus ou mal formulé
3. STRUCTURE : les idées sont-elles bien organisées en paragraphes logiques ?
   5 = organisation thématique claire, 1 = vrac sans structure
4. SOBRIÉTÉ : le modèle s'est-il abstenu d'inventer des idées ou d'ajouter du contenu absent de l'entrée ?
   5 = rien inventé, 1 = contenu ajouté ou hallucinations

Réponds UNIQUEMENT dans ce format exact, rien d'autre :
COMPLÉTUDE=N
CLARTÉ=N
STRUCTURE=N
SOBRIÉTÉ=N"""

def llm_judge(input_text: str, output_text: str, judge_model: str,
              endpoint: str) -> dict[str, int]:
    """Appelle le 14B comme juge, retourne les 4 scores (1-5)."""
    user_msg = f"ENTRÉE :\n{input_text}\n\nSORTIE :\n{output_text}"
    prompt, stops = format_mistral(JUDGE_SYSTEM, user_msg)

    try:
        response, _ = call_ollama(
            prompt=prompt, stops=stops, model=judge_model,
            temperature=0.0, num_ctx=4096, endpoint=endpoint
        )
    except Exception as e:
        print(f"  [JUDGE ERROR] {e}", file=sys.stderr)
        return {"fidelite": 3, "registre": 3, "structure": 3, "minimalite": 3}

    scores = {}
    for line in response.strip().split("\n"):
        line = line.strip()
        for key, field in [("COMPLÉTUDE", "completude"), ("COMPLETUDE", "completude"),
                           ("CLARTÉ", "clarte"), ("CLARTE", "clarte"),
                           ("STRUCTURE", "structure"),
                           ("SOBRIÉTÉ", "sobriete"), ("SOBRIETE", "sobriete")]:
            if line.upper().startswith(key):
                match = re.search(r"=\s*(\d)", line)
                if match:
                    scores[field] = min(5, max(1, int(match.group(1))))

    # Defaults pour les scores manquants
    for field in ["completude", "clarte", "structure", "sobriete"]:
        if field not in scores:
            scores[field] = 3
    return scores

# ─── Score composite ──────────────────────────────────────────────────────────

def rule_score(rule_scores: dict) -> float:
    """
    Score rule-based entre 0.0 (parfait) et 1.0 (terrible).
    Lower is better.

    Pondérations adaptées à la RESTRUCTURATION :
    - preamble 35% : signal le plus fiable (préambule = jamais utile)
    - length_ratio 40% : trop court = idées perdues, trop long = bruit ajouté
    - novel_words 25% : bruyant pour restructuration (reformulation crée des mots "nouveaux")
                        mais attrape les hallucinations extrêmes
    - markdown : ignoré (la mise en forme est bienvenue pour la lisibilité)
    """
    return round(
        rule_scores["preamble"] * 0.35 +
        rule_scores["length_ratio"] * 0.40 +
        rule_scores["novel_words"] * 0.25,
        4
    )


def judge_score(judge_scores: dict) -> float:
    """
    Score juge entre 0.0 (parfait) et 1.0 (terrible).
    Lower is better. Affiché séparément, NON mélangé au score brut.

    Pondérations (restructuration) :
    - complétude 35% : toutes les idées présentes
    - sobriété 25% : rien inventé
    - clarté 20% : bien écrit
    - structure 20% : bien organisé
    """
    def invert(score_1_5):
        return (5 - score_1_5) / 4.0

    return round(
        invert(judge_scores["completude"]) * 0.35 +
        invert(judge_scores["sobriete"]) * 0.25 +
        invert(judge_scores["clarte"]) * 0.20 +
        invert(judge_scores["structure"]) * 0.20,
        4
    )

# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    cfg = load_config()["benchmark"]

    parser = argparse.ArgumentParser(description="Benchmark prompt nettoyage")
    parser.add_argument("--model", default=cfg["model"])
    parser.add_argument("--judge-model", default=cfg["judge_model"])
    parser.add_argument("--temperature", type=float, default=cfg.getfloat("temperature"))
    parser.add_argument("--num-ctx-k", type=int, default=cfg.getint("num_ctx_k"))
    parser.add_argument("--prompt-file", default=cfg["prompt"])
    parser.add_argument("--corpus", default=cfg["corpus"])
    parser.add_argument("--endpoint", default=cfg["endpoint"])
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--skip-judge", action="store_true",
                        help="Skip LLM judge (rule-based score only, faster)")
    args = parser.parse_args()

    # Charger le corpus
    with open(args.corpus, "r", encoding="utf-8") as f:
        corpus = json.load(f)

    # Charger le system prompt
    with open(args.prompt_file, "r", encoding="utf-8") as f:
        system_prompt = f.read().strip()

    num_ctx = args.num_ctx_k * 1024
    all_rule_scores = []
    all_judge_scores = []
    details = []

    print(f"=== Benchmark: {args.model} | temp={args.temperature} | ctx={args.num_ctx_k}K ===")
    print(f"=== Prompt: {len(system_prompt)} chars | Corpus: {len(corpus)} samples ===")
    print()

    for sample in corpus:
        sid = sample["id"]
        input_text = sample["text"]

        # Formater et envoyer
        prompt, stops = format_mistral(system_prompt, input_text)

        print(f"[{sid}/{len(corpus)}] Envoi ({len(input_text)} chars)...", end=" ", flush=True)
        t0 = time.time()

        try:
            output_text, metrics = call_ollama(
                prompt=prompt, stops=stops, model=args.model,
                temperature=args.temperature, num_ctx=num_ctx,
                endpoint=args.endpoint
            )
        except Exception as e:
            print(f"ERREUR: {e}")
            all_composites.append(1.0)  # pire score
            continue

        elapsed = time.time() - t0
        print(f"OK ({elapsed:.1f}s, {metrics.get('eval_count', '?')} tokens)")

        # Scores rule-based
        rules = {
            "novel_words": score_novel_words(input_text, output_text),
            "length_ratio": score_length_ratio(input_text, output_text),
            "preamble": score_preamble(output_text),
            "lists": score_lists(output_text),
        }
        r_score = rule_score(rules)
        all_rule_scores.append(r_score)

        # Détection catastrophe (seuils adaptés restructuration : reformulation = normal)
        is_catastrophe = (rules["novel_words"] > 0.85 or rules["length_ratio"] > 0.8)

        # Score juge (optionnel, séparé)
        judge = None
        j_score = None
        if not args.skip_judge and not is_catastrophe:
            if args.verbose:
                print(f"  Appel juge ({args.judge_model})...", end=" ", flush=True)
            judge = llm_judge(input_text, output_text, args.judge_model, args.endpoint)
            j_score = judge_score(judge)
            all_judge_scores.append(j_score)
            if args.verbose:
                print("OK")
        elif is_catastrophe:
            print(f"  [CATASTROPHE] novel={rules['novel_words']:.2f} length_ratio={rules['length_ratio']:.2f} — juge skip")
            judge = {"completude": 1, "clarte": 1, "structure": 1, "sobriete": 1}
            j_score = 1.0
            all_judge_scores.append(j_score)

        detail = {
            "id": sid,
            "rule_score": r_score,
            "judge_score": j_score,
            "catastrophe": is_catastrophe,
            "rule": rules,
            "judge": judge,
            "input_len": len(input_text),
            "output_len": len(output_text),
            "length_ratio": round(len(output_text) / max(1, len(input_text)), 2),
            "output_text": output_text,
            "elapsed_sec": round(elapsed, 1),
        }
        details.append(detail)

        if args.verbose:
            print(f"  Rule:  novel={rules['novel_words']:.3f} length={rules['length_ratio']:.3f} "
                  f"preamble={rules['preamble']:.0f} lists={rules['lists']:.0f} → {r_score:.4f}")
            if judge:
                print(f"  Judge: comp={judge['completude']} clar={judge['clarte']} "
                      f"str={judge['structure']} sob={judge['sobriete']} → {j_score:.4f}")
            print(f"  Output: {output_text[:120]}...")
            print()

    # Score final = médiane des rule scores (robuste aux outliers)
    rule_median = statistics.median(all_rule_scores) if all_rule_scores else 1.0
    rule_mean = sum(all_rule_scores) / len(all_rule_scores) if all_rule_scores else 1.0
    judge_median = statistics.median(all_judge_scores) if all_judge_scores else None
    judge_mean = (sum(all_judge_scores) / len(all_judge_scores)) if all_judge_scores else None

    # Détails dans un fichier JSON pour analyse
    report = {
        "model": args.model,
        "judge_model": args.judge_model if not args.skip_judge else None,
        "temperature": args.temperature,
        "num_ctx_k": args.num_ctx_k,
        "prompt_chars": len(system_prompt),
        "samples": len(corpus),
        "rule_median": round(rule_median, 4),
        "rule_mean": round(rule_mean, 4),
        "judge_median": round(judge_median, 4) if judge_median is not None else None,
        "judge_mean": round(judge_mean, 4) if judge_mean is not None else None,
        "details": details,
    }
    with open("last_report.json", "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    catastrophes = sum(1 for d in details if d.get("catastrophe", False))
    print(f"\n{'='*60}")
    print(f"RÉSULTATS: {len(corpus)} samples")
    print(f"  Rule-based : médiane={rule_median:.4f} moyenne={rule_mean:.4f}")
    if judge_median is not None:
        print(f"  Juge (14B)  : médiane={judge_median:.4f} moyenne={judge_mean:.4f}")
    if catastrophes:
        print(f"  ({catastrophes} catastrophe(s) détectée(s) — juge skip)")
    print(f"  (0.0 = parfait, 1.0 = terrible)")
    for d in details:
        tag = " [!]" if d.get("catastrophe") else ""
        j_str = f" juge={d['judge_score']:.2f}" if d.get("judge_score") is not None else ""
        print(f"  #{d['id']}: rule={d['rule_score']:.4f}{j_str} "
              f"(novel={d['rule']['novel_words']:.2f} "
              f"len_ratio={d['length_ratio']:.2f}){tag}")
    print(f"{'='*60}")

    # Ligne unique pour autoresearch.
    # Restructuration : la reformulation est autorisée → novel_words est bruyant.
    # Le juge LLM est le seul signal capable de mesurer complétude + sobriété.
    # Fallback sur rule_median uniquement si --skip-judge (pas de juge dispo).
    primary = judge_median if judge_median is not None else rule_median
    print(f"\nSCORE={primary:.4f}")

if __name__ == "__main__":
    main()
