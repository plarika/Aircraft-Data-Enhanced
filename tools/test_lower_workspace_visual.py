#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
adapter = ROOT / "src" / "AircraftDataEnhanced.SdrSharpAdapter"
history = (adapter / "LocalHistoryControl.cs").read_text(encoding="utf-8-sig")
diagnostics = (adapter / "DiagnosticsSummaryControl.cs").read_text(encoding="utf-8-sig")
panel = (adapter / "AircraftDataPanel.cs").read_text(encoding="utf-8-sig")

required_history = [
    "LOCAL HISTORY",
    "Search the embedded SQLite archive",
    "ApplyResponsiveColumns",
    "SetColumnVisible",
    "Aircraft  {_currentAircraft.Count:N0}",
    "Messages  {_currentMessages.Count:N0}",
    "ModernTabControl",
]
for token in required_history:
    assert token in history, f"missing responsive history token: {token}"

required_diagnostics = [
    "SYSTEM DIAGNOSTICS",
    "IQ QUEUE USAGE",
    "PROCESSING",
    "MEMORY",
    "PERSISTENCE",
    "EXPORT",
    "WATERFALL",
    "HEALTHY",
    "ColumnCount = 2",
    "ApplyMetricsLayout",
    "metricsLayoutUpdating",
    "metrics.Controls.Clear();",
    "metrics.GrowStyle = TableLayoutPanelGrowStyle.AddRows",
    "metrics.ResumeLayout(performLayout: true)",
]
for token in required_diagnostics:
    assert token in diagnostics, f"missing compact diagnostics token: {token}"

clear_index = diagnostics.index("metrics.Controls.Clear();")
column_index = diagnostics.index("metrics.ColumnCount = compact ? 1 : 2;")
assert clear_index < column_index, (
    "diagnostics layout must clear controls before reducing table capacity"
)

required_layout = [
    "SplitterWidth = 7",
    "tableShare = verticalAvailable >= 1350",
    "Appearance = TabAppearance.FlatButtons",
    "ItemSize = new Size(92, 31)",
]
for token in required_layout:
    assert token in panel, f"missing balanced lower-workspace token: {token}"

print("[OK] Lower workspace visual regression passed: responsive history, compact diagnostics and balanced split layout.")
