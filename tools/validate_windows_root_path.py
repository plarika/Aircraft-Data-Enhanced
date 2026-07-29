#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", default=".")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    bat_path = root / "BUILD_E_INSTALAR_TUDO.bat"
    validator_path = root / "tools" / "validate_local_history_database.py"

    errors: list[str] = []

    if not bat_path.exists():
        errors.append(f"Missing file: {bat_path}")
    else:
        text = bat_path.read_text(encoding="utf-8-sig")

        safe = (
            'validate_local_history_database.py" "%~dp0."'
        )
        unsafe = (
            'validate_local_history_database.py" "%~dp0"'
        )

        if safe not in text:
            errors.append(
                'Safe root argument "%~dp0." is missing.'
            )

        if unsafe in text:
            errors.append(
                'Unsafe root argument "%~dp0" remains.'
            )

    if not validator_path.exists():
        errors.append(f"Missing file: {validator_path}")
    else:
        text = validator_path.read_text(encoding="utf-8-sig")

        for token in (
            "args.root.strip()",
            ".strip('\"')",
            ".resolve()",
        ):
            if token not in text:
                errors.append(
                    f"Root normalization missing token {token!r}."
                )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        "[OK] Windows root-path regression passed: "
        'BAT uses "%~dp0." and Python normalizes accidental quotes.'
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
