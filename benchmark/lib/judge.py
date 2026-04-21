"""Common judge contract: six-criteria grid + composite scoring.

Two backends implement this contract — ``judge_claude.ClaudeJudge`` and
``judge_ollama.OllamaJudge`` — selected via the ``judge_backend``
setting in ``config.ini``.

Scoring convention (kept aligned with the legacy benchmark so
autoresearch history stays comparable):

    Each criterion is rated 1..5.
    ``composite_score`` returns a scalar in [0.0, 1.0] where
    lower = better, produced by inverting each 1..5 score to a
    0..1 penalty and taking the weighted sum.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Protocol


CRITERIA: tuple[tuple[str, float], ...] = (
    ("completude_macro",      0.25),
    ("preservation_nuances",  0.25),
    ("densite_preservee",     0.15),
    ("non_invention",         0.15),
    ("structure_thematique",  0.10),
    ("clarte_registre",       0.10),
)

# Aliases accepted when parsing judge output, mapped to canonical keys.
_ALIASES: dict[str, str] = {
    "completude_macro":     "completude_macro",
    "completude":           "completude_macro",
    "complétude_macro":     "completude_macro",
    "complétude":           "completude_macro",
    "preservation_nuances": "preservation_nuances",
    "préservation_nuances": "preservation_nuances",
    "nuances":              "preservation_nuances",
    "densite_preservee":    "densite_preservee",
    "densité_preservee":    "densite_preservee",
    "densité_préservée":    "densite_preservee",
    "densite":              "densite_preservee",
    "densité":              "densite_preservee",
    "non_invention":        "non_invention",
    "non-invention":        "non_invention",
    "invention":            "non_invention",
    "structure_thematique": "structure_thematique",
    "structure_thématique": "structure_thematique",
    "structure":            "structure_thematique",
    "clarte_registre":      "clarte_registre",
    "clarté_registre":      "clarte_registre",
    "clarte":               "clarte_registre",
    "clarté":               "clarte_registre",
    "registre":             "clarte_registre",
}


@dataclass(slots=True)
class JudgeResult:
    scores:   dict[str, int]   # canonical key → 1..5
    raw:      str              # verbatim model response, kept for audit
    backend:  str              # "claude" | "ollama"
    model:    str

    def composite(self) -> float:
        return composite_score(self.scores)


class Judge(Protocol):
    backend: str
    model:   str

    def score(self, raw_text: str, restructured: str) -> JudgeResult: ...


def composite_score(scores: dict[str, int]) -> float:
    """Weighted penalty in [0.0, 1.0], lower = better."""
    total = 0.0
    for key, weight in CRITERIA:
        s = scores.get(key, 3)
        total += ((5 - s) / 4.0) * weight
    return round(total, 4)


_LINE_RE = re.compile(
    r"^\s*([A-Za-zÀ-ÿ_\-]+)\s*[:=]\s*(\d)\s*$"
)


def parse_scores(response: str) -> dict[str, int]:
    """Extract the six canonical scores from the judge's raw response.

    Missing criteria default to 3 (neutral) so a malformed response still
    yields a usable composite. The caller can inspect ``JudgeResult.raw``
    to investigate when the composite looks suspiciously flat.
    """
    found: dict[str, int] = {}
    for line in response.splitlines():
        m = _LINE_RE.match(line)
        if not m:
            continue
        label = m.group(1).strip().lower().replace("-", "_")
        value = int(m.group(2))
        canonical = _ALIASES.get(label)
        if canonical is None:
            continue
        found[canonical] = max(1, min(5, value))

    for key, _ in CRITERIA:
        found.setdefault(key, 3)
    return found
