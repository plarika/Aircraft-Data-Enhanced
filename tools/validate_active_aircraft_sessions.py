#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED_STORE = [
    "AircraftSessionSnapshot",
    "AircraftSessionStore",
    "TryAdd(",
    "ActiveCount(",
    "Snapshot(",
    "TimeSpan.FromHours(24)",
    "RecentMessages",
    "MessageCount",
    "PruneLocked",
    "LatestMessage",
]

REQUIRED_CONTROL = [
    "ActiveAircraftSessionsControl",
    "Active: 15 min",
    "SESSION MESSAGE HISTORY",
    "SessionSelected",
    "MessageSelected",
    "SelectionCleared",
    "FilterChanged",
    "LookupAircraftAsync",
    "_metadataPending.Count >=",
    "MessageCount",
    "FirstSeen",
    "LastSeen",
]

REQUIRED_PANEL = [
    "Air Operations Terminal v0.19.0-beta",
    "ActiveAircraftSessionsControl",
    "AircraftSessionStore",
    'CreateWorkspaceTab(\n                "Active Aircraft"',
    'CreateWorkspaceTab(\n                "Session"',
    "RefreshSessionsIfNeeded",
    "_compactAircraftStatus",
    "ShowAircraftSession",
    "ShowSessionMessage",
    "ClearAircraftSessionSelection",
    "Show active aircraft sessions",
    "Active aircraft sessions",
    "TimeSpan.FromMinutes(15)",
    "_lastSessionUiRefreshTicks",
]

REQUIRED_METADATA = [
    "ConfigureAwait(",
    "SafeReleaseNetworkGate",
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    parser.add_argument(
        "--project",
        default="AircraftDataEnhanced.csproj",
    )
    args = parser.parse_args()

    root = Path(args.src)
    project = Path(args.project)
    errors: list[str] = []

    files = {
        "AircraftSessionStore.cs": REQUIRED_STORE,
        "ActiveAircraftSessionsControl.cs": REQUIRED_CONTROL,
        "AircraftDataPanel.cs": REQUIRED_PANEL,
        "AircraftMetadataService.cs": REQUIRED_METADATA,
    }

    for filename, tokens in files.items():
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

    panel_path = root / "AircraftDataPanel.cs"

    if panel_path.exists():
        panel = panel_path.read_text(encoding="utf-8-sig")

        if panel.count("_sessionStore.TryAdd(") != 2:
            errors.append(
                "AircraftDataPanel.cs: expected session updates in both "
                "accepted-message publication paths."
            )

        if panel.count("RefreshSessionsIfNeeded();") != 3:
            errors.append(
                "AircraftDataPanel.cs: expected refresh calls from the "
                "UI timer and both operations/session view-activation paths."
            )

        if "Task.Run" in panel:
            errors.append(
                "AircraftDataPanel.cs: session UI must not create Task.Run work."
            )

    control_path = root / "ActiveAircraftSessionsControl.cs"

    if control_path.exists():
        control = control_path.read_text(encoding="utf-8-sig")

        if "Task.Run" in control:
            errors.append(
                "ActiveAircraftSessionsControl.cs: metadata lookups must use "
                "native async I/O, not Task.Run."
            )

        if control.count("AircraftMetadataService") != 1:
            errors.append(
                "ActiveAircraftSessionsControl.cs: expected one reusable "
                "metadata service field."
            )

    project_path = project

    if not project_path.is_absolute():
        project_path = root.parent / project_path

    if not project_path.exists():
        errors.append(f"Missing project file: {project_path}")
    else:
        project_text = project_path.read_text(
            encoding="utf-8-sig"
        )

        if "<Version>0.19.0-beta</Version>" not in project_text:
            errors.append(
                "AircraftDataEnhanced.csproj: v0.19.0-beta not set."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Active aircraft sessions validation passed: "
        f"{root.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
