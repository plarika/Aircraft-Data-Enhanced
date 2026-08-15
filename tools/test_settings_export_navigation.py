# SPDX-License-Identifier: MIT
from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "src/AircraftDataEnhanced.SdrSharpAdapter/AircraftDataPanel.cs").read_text(encoding="utf-8")

checks = {
    "Settings toggles control center": "var visible =\n                    !controlCenter.Visible;" in source,
    "Settings persists visibility": "_uiPreferences.ControlCenterVisible =\n                    visible;" in source,
    "Export navigation bypasses hidden button": '"export",\n            "Export",\n            (_, _) => HandleExportCommand()' in source,
    "File export bypasses hidden button": '"Export JSONL…",\n                (_, _) => HandleExportCommand()' in source,
    "Direct export handler exists": "private void HandleExportCommand()" in source,
    "Export creates destination directory": "Directory.CreateDirectory(" in source and "RuntimeDataPaths.ExportsDirectory" in source,
    "Hidden button PerformClick removed from export navigation": "(_, _) => _export.PerformClick()" not in source,
    "Export MessageBox owner uses IWin32Window": "IWin32Window owner =" in source and "FindForm() ?? (IWin32Window)this" in source,
    "Invalid Form/control coalescing removed": "FindForm() ?? this" not in source,
}

failed = [name for name, ok in checks.items() if not ok]
if failed:
    for name in failed:
        print(f"[ERRO] {name}")
    raise SystemExit(1)

print("[OK] Settings/export navigation regression passed.")
