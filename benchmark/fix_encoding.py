"""Fix UTF-8 text that was double-encoded (UTF-8 → Latin-1 → UTF-8)."""
import re
import sys

# Explicit character replacements for common French garbled patterns
# These are UTF-8 bytes decoded as Latin-1 then re-encoded as UTF-8
REPLACEMENTS = [
    ('\u00c3\u00a7', 'ç'),  # C3 A7
    ('\u00c3\u00a9', 'é'),  # C3 A9
    ('\u00c3\u00a8', 'è'),  # C3 A8
    ('\u00c3\u00aa', 'ê'),  # C3 AA
    ('\u00c3\u00ab', 'ë'),  # C3 AB
    ('\u00c3\u00af', 'ï'),  # C3 AF
    ('\u00c3\u00ae', 'î'),  # C3 AE
    ('\u00c3\u00a2', 'â'),  # C3 A2
    ('\u00c3\u00b4', 'ô'),  # C3 B4
    ('\u00c3\u00b9', 'ù'),  # C3 B9
    ('\u00c3\u00bb', 'û'),  # C3 BB
    ('\u00c3\u00bc', 'ü'),  # C3 BC
    ('\u00c3\u00b6', 'ö'),  # C3 B6
    ('\u00c3\u0089', 'É'),  # C3 89
    ('\u00c3\u0080', 'À'),  # C3 80
    ('\u00c5\u0093', 'œ'),  # C5 93
]

def fix_encoding(text: str) -> str:
    for garbled, correct in REPLACEMENTS:
        text = text.replace(garbled, correct)
    # Handle 'à' (C3 A0): Ã followed by NBSP (U+00A0) or regular space
    # NBSP case: direct replacement
    text = text.replace('\u00c3\u00a0', 'à')
    # Space case: Ã + space → à (Ã never appears naturally in French)
    text = re.sub(r'\u00c3 ', 'à', text)
    return text


if __name__ == "__main__":
    test = "c\u00c3\u00a7a, d\u00c3\u00a9j\u00c3\u00a0 pas mal, \u00c3\u00aatre \u00c3\u00a0 c\u00c3\u00b4t\u00c3\u00a9"
    result = fix_encoding(test)
    print(f"Input:  {test}")
    print(f"Output: {result}")
    print(f"Expected: ça, déjà pas mal, être à côté")
    print(f"Match: {result == 'ça, déjà pas mal, être à côté'}")
