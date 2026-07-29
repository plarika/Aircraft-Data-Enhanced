#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


def extract_calls(text: str, method_name: str) -> list[str]:
    calls: list[str] = []
    needle = method_name + "("
    search_from = 0

    while True:
        start = text.find(needle, search_from)
        if start < 0:
            break

        prefix = text[max(0, start - 120):start]
        if "private PendingCapture?" in prefix:
            search_from = start + len(needle)
            continue

        index = start + len(method_name)
        depth = 0
        state = "code"

        while index < len(text):
            ch = text[index]
            nxt = text[index + 1] if index + 1 < len(text) else ""

            if state == "code":
                if ch == "/" and nxt == "/":
                    state = "line_comment"
                    index += 1
                elif ch == "/" and nxt == "*":
                    state = "block_comment"
                    index += 1
                elif ch == '"':
                    state = "string"
                elif ch == "'":
                    state = "char"
                elif ch == "(":
                    depth += 1
                elif ch == ")":
                    depth -= 1
                    if depth == 0:
                        calls.append(text[start:index + 1])
                        search_from = index + 1
                        break

            elif state == "line_comment":
                if ch == "\n":
                    state = "code"

            elif state == "block_comment":
                if ch == "*" and nxt == "/":
                    state = "code"
                    index += 1

            elif state == "string":
                if ch == "\\":
                    index += 1
                elif ch == '"':
                    state = "code"

            elif state == "char":
                if ch == "\\":
                    index += 1
                elif ch == "'":
                    state = "code"

            index += 1
        else:
            raise RuntimeError(
                f"Unterminated call to {method_name}."
            )

    return calls


def validate(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8-sig")
    calls = extract_calls(text, "FinalizeCaptureLocked")
    errors: list[str] = []

    if len(calls) != 4:
        errors.append(
            f"Expected 4 FinalizeCaptureLocked calls, found {len(calls)}."
        )

    expected_flags = [True, False, False, False]
    actual_flags: list[bool] = []

    for index, call in enumerate(calls, start=1):
        if "limited:" not in call:
            errors.append(
                f"Call {index} is missing named argument 'limited'."
            )

        if "continuousOrInterference:" not in call:
            errors.append(
                f"Call {index} is missing named argument "
                "'continuousOrInterference'."
            )

        if "detector" not in call:
            errors.append(
                f"Call {index} is missing detector argument."
            )

        if "continuousOrInterference: true" in call:
            actual_flags.append(True)
        elif "continuousOrInterference: false" in call:
            actual_flags.append(False)
        else:
            actual_flags.append(False)

    if len(actual_flags) == 4 and actual_flags != expected_flags:
        errors.append(
            f"Unexpected continuous/interference flags: {actual_flags}; "
            f"expected {expected_flags}."
        )

    declaration_tokens = [
        "private PendingCapture? FinalizeCaptureLocked(",
        "string completionReason",
        "bool limited",
        "bool continuousOrInterference",
        "BurstDetectorSnapshot detector",
    ]

    for token in declaration_tokens:
        if token not in text:
            errors.append(
                f"Method declaration is missing token: {token}"
            )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate FinalizeCaptureLocked invocations."
    )
    parser.add_argument(
        "source",
        nargs="?",
        default="src/IqCaptureManager.cs",
    )
    args = parser.parse_args()

    path = Path(args.source)
    errors = validate(path)

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    calls = extract_calls(
        path.read_text(encoding="utf-8-sig"),
        "FinalizeCaptureLocked",
    )

    print(
        f"[OK] FinalizeCaptureLocked validation passed: "
        f"{path.resolve()} ({len(calls)} calls)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
