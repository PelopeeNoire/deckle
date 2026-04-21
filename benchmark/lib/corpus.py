"""Reader for the WhispUI corpus JSONL files.

WhispUI writes one line per transcription into
``benchmark/telemetry/<profile-slug>/corpus.jsonl``. Each line is a
TelemetryEvent envelope:

    {
      "timestamp": "2026-04-21T14:32:11.482+02:00",
      "kind":      "Corpus",
      "session":   "2026-04-21-a7c3",
      "payload": {
        "profile":          "Restructuration",
        "profile_id":       "restructuration",
        "slug":             "restructuration",
        "duration_seconds": 45.3,
        "whisper": {
          "model":          "ggml-large-v3.bin",
          "language":       "fr",
          "elapsed_ms":     1234,
          "initial_prompt": "…"
        },
        "raw":     { "text": "…", "word_count": 42, "char_count": 320 },
        "metrics": { "words_per_second": 2.8 },
        "audio_file": "…"
      }
    }

Consumers want the raw Whisper text plus enough metadata to slice by
duration and group by initial prompt. Anything else stays accessible via
``Sample.envelope``.
"""

from __future__ import annotations

import glob as _glob
import json
from dataclasses import dataclass, field
from pathlib import Path


@dataclass(slots=True)
class Sample:
    source_file:      str
    line_no:          int
    timestamp:        str
    session:          str
    profile:          str
    slug:             str
    duration_seconds: float
    raw_text:         str
    initial_prompt:   str | None
    whisper_model:    str
    audio_file:       str | None
    envelope:         dict = field(repr=False)

    @property
    def id(self) -> str:
        """Stable per-sample identifier across runs.

        After the telemetry refonte every profile's corpus lives in
        ``<profile-slug>/corpus.jsonl``, so the file stem alone ("corpus")
        no longer disambiguates. The slug is the profile identity and is
        unique across the tree — use it as the per-sample prefix.
        """
        prefix = self.slug or Path(self.source_file).parent.name or "corpus"
        return f"{prefix}:{self.line_no}"


def _parse_envelope(envelope: dict, source_file: str, line_no: int) -> Sample | None:
    kind = str(envelope.get("kind", "")).lower()
    if kind != "corpus":
        return None
    payload = envelope.get("payload") or {}
    raw     = payload.get("raw") or {}
    whisper = payload.get("whisper") or {}

    text = raw.get("text") or ""
    if not text.strip():
        return None

    return Sample(
        source_file      = source_file,
        line_no          = line_no,
        timestamp        = str(envelope.get("timestamp", "")),
        session          = str(envelope.get("session", "")),
        profile          = str(payload.get("profile", "")),
        slug             = str(payload.get("slug", "")),
        duration_seconds = float(payload.get("duration_seconds", 0.0)),
        raw_text         = text,
        initial_prompt   = whisper.get("initial_prompt") or None,
        whisper_model    = str(whisper.get("model", "")),
        audio_file       = payload.get("audio_file") or None,
        envelope         = envelope,
    )


def load_corpus(
    patterns: list[str] | str,
    *,
    duration_min: float | None = None,
    duration_max: float | None = None,
    initial_prompt: str | None = None,
    slug:           str | None = None,
) -> list[Sample]:
    """Load every JSONL matching ``patterns`` and return the kept samples.

    Patterns may be absolute or relative — relative paths are resolved
    against the current working directory (callers that pin the layout
    should resolve against ``benchmark/`` themselves).

    Filters:
        - ``duration_min`` / ``duration_max``: inclusive bounds in seconds.
        - ``initial_prompt``: exact match on ``whisper.initial_prompt``.
          Use the literal empty string ``""`` to keep only entries with
          no initial prompt set.
        - ``slug``: exact match on the payload slug (profile folder).
    """
    if isinstance(patterns, str):
        patterns = [patterns]

    paths: list[str] = []
    for pattern in patterns:
        paths.extend(sorted(_glob.glob(pattern)))
    if not paths:
        return []

    samples: list[Sample] = []
    for path in paths:
        with open(path, "r", encoding="utf-8") as f:
            for line_no, line in enumerate(f, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    envelope = json.loads(line)
                except json.JSONDecodeError:
                    continue
                sample = _parse_envelope(envelope, path, line_no)
                if sample is None:
                    continue
                if duration_min is not None and sample.duration_seconds < duration_min:
                    continue
                if duration_max is not None and sample.duration_seconds > duration_max:
                    continue
                if initial_prompt is not None and (sample.initial_prompt or "") != initial_prompt:
                    continue
                if slug is not None and sample.slug != slug:
                    continue
                samples.append(sample)
    return samples


def group_by_initial_prompt(samples: list[Sample]) -> dict[str, list[Sample]]:
    """Bucket samples by ``whisper.initial_prompt`` value.

    Useful when comparing the impact of a prompt change: each bucket is a
    subset of the corpus recorded under the same Whisper primer.
    """
    buckets: dict[str, list[Sample]] = {}
    for s in samples:
        buckets.setdefault(s.initial_prompt or "", []).append(s)
    return buckets
