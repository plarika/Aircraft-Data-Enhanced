#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.src)
    panel_path = root / "AircraftDataPanel.cs"
    spec_path = root / "SpectrumWaterfallControl.cs"

    errors: list[str] = []

    if not panel_path.exists():
        errors.append(f"Missing file: {panel_path}")
    else:
        text = panel_path.read_text(encoding="utf-8-sig")

        required_panel = [
            '_uiPreferences.CommandBarVisible',
            '"Command bar"',
            'viewMenu.DropDownItems.Add(showCommandBar);',
            'Control commandBar,',
            'VDL2 Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta',
        ]

        for token in required_panel:
            if token not in text:
                errors.append(
                    f"AircraftDataPanel.cs: missing token {token!r}"
                )

    if not spec_path.exists():
        errors.append(f"Missing file: {spec_path}")
    else:
        text = spec_path.read_text(encoding="utf-8-sig")

        required_spec = [
            '(rows.Length - 1 - rowIndex) *',
        ]

        for token in required_spec:
            if token not in text:
                errors.append(
                    f"SpectrumWaterfallControl.cs: missing token {token!r}"
                )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Waterfall direction and top-layout validation passed: "
        f"{root.resolve()}"
    )
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
