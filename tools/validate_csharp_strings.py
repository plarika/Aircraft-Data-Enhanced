#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


def validate_file(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8-sig")
    errors: list[str] = []

    index = 0
    line = 1
    state = "code"
    literal_start = 0

    while index < len(text):
        ch = text[index]
        nxt = text[index + 1] if index + 1 < len(text) else ""
        third = text[index + 2] if index + 2 < len(text) else ""

        if state == "code":
            if ch == "/" and nxt == "/":
                state = "line_comment"
                index += 1
            elif ch == "/" and nxt == "*":
                state = "block_comment"
                index += 1
            elif ch == "'":
                state = "char"
                literal_start = line
            elif ch == "@" and nxt == '"':
                state = "verbatim"
                literal_start = line
                index += 1
            elif ch == "$" and nxt == '"':
                state = "regular"
                literal_start = line
                index += 1
            elif ch in {"$", "@"} and nxt in {"$", "@"} and third == '"':
                state = "verbatim"
                literal_start = line
                index += 2
            elif ch == '"' and text[index:index + 3] == '"""':
                state = "raw"
                literal_start = line
                index += 2
            elif ch == '"':
                state = "regular"
                literal_start = line

        elif state == "line_comment":
            if ch == "\n":
                state = "code"

        elif state == "block_comment":
            if ch == "*" and nxt == "/":
                state = "code"
                index += 1

        elif state == "char":
            if ch == "\\":
                index += 1
            elif ch == "'":
                state = "code"
            elif ch == "\n":
                errors.append(
                    f"{path}: newline in character literal started at line "
                    f"{literal_start}"
                )
                state = "code"

        elif state == "regular":
            if ch == "\\":
                index += 1
            elif ch == '"':
                state = "code"
            elif ch == "\n":
                errors.append(
                    f"{path}: newline in regular string started at line "
                    f"{literal_start}"
                )
                state = "code"

        elif state == "verbatim":
            if ch == '"' and nxt == '"':
                index += 1
            elif ch == '"':
                state = "code"

        elif state == "raw":
            if text[index:index + 3] == '"""':
                state = "code"
                index += 2

        if ch == "\n":
            line += 1

        index += 1

    if state in {"regular", "verbatim", "raw", "char"}:
        errors.append(
            f"{path}: unterminated {state} literal near line {literal_start}"
        )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Detect accidental physical newlines in C# string literals."
    )
    parser.add_argument("root", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.root)
    errors: list[str] = []

    for path in sorted(root.rglob("*.cs")):
        errors.extend(validate_file(path))

    if errors:
        print("\n".join(errors))
        return 1

    print(f"[OK] C# string validation passed: {root.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
