#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


PAIRS = {
    "(": ")",
    "[": "]",
    "{": "}",
}


def validate(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8-sig")
    stack: list[tuple[str, int]] = []
    errors: list[str] = []

    state = "code"
    line = 1
    index = 0

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
            elif ch == "@" and nxt == '"':
                state = "verbatim"
                index += 1
            elif ch == "$" and nxt == '"':
                state = "regular"
                index += 1
            elif ch in {"@", "$"} and nxt in {"@", "$"} and third == '"':
                state = "verbatim"
                index += 2
            elif text[index:index + 3] == '"""':
                state = "raw"
                index += 2
            elif ch == '"':
                state = "regular"
            elif ch in PAIRS:
                stack.append((ch, line))
            elif ch in PAIRS.values():
                if not stack:
                    errors.append(
                        f"{path}: unexpected {ch!r} at line {line}"
                    )
                else:
                    opening, opening_line = stack.pop()
                    expected = PAIRS[opening]
                    if ch != expected:
                        errors.append(
                            f"{path}: {opening!r} from line {opening_line} "
                            f"closed by {ch!r} at line {line}"
                        )

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

        elif state == "regular":
            if ch == "\\":
                index += 1
            elif ch == '"':
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

    for opening, opening_line in reversed(stack):
        errors.append(
            f"{path}: unclosed {opening!r} from line {opening_line}"
        )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.root)
    errors: list[str] = []

    for path in sorted(root.rglob("*.cs")):
        errors.extend(validate(path))

    if errors:
        print("\n".join(f"[ERRO] {error}" for error in errors))
        return 1

    print(f"[OK] C# structural validation passed: {root.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
