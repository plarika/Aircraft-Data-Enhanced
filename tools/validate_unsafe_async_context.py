#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path


def extract_block(
    text: str,
    start: int,
) -> str:
    opening = text.find("{", start)

    if opening < 0:
        return ""

    depth = 0

    for index in range(opening, len(text)):
        character = text[index]

        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1

            if depth == 0:
                return text[opening:index + 1]

    return ""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "file",
        nargs="?",
        default="src/AircraftDataPanel.cs",
    )
    args = parser.parse_args()

    path = Path(args.file)
    errors: list[str] = []

    if not path.exists():
        print(f"[ERRO] Missing file: {path}")
        return 1

    text = path.read_text(encoding="utf-8-sig")

    declarations = list(
        re.finditer(
            r"\b(?:public|private|internal|protected)\s+"
            r"(?:static\s+)?unsafe\s+"
            r"[^{;]+",
            text,
        )
    )

    if not declarations:
        errors.append(
            "No unsafe methods or constructors were found."
        )

    for declaration in declarations:
        block = extract_block(
            text,
            declaration.start(),
        )

        header = declaration.group(0).strip()

        if re.search(r"\bawait\b", block):
            errors.append(
                f"Unsafe context contains await: {header}"
            )

        if re.search(
            r"\basync\s*(?:\([^)]*\))?\s*=>",
            block,
        ):
            errors.append(
                f"Unsafe context contains an async lambda: {header}"
            )

    required = [
        "_clearHistory.Click +=",
        "ClearHistoryButtonClicked;",
        "private async void ClearHistoryButtonClicked",
        "await ClearLocalHistoryAsync();",
    ]

    for token in required:
        if token not in text:
            errors.append(
                f"Safe clear-history handler missing token {token!r}."
            )

    constructor_match = re.search(
        r"public\s+unsafe\s+AircraftDataPanel\s*\(",
        text,
    )

    if constructor_match:
        constructor_block = extract_block(
            text,
            constructor_match.start(),
        )

        if "_clearHistory.Click += async" in constructor_block:
            errors.append(
                "The clear-history async lambda remains in the unsafe constructor."
            )
    else:
        errors.append(
            "The unsafe AircraftDataPanel constructor was not found."
        )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Unsafe/async context validation passed: "
        f"{path.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
