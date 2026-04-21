"""Rule-based metrics kept as cheap pre-filters.

The new 6-criteria grid is scored by the LLM judge (Claude or Ollama).
These rules are not the primary score anymore — they just flag obvious
catastrophes before we spend a judge call, and feed the diagnostic
printed alongside each sample.
"""

from __future__ import annotations

import re


_WORD_RE = re.compile(r"[a-zàâäéèêëïîôùûüÿçœæ]+")


def normalize_words(text: str) -> set[str]:
    return set(_WORD_RE.findall(text.lower()))


def score_novel_words(input_text: str, output_text: str) -> float:
    """Fraction of output words absent from the input. 0.0 = clean,
    1.0 = fully hallucinated."""
    in_words  = normalize_words(input_text)
    out_words = normalize_words(output_text)
    if not out_words:
        return 1.0
    novel = out_words - in_words
    return len(novel) / len(out_words)


def length_ratio(input_text: str, output_text: str) -> float:
    """Raw char ratio out/in. Used directly in the judge feedback and in
    the length-ratio rule below. Returns 0.0 on empty input."""
    if not input_text:
        return 0.0
    return len(output_text) / len(input_text)


def score_length_ratio(input_text: str, output_text: str) -> float:
    """Length-ratio penalty sized for restructuring: 0.3–1.0 is the
    acceptable band, below 0.3 means information was dropped, above 1.0
    means content was added."""
    r = length_ratio(input_text, output_text)
    if 0.3 <= r <= 1.0:
        return 0.0
    if r < 0.3:
        return (0.3 - r) / 0.3
    return min(1.0, (r - 1.0) / 1.0)


_FORBIDDEN_PREAMBLES = (
    "voici", "bien sûr", "d'accord", "je vais", "la transcription",
    "en voici", "certainement", "avec plaisir", "voilà la version",
    "la version corrigée", "version nettoyée",
)


def score_preamble(output_text: str) -> float:
    """1.0 if the output starts with a known assistant-style preamble."""
    lower = output_text.lower().strip()
    return 1.0 if any(lower.startswith(p) for p in _FORBIDDEN_PREAMBLES) else 0.0


_LIST_PATTERNS = (
    re.compile(r"^\s*[-*]\s", re.MULTILINE),
    re.compile(r"^\s*\d+\.\s", re.MULTILINE),
)


def score_lists(output_text: str) -> float:
    """1.0 if the output contains bullet or numbered lists."""
    return 1.0 if any(p.search(output_text) for p in _LIST_PATTERNS) else 0.0


def is_catastrophe(input_text: str, output_text: str) -> bool:
    """Cheap pre-filter: skip the LLM judge when the output is obviously
    broken (mass hallucination or runaway length)."""
    return (
        score_novel_words(input_text, output_text) > 0.85
        or length_ratio(input_text, output_text) > 2.0
    )


def rule_diagnostic(input_text: str, output_text: str) -> dict:
    """All rule signals bundled for report output."""
    return {
        "novel_words":  round(score_novel_words(input_text, output_text), 4),
        "length_ratio": round(length_ratio(input_text, output_text), 4),
        "preamble":     score_preamble(output_text),
        "lists":        score_lists(output_text),
    }
