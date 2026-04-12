"""
Benchmark de qualité pour le prompt de nettoyage de transcription.

Envoie chaque transcription du corpus à Ollama, score le résultat avec
des métriques rule-based + un LLM juge (le 14B local), et produit un
score composite unique.

Usage :
    python benchmark.py [--model MODEL] [--judge-model JUDGE] [--temperature T]
                        [--num-ctx-k N] [--prompt-file FILE] [--corpus FILE]
                        [--verbose]

Sortie : une seule ligne "SCORE=X.XXXX" (pour autoresearch) + détails dans run.log
"""

import argparse
import json
import re
import sys
import time
import urllib.request
import unicodedata

# ─── Configuration par défaut ─────────────────────────────────────────────────

DEFAULT_MODEL = "ministral:3b-instruct-q8"
DEFAULT_JUDGE = "ministral-3:14b"
DEFAULT_TEMP = 0.15
DEFAULT_CTX_K = 32
DEFAULT_ENDPOINT = "http://localhost:11434/api/generate"
DEFAULT_CORPUS = "corpus.json"
DEFAULT_PROMPT = "system_prompt.txt"

# ─── Prompt template (Mistral/Ministral) ──────────────────────────────────────

def format_mistral(system: str, user: str) -> tuple[str, list[str]]:
    """Format Mistral [INST] et tokens de stop."""
    merged = f"{system}\n\n{user}" if system.strip() else user
    return f"[INST] {merged} [/INST]", ["[INST]", "[/INST]", "</s>"]

def strip_stops(text: str, stops: list[str]) -> str:
    for s in stops:
        text = text.replace(s, "")
    return text.strip()

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

    text = strip_stops(data.get("response", ""), stops)
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
    """Écart du ratio de longueur à 1.0. 0.0 = même longueur = parfait."""
    if not input_text:
        return 1.0
    ratio = len(output_text) / len(input_text)
    return abs(ratio - 1.0)

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

MARKDOWN_PATTERNS = [
    r"^#{1,6}\s",     # headers
    r"^\s*[-*]\s",     # bullets
    r"\*\*[^*]+\*\*",  # bold
    r"```",            # code blocks
    r"^\s*\d+\.\s",    # numbered lists
]

def score_markdown(output_text: str) -> float:
    """1.0 si markdown parasite détecté, 0.0 sinon."""
    for pattern in MARKDOWN_PATTERNS:
        if re.search(pattern, output_text, re.MULTILINE):
            return 1.0
    return 0.0

# ─── LLM Juge ────────────────────────────────────────────────────────────────

JUDGE_SYSTEM = """Tu es un évaluateur de qualité de nettoyage de transcription vocale.

Tu reçois une transcription brute (ENTRÉE) et sa version nettoyée (SORTIE).
Le nettoyage attendu est MINIMAL : ponctuation, accents, répétitions immédiates, mots mal transcrits évidents. Rien d'autre.

Évalue la SORTIE sur 4 critères, chacun noté de 1 (très mauvais) à 5 (parfait) :

1. FIDÉLITÉ : la sortie conserve-t-elle tous les mots, concepts et détails de l'entrée sans rien inventer ?
   5 = identique sauf corrections minimales, 1 = contenu inventé ou perdu
2. REGISTRE : le niveau de langue oral/familier est-il conservé ?
   5 = même ton exactement, 1 = transformé en style formel/écrit
3. STRUCTURE : l'ordre des phrases est-il conservé (pas de restructuration) ?
   5 = même ordre, 1 = réorganisé en paragraphes/sections
4. MINIMALITÉ : les corrections sont-elles limitées au strict nécessaire ?
   5 = seules ponctuation/accents/typos corrigés, 1 = phrases reformulées

Réponds UNIQUEMENT dans ce format exact, rien d'autre :
FIDÉLITÉ=N
REGISTRE=N
STRUCTURE=N
MINIMALITÉ=N"""

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
        for key, field in [("FIDÉLITÉ", "fidelite"), ("FIDELITE", "fidelite"),
                           ("REGISTRE", "registre"), ("STRUCTURE", "structure"),
                           ("MINIMALITÉ", "minimalite"), ("MINIMALITE", "minimalite")]:
            if line.upper().startswith(key):
                match = re.search(r"=\s*(\d)", line)
                if match:
                    scores[field] = min(5, max(1, int(match.group(1))))

    # Defaults pour les scores manquants
    for field in ["fidelite", "registre", "structure", "minimalite"]:
        if field not in scores:
            scores[field] = 3
    return scores

