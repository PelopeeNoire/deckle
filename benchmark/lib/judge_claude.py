"""Claude Sonnet judge via the Anthropic SDK.

The six-criteria grid is long enough that we keep it in the ``system``
block with ``cache_control: ephemeral`` — every sample in an
autoresearch run reuses the same primer, so the cached input tokens
dominate after the first call.

Environment:
    ANTHROPIC_API_KEY must be set. The Anthropic SDK reads it
    automatically; we do not pass the key explicitly.

Failure mode:
    Any SDK error surfaces as a neutral result (all scores = 3) with
    the exception captured in ``JudgeResult.raw``, so a single API
    hiccup doesn't wipe an entire benchmark run.
"""

from __future__ import annotations

import os

from .judge import Judge, JudgeResult, parse_scores


DEFAULT_MODEL = "claude-sonnet-4-6"


class ClaudeJudge(Judge):
    backend = "claude"

    def __init__(
        self,
        *,
        system_prompt: str,
        model: str = DEFAULT_MODEL,
        max_tokens: int = 256,
        temperature: float = 0.0,
    ) -> None:
        # Import lazily so ``python benchmark.py --judge ollama`` doesn't
        # require the SDK to be installed on the local machine.
        try:
            import anthropic
        except ImportError as exc:
            raise RuntimeError(
                "The 'anthropic' package is required for the Claude judge. "
                "Install it with: pip install anthropic"
            ) from exc

        if not os.environ.get("ANTHROPIC_API_KEY"):
            raise RuntimeError(
                "ANTHROPIC_API_KEY is not set — cannot use the Claude judge."
            )

        self._client       = anthropic.Anthropic()
        self.system_prompt = system_prompt
        self.model         = model
        self.max_tokens    = max_tokens
        self.temperature   = temperature

    def score(self, raw_text: str, restructured: str) -> JudgeResult:
        user_msg = f"ENTRÉE :\n{raw_text}\n\nSORTIE :\n{restructured}"
        try:
            message = self._client.messages.create(
                model       = self.model,
                max_tokens  = self.max_tokens,
                temperature = self.temperature,
                system = [
                    {
                        "type":          "text",
                        "text":          self.system_prompt,
                        "cache_control": {"type": "ephemeral"},
                    }
                ],
                messages = [
                    {"role": "user", "content": user_msg},
                ],
            )
            response = "".join(
                block.text for block in message.content if getattr(block, "type", "") == "text"
            )
        except Exception as exc:
            # Neutral fallback: 3 across the board, failure kept in `raw`.
            response = f"[ERROR] {exc}"
            return JudgeResult(
                scores  = parse_scores(""),
                raw     = response,
                backend = self.backend,
                model   = self.model,
            )

        return JudgeResult(
            scores  = parse_scores(response),
            raw     = response,
            backend = self.backend,
            model   = self.model,
        )
