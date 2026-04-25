"""Whisper transcription bench — re-runs whisper.cpp on the corpus.

This script does **only one job**: re-transcribe each WAV of the
filtered corpus with a candidate set of Whisper inference parameters,
then dump the results to a JSON file that a human (or the running
Claude session) can read and judge.

There is intentionally **no automated LLM judge here** — pulling a
remote scoring API into the bench has been explicitly ruled out
(no API key on disk, ever). The expected workflow is:

    1. you ask the running session to launch this script,
    2. it transcribes everything and writes the report,
    3. the session reads the JSON, evaluates each transcription
       qualitatively (coherence, accents, hallucinations, registre),
       and proposes the next initial prompt to try,
    4. you validate, the prompt file is updated, you re-run.

Usage:
    python whisper_bench.py [--bracket {relecture,lissage,affinage,arrangement}]
                            [--slug SLUG] [--limit N]
                            [--initial-prompt PROMPT | --initial-prompt-file FILE]
                            [--temperature T] [--beam-size N]
                            [--verbose]

Outputs (under benchmark/reports/):
    last_whisper_run.json     full structured dump
    last_whisper_run.txt      compact human-readable companion
"""

from __future__ import annotations

import argparse
import configparser
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path

# Force UTF-8 stdout/stderr on Windows so accented output survives
# terminal redirection.
if sys.stdout.encoding != "utf-8":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

BENCHMARK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BENCHMARK_DIR))

from lib.corpus import BRACKET_SLUGS, Sample, bracket_of, load_corpus     # noqa: E402


CONFIG_FILE         = BENCHMARK_DIR / "config" / "config.ini"
REPORTS_DIR         = BENCHMARK_DIR / "reports"
DEFAULT_CORPUS_GLOB = str(BENCHMARK_DIR / "telemetry" / "*" / "corpus.jsonl")

REPO_ROOT      = BENCHMARK_DIR.parent
DEFAULT_BINARY = str((REPO_ROOT / "whisper.cpp" / "build" / "bin" / "whisper-cli.exe").resolve())
DEFAULT_MODEL  = str((REPO_ROOT / "models" / "ggml-large-v3.bin").resolve())


def load_config() -> configparser.ConfigParser:
    cfg = configparser.ConfigParser()
    cfg["whisper_bench"] = {
        "whisper_binary":         DEFAULT_BINARY,
        "model_path":             DEFAULT_MODEL,
        "language":               "fr",
        "default_initial_prompt": "",
        "default_temperature":    "0.0",
        "default_beam_size":      "5",
        "corpus_glob":            DEFAULT_CORPUS_GLOB,
    }
    if CONFIG_FILE.exists():
        cfg.read(CONFIG_FILE, encoding="utf-8")
    return cfg


# Markers used to filter Git Bash / MSYS / MinGW entries out of PATH
# when launching whisper-cli — see ``transcribe_sample`` for the rationale.
_UNIX_PATH_MARKERS = (
    "\\mingw64\\", "\\mingw32\\", "\\msys64\\", "\\msys2\\",
    "\\usr\\bin", "\\usr\\local\\bin",
)


def transcribe_sample(
    sample:           Sample,
    *,
    whisper_binary:   str,
    model_path:       str,
    language:         str,
    initial_prompt:   str,
    temperature:      float,
    beam_size:        int,
    extra_cli_args:   list[str] = (),
) -> tuple[str, float]:
    """Run whisper-cli on the sample's WAV; return ``(text, elapsed_seconds)``.

    ``audio_file`` in ``corpus.jsonl`` is recorded relative to the JSONL's
    parent directory (e.g. ``audio/<timestamp>.wav`` under
    ``telemetry/<slug>/``); we resolve it against that parent so the
    bench works regardless of cwd.
    """
    if not sample.audio_file:
        raise RuntimeError(f"Sample {sample.id} has no audio_file recorded")

    audio_path = Path(sample.audio_file)
    if not audio_path.is_absolute():
        audio_path = (Path(sample.source_file).parent / sample.audio_file).resolve()
    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    cmd = [
        whisper_binary,
        "-m",  model_path,
        "-f",  str(audio_path),
        "-l",  language,
        "-tp", str(temperature),
        "-bs", str(beam_size),
        "-np",          # quiet model load + progress
        "-nt",          # no timestamps in stdout
    ]
    if initial_prompt:
        cmd += ["--prompt", initial_prompt]
    if extra_cli_args:
        cmd += list(extra_cli_args)

    # Anchor cwd at the binary's folder and hand it a sanitised PATH so
    # the OS loader picks up the adjacent ggml/libwhisper DLLs cleanly.
    # Background: launched from Git Bash, ``$PATH`` exposes Windows-
    # translated paths to the bundled MinGW runtime which ship libgcc/
    # libstdc++/libwinpthread DLLs that get pulled in ahead of
    # whisper-cli's runtime, producing STATUS_ENTRYPOINT_NOT_FOUND
    # (0xC0000139). PowerShell does not hit this — its PATH is Win-only.
    binary_dir = str(Path(whisper_binary).parent)
    child_env  = os.environ.copy()
    raw_path   = child_env.get("PATH", "")
    win_paths  = [binary_dir] + [
        p for p in raw_path.split(os.pathsep)
        if p and not any(m.lower() in p.lower() for m in _UNIX_PATH_MARKERS)
    ]
    child_env["PATH"] = os.pathsep.join(win_paths)

    t0 = time.time()
    proc = subprocess.run(
        cmd,
        cwd            = binary_dir,
        env            = child_env,
        capture_output = True,
        text           = True,
        encoding       = "utf-8",
        errors         = "replace",
        check          = False,
    )
    elapsed = time.time() - t0

    if proc.returncode != 0:
        raise RuntimeError(
            f"whisper-cli failed (code {proc.returncode}):\n  stderr: {proc.stderr[:500]}"
        )
    return proc.stdout.strip(), elapsed


