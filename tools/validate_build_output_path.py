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
    errors: list[str] = []

    if not bat_path.exists():
        print(f"[ERRO] Missing file: {bat_path}")
        return 1

    text = bat_path.read_text(encoding="utf-8-sig")

    required = [
        r'set "OUTDIR=%CD%\bin\x86\Release\net9.0-windows\win-x86"',
        r'set "DLL=%OUTDIR%\SDRSharp.Plugin.AircraftDataEnhanced.dll"',
        r'if not exist "%OUTDIR%\Microsoft.Data.Sqlite.dll"',
        r'if exist "%OUTDIR%\e_sqlite3.dll"',
        r'if exist "%OUTDIR%\runtimes\win-x86\native\e_sqlite3.dll"',
        r'robocopy "%OUTDIR%" "%DEST%" /E',
    ]

    forbidden = [
        r'for /r "%CD%\bin\Release"',
        r'for %%F in ("%DLL%") do set "OUTDIR=%%~dpF"',
        r'"%OUTDIR%Microsoft.Data.Sqlite.dll"',
        r'"%OUTDIR%e_sqlite3.dll"',
        r'"%OUTDIR%runtimes\win-x86',
    ]

    for token in required:
        if token not in text:
            errors.append(
                f"Missing deterministic output token: {token!r}"
            )

    for token in forbidden:
        if token in text:
            errors.append(
                f"Stale or malformed output token remains: {token!r}"
            )

    if text.count('set "OUTDIR=') != 1:
        errors.append(
            "Expected exactly one OUTDIR assignment."
        )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        "[OK] Windows build-output path validation passed: "
        r"bin\x86\Release\net9.0-windows\win-x86"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