# ─── Score composite ──────────────────────────────────────────────────────────

def composite_score(rule_scores: dict, judge_scores: dict) -> float:
    """
    Score composite entre 0.0 (parfait) et 1.0 (terrible).
    Lower is better (pour autoresearch).

    Pondérations :
    - Rule-based (40%) : novel_words 15%, length_ratio 10%, preamble 10%, markdown 5%
    - LLM judge (60%) : fidélité 20%, registre 15%, structure 15%, minimalité 10%
      (scores 1-5 inversés en 0-1 où 0=parfait)
    """
    # Rule-based : déjà en 0-1 où 0 = parfait
    rule = (
        rule_scores["novel_words"] * 0.15 +
        min(rule_scores["length_ratio"], 1.0) * 0.10 +
        rule_scores["preamble"] * 0.10 +
        rule_scores["markdown"] * 0.05
    )

    # LLM judge : convertir 1-5 en 0-1 (5→0.0, 1→1.0)
    def invert(score_1_5):
        return (5 - score_1_5) / 4.0

    judge = (
        invert(judge_scores["fidelite"]) * 0.20 +
        invert(judge_scores["registre"]) * 0.15 +
        invert(judge_scores["structure"]) * 0.15 +
        invert(judge_scores["minimalite"]) * 0.10
    )

    return round(rule + judge, 4)

# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Benchmark prompt nettoyage")
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--judge-model", default=DEFAULT_JUDGE)
    parser.add_argument("--temperature", type=float, default=DEFAULT_TEMP)
    parser.add_argument("--num-ctx-k", type=int, default=DEFAULT_CTX_K)
    parser.add_argument("--prompt-file", default=DEFAULT_PROMPT)
    parser.add_argument("--corpus", default=DEFAULT_CORPUS)
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    # Charger le corpus
    with open(args.corpus, "r", encoding="utf-8") as f:
        corpus = json.load(f)

    # Charger le system prompt
    with open(args.prompt_file, "r", encoding="utf-8") as f:
        system_prompt = f.read().strip()

    num_ctx = args.num_ctx_k * 1024
    all_composites = []
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
        rule = {
            "novel_words": score_novel_words(input_text, output_text),
            "length_ratio": score_length_ratio(input_text, output_text),
            "preamble": score_preamble(output_text),
            "markdown": score_markdown(output_text),
        }

        # Score LLM juge
        if args.verbose:
            print(f"  Appel juge ({args.judge_model})...", end=" ", flush=True)
        judge = llm_judge(input_text, output_text, args.judge_model, args.endpoint)
        if args.verbose:
            print("OK")

        # Composite
        comp = composite_score(rule, judge)
        all_composites.append(comp)

        detail = {
            "id": sid,
            "composite": comp,
            "rule": rule,
            "judge": judge,
            "input_len": len(input_text),
            "output_len": len(output_text),
            "output_preview": output_text[:200],
            "elapsed_sec": round(elapsed, 1),
        }
        details.append(detail)

        if args.verbose:
            print(f"  Rule:  novel={rule['novel_words']:.3f} length={rule['length_ratio']:.3f} "
                  f"preamble={rule['preamble']:.0f} markdown={rule['markdown']:.0f}")
            print(f"  Judge: fid={judge['fidelite']} reg={judge['registre']} "
                  f"str={judge['structure']} min={judge['minimalite']}")
            print(f"  Composite: {comp:.4f}")
            print(f"  Output: {output_text[:120]}...")
            print()

    # Score final = moyenne des composites
    avg = sum(all_composites) / len(all_composites) if all_composites else 1.0

    # Détails dans un fichier JSON pour analyse
    report = {
        "model": args.model,
        "judge_model": args.judge_model,
        "temperature": args.temperature,
        "num_ctx_k": args.num_ctx_k,
        "prompt_chars": len(system_prompt),
        "samples": len(corpus),
        "avg_composite": round(avg, 4),
        "details": details,
    }
    with open("last_report.json", "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    print(f"\n{'='*60}")
    print(f"RÉSULTATS: {len(corpus)} samples, score moyen = {avg:.4f}")
    print(f"  (0.0 = parfait, 1.0 = terrible)")
    for d in details:
        print(f"  #{d['id']}: {d['composite']:.4f} "
              f"(novel={d['rule']['novel_words']:.2f} "
              f"fid={d['judge']['fidelite']} reg={d['judge']['registre']})")
    print(f"{'='*60}")

    # Ligne unique pour autoresearch
    print(f"\nSCORE={avg:.4f}")

if __name__ == "__main__":
    main()