def run(
    *,
    samples:        list[Sample],
    initial_prompt: str,
    temperature:    float,
    beam_size:      int,
    whisper_binary: str,
    model_path:     str,
    language:       str,
    verbose:        bool,
    extra_cli_args: list[str] = (),
) -> dict:
    details: list[dict] = []

    print(f"=== Whisper bench: model={Path(model_path).name} | temp={temperature} | beam={beam_size} ===")
    if extra_cli_args:
        print(f"=== Extra CLI args: {' '.join(extra_cli_args)} ===")
    print(f"=== Initial prompt: {len(initial_prompt)} chars | Corpus: {len(samples)} samples ===\n")

    total_audio_s   = 0.0
    total_compute_s = 0.0

    for i, sample in enumerate(samples, start=1):
        bracket = bracket_of(sample.duration_seconds) or "—"
        print(
            f"[{i}/{len(samples)}] {sample.id} ({sample.duration_seconds:.1f}s, {bracket})...",
            end=" ", flush=True,
        )
        try:
            text, elapsed = transcribe_sample(
                sample,
                whisper_binary = whisper_binary,
                model_path     = model_path,
                language       = language,
                initial_prompt = initial_prompt,
                temperature    = temperature,
                beam_size      = beam_size,
                extra_cli_args = extra_cli_args,
            )
        except Exception as exc:
            print(f"ERROR: {exc}")
            details.append({
                "id":            sample.id,
                "bracket":       bracket,
                "duration_sec":  round(sample.duration_seconds, 2),
                "audio_file":    sample.audio_file,
                "error":         str(exc),
            })
            continue
        print(f"OK ({elapsed:.1f}s, {len(text)} chars)")

        total_audio_s   += sample.duration_seconds
        total_compute_s += elapsed

        details.append({
            "id":              sample.id,
            "bracket":         bracket,
            "duration_sec":    round(sample.duration_seconds, 2),
            "audio_file":      sample.audio_file,
            "transcribe_sec":  round(elapsed, 1),
            "rtf":             round(elapsed / sample.duration_seconds, 3) if sample.duration_seconds else None,
            "output_chars":    len(text),
            "output_text":     text,
        })

        if verbose:
            print(f"  output: {text[:160]}...\n")

    # Aggregate runtime stats
    realtime_factor = (
        round(total_compute_s / total_audio_s, 3) if total_audio_s else None
    )

    report = {
        "timestamp":      time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "model":          Path(model_path).name,
        "language":       language,
        "initial_prompt": initial_prompt,
        "temperature":    temperature,
        "beam_size":      beam_size,
        "samples":        len(samples),
        "errors":         sum(1 for d in details if "error" in d),
        "total_audio_sec":   round(total_audio_s,   1),
        "total_compute_sec": round(total_compute_s, 1),
        "realtime_factor":   realtime_factor,
        "details":           details,
    }

    REPORTS_DIR.mkdir(parents=True, exist_ok=True)
    json_path = REPORTS_DIR / "last_whisper_run.json"
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    # Compact human-readable companion (handy for quick eyeballing without
    # opening the JSON).
    txt_lines = [
        f"Whisper bench — {report['timestamp']}",
        f"  model       : {report['model']}",
        f"  language    : {report['language']}",
        f"  temperature : {report['temperature']}",
        f"  beam_size   : {report['beam_size']}",
        f"  initial_prompt ({len(initial_prompt)} chars):",
        f"    {initial_prompt or '(empty)'}",
        f"  samples     : {report['samples']} ({report['errors']} errors)",
        f"  audio total : {report['total_audio_sec']:.1f}s",
        f"  compute     : {report['total_compute_sec']:.1f}s (RTF={report['realtime_factor']})",
        "",
        "─" * 70,
    ]
    for d in details:
        if "error" in d:
            txt_lines.append(f"\n[{d['id']} | {d['bracket']} | {d['duration_sec']}s] ERROR: {d['error']}")
            continue
        txt_lines.append(
            f"\n[{d['id']} | {d['bracket']} | {d['duration_sec']}s | "
            f"transcribed in {d['transcribe_sec']}s, RTF={d['rtf']}]"
        )
        txt_lines.append(d["output_text"])
    txt_path = REPORTS_DIR / "last_whisper_run.txt"
    txt_path.write_text("\n".join(txt_lines), encoding="utf-8")

    print(f"\n{'='*60}")
    print(f"DONE: {report['samples']} samples, {report['errors']} errors")
    print(f"  total audio   : {report['total_audio_sec']:.1f}s")
    print(f"  total compute : {report['total_compute_sec']:.1f}s")
    if realtime_factor is not None:
        print(f"  realtime fact.: {realtime_factor:.2f}x")
    print(f"\n  JSON  → {json_path}")
    print(f"  TXT   → {txt_path}")
    return report


