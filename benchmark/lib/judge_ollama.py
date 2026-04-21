"""Ollama-backed judge — kept as a local fallback for the Claude judge.

Used when ``judge_backend = ollama`` in config.ini, typically when the
Anthropic API key is unavailable or when Louis wants a fully local run
for comparison.
"""

from __future__ import annotations

from . import ollama as ollama_client
from .judge import Judge, JudgeResult, parse_scores


class OllamaJudge(Judge):
    backend = "ollama"

    def __init__(
        self,
        *,
        system_prompt: str,
        model: str,
        endpoint: str,
        temperature: float = 0.0,
        num_ctx: int = 8192,
    ) -> None:
        self.system_prompt = system_prompt
        self.model         = model
        self.endpoint      = endpoint
        self.temperature   = temperature
        self.num_ctx       = num_ctx

    def score(self, raw_text: str, restructured: str) -> JudgeResult:
        user_msg = f"ENTRÉE :\n{raw_text}\n\nSORTIE :\n{restructured}"
        response, _ = ollama_client.call_ollama(
            system      = self.system_prompt,
            user        = user_msg,
            model       = self.model,
            temperature = self.temperature,
            num_ctx     = self.num_ctx,
            endpoint    = self.endpoint,
        )
        return JudgeResult(
            scores  = parse_scores(response),
            raw     = response,
            backend = self.backend,
            model   = self.model,
        )
