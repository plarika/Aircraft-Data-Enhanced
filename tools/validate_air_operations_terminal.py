#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


def check_tokens(
    path: Path,
    tokens: list[str],
    errors: list[str],
) -> None:
    if not path.exists():
        errors.append(f"Missing file: {path}")
        return

    text = path.read_text(encoding="utf-8-sig")

    for token in tokens:
        if token not in text:
            errors.append(
                f"{path.name}: missing token {token!r}"
            )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", default=".")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    src = root / "src"
    errors: list[str] = []

    check_tokens(
        src / "AirOperationsBoardControl.cs",
        [
            "AIR OPERATIONS TERMINAL",
            "PUBLIC BETA",
            "OperationsBoardStatus",
            "StatusTile",
            "ActiveWindow",
            "SessionSelected",
            "RF link",
            "Database",
            "Pipeline",
        ],
        errors,
    )

    check_tokens(
        src / "UiPreferencesStore.cs",
        [
            "ui-preferences.json",
            "SelectedWorkspace",
            "CommandBarVisible",
            "WaterfallVisible",
            "OperationsWindowIndex",
            "File.Move",
            "overwrite:",
        ],
        errors,
    )

    check_tokens(
        src / "ProductAboutDialog.cs",
        [
            "Air Operations Terminal · v0.19.0-beta",
            "Copy system information",
            "RuntimeInformation",
            "UiPreferencesStore.PreferencesPath",
            "Only verified ACARS/AVLC",
        ],
        errors,
    )

    check_tokens(
        src / "AircraftDataPanel.cs",
        [
            "VDL2 Air Operations Terminal v0.19.0-beta",
            '"Operations Board"',
            "_operationsBoardViewActive",
            "_operationsBoard.UpdateStatus",
            "ApplyStoredControlPreferences",
            "SaveUiPreferences",
            "ShowProductAboutDialog",
            "dataViews.SelectedIndex ==\n            0",
            "dataViews.SelectedIndex ==\n                    2",
            "dataViews.SelectedIndex ==\n                    3",
        ],
        errors,
    )

    check_tokens(
        root / "AircraftDataEnhanced.csproj",
        [
            "<Version>0.19.0-beta</Version>",
            "<AssemblyVersion>0.19.0.0</AssemblyVersion>",
            "<FileVersion>0.19.0.0</FileVersion>",
        ],
        errors,
    )

    panel_path = src / "AircraftDataPanel.cs"

    if panel_path.exists():
        text = panel_path.read_text(encoding="utf-8-sig")

        if text.count(
            "CreateWorkspaceTab("
        ) < 9:
            errors.append(
                "AircraftDataPanel.cs: expected the operations-board workspace "
                "plus existing data/detail/control tabs."
            )

        if text.count(
            "_operationsBoard.FilterChanged"
        ) != 1:
            errors.append(
                "AircraftDataPanel.cs: operations-board filter wiring is incorrect."
            )

        if "_sessionViewActive =\n                    dataViews.SelectedIndex ==\n                    1" in text:
            errors.append(
                "AircraftDataPanel.cs: old Active Aircraft tab index remains."
            )

        if "dataViews.SelectedIndex = 2));" in text:
            errors.append(
                "AircraftDataPanel.cs: old Local History tab index remains."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Air Operations Terminal beta validation passed: "
        f"{root}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