def main() -> None:
    cfg = load_config()["whisper_bench"]

    parser = argparse.ArgumentParser(description="Whisper transcription bench (no LLM judge)")
    parser.add_argument("--whisper-binary",      default=cfg["whisper_binary"])
    parser.add_argument("--model-path",          default=cfg["model_path"])
    parser.add_argument("--language",            default=cfg.get("language", "fr"))
    parser.add_argument("--initial-prompt",      default=cfg.get("default_initial_prompt", ""))
    parser.add_argument("--initial-prompt-file", default=None,
                        help="Read initial prompt from a file (overrides --initial-prompt)")
    parser.add_argument("--temperature",         type=float, default=cfg.getfloat("default_temperature"))
    parser.add_argument("--beam-size",           type=int,   default=cfg.getint("default_beam_size"))
    parser.add_argument("--corpus-glob",         default=cfg.get("corpus_glob", DEFAULT_CORPUS_GLOB))
    parser.add_argument("--bracket",             choices=BRACKET_SLUGS, default=None,
                        help="Filter samples by audio duration bracket")
    parser.add_argument("--slug",                default=None, help="Filter samples by profile slug")
    parser.add_argument("--duration-min",        type=float, default=None)
    parser.add_argument("--duration-max",        type=float, default=None)
    parser.add_argument("--limit",               type=int,   default=None,
                        help="Process only the first N samples after filtering")
    parser.add_argument("--extra-cli-args",      default="",
                        help="Extra args passed verbatim to whisper-cli (e.g. \"--no-fallback --no-speech-thold 0.8\")")
    parser.add_argument("--verbose",             action="store_true")
    args = parser.parse_args()

    initial_prompt = args.initial_prompt
    if args.initial_prompt_file:
        prompt_path = Path(args.initial_prompt_file)
        if not prompt_path.is_absolute():
            prompt_path = BENCHMARK_DIR / prompt_path
        initial_prompt = prompt_path.read_text(encoding="utf-8").strip()

    # Resolve any path-bearing arg that came in relative to benchmark/.
    def _abs(p: str) -> str:
        path = Path(p)
        return str(path if path.is_absolute() else (BENCHMARK_DIR / path).resolve())

    whisper_binary = _abs(args.whisper_binary)
    model_path     = _abs(args.model_path)
    corpus_glob    = args.corpus_glob if Path(args.corpus_glob).is_absolute() else str(BENCHMARK_DIR / args.corpus_glob)

    samples = load_corpus(
        corpus_glob,
        duration_min = args.duration_min,
        duration_max = args.duration_max,
        slug         = args.slug,
        bracket      = args.bracket,
    )
    if args.limit is not None and args.limit > 0:
        samples = samples[:args.limit]
    if not samples:
        sys.exit(f"No corpus samples found at {corpus_glob}")

    run(
        samples        = samples,
        initial_prompt = initial_prompt,
        temperature    = args.temperature,
        beam_size      = args.beam_size,
        whisper_binary = whisper_binary,
        model_path     = model_path,
        language       = args.language,
        verbose        = args.verbose,
        extra_cli_args = args.extra_cli_args.split() if args.extra_cli_args else (),
    )


if __name__ == "__main__":
    main()
