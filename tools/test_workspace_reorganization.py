#!/usr/bin/env python3
from pathlib import Path
import sys

root = Path(sys.argv[1] if len(sys.argv) > 1 else '.').resolve()
panel = root / 'src' / 'AircraftDataEnhanced.SdrSharpAdapter' / 'AircraftDataPanel.cs'
text = panel.read_text(encoding='utf-8-sig')

required = [
    'var diagnosticsView =',
    '"Diagnostics",\n                _diagnosticsSummary',
    'dataViews.TabPages.Add(\n            diagnosticsView);',
    'dataViews.SelectedIndex = 4;',
    '4 => "diagnostics"',
    'eventSplit.Panel2Collapsed = true;',
    '_uiPreferences.DetailsVisible = false;',
]
for marker in required:
    if marker not in text:
        raise SystemExit(f'[ERRO] Workspace reorganization marker missing: {marker!r}')

for forbidden in [
    'var diagnosticsTab =',
    'detailsTabs.TabPages.Add(diagnosticsTab);',
]:
    if forbidden in text:
        raise SystemExit(f'[ERRO] Diagnostics still lives in secondary inspector: {forbidden}')

# The inspector must still exist for explicit aircraft/session/message/decoder details.
for tab in ['Aircraft', 'Session', 'Message', 'Decoder']:
    marker = f'"{tab}",' 
    if marker not in text:
        raise SystemExit(f'[ERRO] Detail inspector tab missing: {tab}')

print('[OK] Workspace reorganization regression passed: no side-by-side diagnostics; Diagnostics is a primary full-width workspace and detail inspector remains available on demand.')
