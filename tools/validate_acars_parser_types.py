#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate ACARS parser string argument types."
    )
    parser.add_argument(
        "file",
        nargs="?",
        default="src/AcarsMessageParser.cs",
    )
    args = parser.parse_args()

    path = Path(args.file)
    errors: list[str] = []

    if not path.exists():
        print(f"[ERRO] Missing file: {path}")
        return 1

    text = path.read_text(encoding="utf-8-sig")

    expected = re.search(
        r"var\s+mode\s*=\s*"
        r"PrintableChar\(frame\[offset\+\+\]\)"
        r"\s*\.ToString\(\)\s*;",
        text,
        re.MULTILINE,
    )

    if not expected:
        errors.append(
            "ACARS mode must be converted from char to string "
            "with PrintableChar(...).ToString()."
        )

    forbidden = re.search(
        r"var\s+mode\s*=\s*"
        r"PrintableChar\(frame\[offset\+\+\]\)\s*;",
        text,
        re.MULTILINE,
    )

    if forbidden:
        errors.append(
            "ACARS mode is still inferred as char."
        )

    create_match = re.search(
        r"private\s+static\s+AcarsMessage\s+Create\s*\("
        r".*?"
        r"string\s+mode\s*,",
        text,
        re.DOTALL,
    )

    if not create_match:
        errors.append(
            "Create(...) must keep its mode argument typed as string."
        )

    create_calls = text.count("Create(") - 1

    if create_calls != 2:
        errors.append(
            f"Expected 2 AcarsMessage Create calls, found {create_calls}."
        )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] ACARS parser type validation passed: "
        f"{path.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
