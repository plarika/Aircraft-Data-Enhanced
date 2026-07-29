#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate known D8PSK analyzer source hazards."
    )
    parser.add_argument(
        "source",
        nargs="?",
        default="src/D8pskSymbolAnalyzer.cs",
    )
    args = parser.parse_args()

    path = Path(args.source)
    text = path.read_text(encoding="utf-8-sig")
    errors: list[str] = []

    if re.search(
        r"FormattableString\.Invariant\s*\(\s*"
        r"\$\"[\s\S]*?\"\s*\+\s*\$?\"",
        text,
    ):
        errors.append(
            "FormattableString.Invariant contains concatenated strings. "
            "Pass one interpolated string instead."
        )

    if re.search(r"(?m)^\s*bit_preview\s*,\s*$", text):
        errors.append(
            "Anonymous object uses undefined identifier 'bit_preview'. "
            "Use 'bit_preview = bitPreview'."
        )

    required = [
        "bit_preview = bitPreview",
        "FormattableString.Invariant(",
        "D8pskSymbolAnalyzer",
    ]

    for token in required:
        if token not in text:
            errors.append(f"Required token missing: {token}")

    if text.count("FormattableString.Invariant(") != 2:
        errors.append(
            "Unexpected number of FormattableString.Invariant calls; "
            "expected exactly 2."
        )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(f"[OK] D8PSK analyzer validation passed: {path.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
