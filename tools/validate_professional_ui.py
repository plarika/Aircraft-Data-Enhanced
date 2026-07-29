#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED_PANEL = [
    "MenuStrip",
    "StatusStrip",
    "BuildApplicationMenu",
    "BuildControlCenter",
    "Control Center",
    "System status",
    "Channel monitor",
    "Automatic VDL2 decoding",
    "Preset: Weak signals",
    "VDL2 Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta",
    "_compactIqStatus",
    "_compactDecoderStatus",
    "_compactMessageStatus",
    "_compactPipelineStatus",
    "_uiPreferences.ControlCenterVisible",
    "_uiPreferences.ChannelMonitorVisible",
    "mainSplit.Panel1Collapsed",
    "eventSplit.Panel2Collapsed",
]

FORBIDDEN_PANEL = [
    "var header = new TableLayoutPanel",
    'Text = "Aircraft Data Enhanced — VDL2 Live Pipeline Aircraft Dashboard v0.15.0"',
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    panel_path = Path(args.src) / "AircraftDataPanel.cs"
    errors: list[str] = []

    if not panel_path.exists():
        errors.append(f"Missing file: {panel_path}")
    else:
        text = panel_path.read_text(encoding="utf-8-sig")

        for token in REQUIRED_PANEL:
            if token not in text:
                errors.append(f"AircraftDataPanel.cs: missing token {token!r}")

        for token in FORBIDDEN_PANEL:
            if token in text:
                errors.append(f"AircraftDataPanel.cs: obsolete UI token remains {token!r}")

        if text.count("BuildApplicationMenu(") != 2:
            errors.append(
                "AircraftDataPanel.cs: expected one application-menu call and declaration."
            )

        if text.count("BuildControlCenter(") != 2:
            errors.append(
                "AircraftDataPanel.cs: expected one control-center call and declaration."
            )

        if text.count("new StatusStrip") != 1:
            errors.append("AircraftDataPanel.cs: expected one compact status strip.")

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(f"[OK] Professional workspace UI validation passed: {panel_path.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
