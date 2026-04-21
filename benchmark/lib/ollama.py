"""Thin Ollama client shared by benchmark.py and autoresearch.py.

Covers the two call shapes the benchmark uses:

    - ``call_ollama(system, user, model, temperature, num_ctx, endpoint)``
      for scored runs (returns text plus a small metrics dict).
    - ``call_ollama_text(system, user, model, temperature, num_ctx, endpoint)``
      when the caller only needs the generated text (designer loop, judge).

Both go through the Mistral ``[INST] ... [/INST]`` raw format. The helper
also owns the sanitize pass that strips ANSI / control chars from LLM
output so terminal redirection never interprets them as keystrokes.
"""

from __future__ import annotations

import json
import re
import urllib.request
from dataclasses import dataclass


_INST_STOPS = ["[INST]", "[/INST]", "</s>", "<s>"]


@dataclass(slots=True)
class OllamaMetrics:
    total_duration_ms: float
    eval_count: int
    eval_duration_ms: float


def sanitize(text: str) -> str:
    """Drop ANSI escape sequences and control chars from LLM output.

    Prevents a model that generates ``\\x1b[...`` from being interpreted
    by the terminal as input (seen in the wild with noisy prompts).
    """
    text = re.sub(r"\x1b\[[0-9;]*[a-zA-Z]", "", text)
    text = re.sub(r"\x1b\][^\x07]*\x07", "", text)
    text = re.sub(r"\x1b[^\[\]0-9;a-zA-Z]", "", text)
    text = re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", "", text)
    return text


def _format_mistral(system: str, user: str) -> str:
    merged = f"{system}\n\n{user}" if system.strip() else user
    return f"[INST] {merged} [/INST]"


def _strip_stops(text: str) -> str:
    for s in _INST_STOPS:
        text = text.replace(s, "")
    return text.strip()


def call_ollama(
    *,
    system: str,
    user: str,
    model: str,
    temperature: float,
    num_ctx: int,
    endpoint: str,
    timeout: float = 300.0,
    keep_alive: str = "5m",
) -> tuple[str, OllamaMetrics]:
    """Raw Mistral call → (sanitized text, metrics)."""
    prompt = _format_mistral(system, user)
    body = json.dumps(
        {
            "model":      model,
            "prompt":     prompt,
            "raw":        True,
            "stream":     False,
            "keep_alive": keep_alive,
            "options": {
                "temperature": temperature,
                "num_ctx":     num_ctx,
                "stop":        _INST_STOPS,
            },
        }
    ).encode("utf-8")

    req = urllib.request.Request(
        endpoint,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))

    text = sanitize(_strip_stops(data.get("response", "")))
    metrics = OllamaMetrics(
        total_duration_ms=data.get("total_duration", 0) / 1e6,
        eval_count=data.get("eval_count", 0),
        eval_duration_ms=data.get("eval_duration", 0) / 1e6,
    )
    return text, metrics


def call_ollama_text(
    *,
    system: str,
    user: str,
    model: str,
    temperature: float = 0.7,
    num_ctx: int = 8192,
    endpoint: str,
    timeout: float = 300.0,
    keep_alive: str = "10m",
) -> str:
    """Thin wrapper when the caller only wants the generated text."""
    text, _ = call_ollama(
        system=system,
        user=user,
        model=model,
        temperature=temperature,
        num_ctx=num_ctx,
        endpoint=endpoint,
        timeout=timeout,
        keep_alive=keep_alive,
    )
    return text


def check_endpoint(endpoint: str, timeout: float = 10.0) -> list[str]:
    """GET /api/tags to confirm Ollama is up. Returns the loaded model list
    (empty on failure, caller decides whether to abort)."""
    base = endpoint.rstrip("/").removesuffix("/api/generate")
    try:
        req = urllib.request.Request(f"{base}/api/tags", method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        return [m["name"] for m in data.get("models", [])]
    except Exception:
        return []
