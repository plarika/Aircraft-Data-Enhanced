#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED_LOOKUP = [
    "AircraftOnlineProvider",
    "Planespotters",
    "AdsbExchange",
    "FlightSearch",
    "https://www.planespotters.net/hex/",
    "https://globe.adsbexchange.com/?icao=",
    "BuildFlightSearchUrl",
    "UseShellExecute = true",
]

FORBIDDEN_LOOKUP = [
    "AirNavRadar",
    "airnavradar.com",
]

REQUIRED_PANEL = [
    "DataGridViewLinkColumn",
    "AddIcaoLinkColumn",
    "GridCellContentClick",
    "GridCellDoubleClick",
    "GridCellMouseDown",
    "BuildAircraftOnlineMenu",
    "OpenSelectedAircraftOnline",
    "VDL2 Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta",
    "AircraftOnlineProvider.Planespotters",
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.src)
    errors: list[str] = []

    lookup_path = root / "AircraftOnlineLookup.cs"
    panel_path = root / "AircraftDataPanel.cs"

    if not lookup_path.exists():
        errors.append(f"Missing file: {lookup_path}")
    else:
        text = lookup_path.read_text(encoding="utf-8-sig")

        for token in REQUIRED_LOOKUP:
            if token not in text:
                errors.append(
                    f"AircraftOnlineLookup.cs: missing token {token!r}"
                )

        for token in FORBIDDEN_LOOKUP:
            if token in text:
                errors.append(
                    f"AircraftOnlineLookup.cs: forbidden stale token {token!r}"
                )

    if not panel_path.exists():
        errors.append(f"Missing file: {panel_path}")
    else:
        text = panel_path.read_text(encoding="utf-8-sig")

        for token in REQUIRED_PANEL:
            if token not in text:
                errors.append(
                    f"AircraftDataPanel.cs: missing token {token!r}"
                )

        if text.count("AddIcaoLinkColumn();") != 1:
            errors.append(
                "AircraftDataPanel.cs: expected one ICAO link column."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Reliable aircraft online lookup validation passed: "
        f"{root.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
