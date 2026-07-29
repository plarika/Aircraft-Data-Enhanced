#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED_DASHBOARD = [
    "AircraftDashboardControl",
    "AircraftMetadataService",
    "SetMessage",
    "LookupAircraftAsync",
    "LookupRouteAsync",
    "Aircraft details",
    "Live map",
    "Search flight",
    "Copy details",
    "Refresh online",
]

REQUIRED_SERVICE = [
    "https://hexdb.io/api/v1/aircraft/",
    "https://hexdb.io/api/v1/route/icao/",
    "ConcurrentDictionary",
    "SemaphoreSlim",
    "TimeSpan.FromSeconds(6)",
    "TimeSpan.FromHours(24)",
    "TimeSpan.FromMinutes(15)",
    "ResponseHeadersRead",
    "CancellationToken",
]

REQUIRED_PANEL = [
    "AircraftDashboardControl",
    "CreateWorkspaceTab",
    "ApplyModernGridStyle",
    "_aircraftDashboard.SetMessage",
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.src)
    errors: list[str] = []

    paths = {
        "AircraftDashboardControl.cs": REQUIRED_DASHBOARD,
        "AircraftMetadataService.cs": REQUIRED_SERVICE,
        "AircraftDataPanel.cs": REQUIRED_PANEL,
    }

    for filename, tokens in paths.items():
        path = root / filename

        if not path.exists():
            errors.append(f"Missing file: {path}")
            continue

        text = path.read_text(encoding="utf-8-sig")

        for token in tokens:
            if token not in text:
                errors.append(
                    f"{filename}: missing token {token!r}"
                )

    service_path = root / "AircraftMetadataService.cs"

    if service_path.exists():
        text = service_path.read_text(encoding="utf-8-sig")

        if text.count("new HttpClient") != 1:
            errors.append(
                "AircraftMetadataService.cs: expected one reusable HttpClient."
            )

        if "Task.Run" in text:
            errors.append(
                "AircraftMetadataService.cs: Task.Run must not wrap HTTP calls."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Aircraft dashboard and metadata validation passed: "
        f"{root.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
