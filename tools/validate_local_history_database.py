#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path

REQUIRED_DATABASE = [
    "class LocalHistoryDatabase",
    "Channel.CreateBounded",
    "BoundedChannelFullMode.Wait",
    "journal_mode=WAL",
    "synchronous=NORMAL",
    "CREATE TABLE IF NOT EXISTS messages",
    "CREATE TABLE IF NOT EXISTS aircraft",
    "INSERT OR IGNORE INTO messages",
    "ON CONFLICT(icao) DO UPDATE",
    "QueryMessagesAsync",
    "QueryAircraftAsync",
    "ClearAsync",
    "VacuumAsync",
    "LocalApplicationData",
    "aircraft-history.sqlite3",
]

REQUIRED_BOOTSTRAP = [
    "ModuleInitializer",
    "AssemblyLoadContext.Default.Resolving",
    "ResolvingUnmanagedDll",
    "runtimes",
    "win-x86",
    "NativeLibrary.Load",
]

REQUIRED_CONTROL = [
    "class LocalHistoryControl",
    "Historical Aircraft",
    "Historical Messages",
    "LocalHistoryQuery",
    "OpenDatabaseFolder",
    "RefreshAsync",
]

REQUIRED_PANEL = [
    "_historyDatabase",
    "_historyControl",
    '"Local History"',
    "RefreshHistoryIfNeeded",
    "TryEnqueue",
    "ClearLocalHistoryAsync",
    "VacuumLocalHistoryAsync",
    "Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta",
]

REQUIRED_PROJECT = [
    '<Version>0.19.0-beta</Version>',
    '<RuntimeIdentifier>win-x86</RuntimeIdentifier>',
    '<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>',
    '<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.18" />',
]

REQUIRED_BUILD = [
    "dotnet restore",
    "-r win-x86",
    "Microsoft.Data.Sqlite.dll",
    "e_sqlite3.dll",
    "robocopy",
]


def check_file(path: Path, required: list[str], errors: list[str]) -> None:
    if not path.exists():
        errors.append(f"Missing file: {path}")
        return
    text = path.read_text(encoding="utf-8-sig")
    for token in required:
        if token not in text:
            errors.append(f"{path.name}: missing token {token!r}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", default=".")
    args = parser.parse_args()

    raw_root = args.root.strip().strip('"').strip("'")
    root = Path(raw_root).resolve()
    src = root / "src"
    errors: list[str] = []

    check_file(src / "LocalHistoryDatabase.cs", REQUIRED_DATABASE, errors)
    check_file(src / "PluginDependencyBootstrap.cs", REQUIRED_BOOTSTRAP, errors)
    check_file(src / "LocalHistoryControl.cs", REQUIRED_CONTROL, errors)
    check_file(src / "AircraftDataPanel.cs", REQUIRED_PANEL, errors)
    check_file(root / "AircraftDataEnhanced.csproj", REQUIRED_PROJECT, errors)
    check_file(root / "BUILD_E_INSTALAR_TUDO.bat", REQUIRED_BUILD, errors)

    database_path = src / "LocalHistoryDatabase.cs"
    if database_path.exists():
        text = database_path.read_text(encoding="utf-8-sig")
        if "Task.Run(WorkerLoopAsync)" not in text:
            errors.append("LocalHistoryDatabase.cs: writer is not started on a background task.")
        if "DELETE FROM aircraft;\n                DELETE FROM messages;" not in text:
            errors.append("LocalHistoryDatabase.cs: clear order must respect the foreign key.")
        if "SELECT *" in text.upper():
            errors.append("LocalHistoryDatabase.cs: SELECT * is forbidden.")

    panel_path = src / "AircraftDataPanel.cs"
    if panel_path.exists():
        text = panel_path.read_text(encoding="utf-8-sig")
        if text.count("_historyDatabase.TryEnqueue(") != 2:
            errors.append(
                "AircraftDataPanel.cs: both verified publication paths must persist to SQLite."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(f"[OK] Embedded local history validation passed: {root.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
