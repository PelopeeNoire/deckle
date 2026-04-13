"""Build corpus.json for restructuration benchmark from raw transcript files."""
import json
import re
import os

BENCHMARK_DIR = os.path.dirname(os.path.abspath(__file__))

RAW_FILES = [
    os.path.join(BENCHMARK_DIR, "raw_prompts.txt"),
    os.path.join(BENCHMARK_DIR, "raw_restructuration.txt"),
]

OUTPUT = os.path.join(BENCHMARK_DIR, "corpus.json")


def split_segments(text: str) -> list[dict]:
    """Split text on --- separators. Detect + chains."""
    # Split on lines that are just --- or --- +
    parts = re.split(r'\n---\s*(\+?)\s*\n', text)

    segments = []
    i = 0
    while i < len(parts):
        content = parts[i].strip()
        if content:
            # Check if this segment chains with the next (+ marker)
            chain = False
            if i + 1 < len(parts) and parts[i + 1].strip() == '+':
                chain = True
            segments.append({"text": content, "chain_next": chain})
        i += 2  # skip the separator capture group

    # Handle last segment (no separator after it)
    if len(parts) % 2 == 1 and parts[-1].strip():
        if not segments or segments[-1]["text"] != parts[-1].strip():
            segments.append({"text": parts[-1].strip(), "chain_next": False})

    return segments


def group_chains(segments: list[dict]) -> list[str]:
    """Merge chained segments into single texts."""
    groups = []
    current = []

    for seg in segments:
        current.append(seg["text"])
        if not seg["chain_next"]:
            groups.append("\n\n".join(current))
            current = []

    # Flush remaining
    if current:
        groups.append("\n\n".join(current))

    return groups


def estimate_duration(text: str) -> int:
    """Rough estimate: ~150 words per minute of speech."""
    words = len(text.split())
    return max(30, int(words / 150 * 60))


def main():
    all_groups = []

    for filepath in RAW_FILES:
        if not os.path.exists(filepath):
            print(f"SKIP: {filepath} not found")
            continue

        with open(filepath, "r", encoding="utf-8") as f:
            text = f.read()

        segments = split_segments(text)
        groups = group_chains(segments)

        source = os.path.basename(filepath).replace("raw_", "").replace(".txt", "")
        print(f"{filepath}: {len(segments)} segments -> {len(groups)} groups")
        for i, g in enumerate(groups):
            words = len(g.split())
            print(f"  Group {i+1}: {words} words, ~{estimate_duration(g)}s")

        all_groups.extend(groups)

    # Build corpus entries
    corpus = []
    for i, text in enumerate(all_groups, 1):
        corpus.append({
            "id": i,
            "duration_sec": estimate_duration(text),
            "profile": "restructuration",
            "text": text,
        })

    with open(OUTPUT, "w", encoding="utf-8") as f:
        json.dump(corpus, f, ensure_ascii=False, indent=2)

    print(f"\nCorpus written: {OUTPUT}")
    print(f"Total samples: {len(corpus)}")
    total_words = sum(len(c["text"].split()) for c in corpus)
    print(f"Total words: {total_words}")


if __name__ == "__main__":
    main()
