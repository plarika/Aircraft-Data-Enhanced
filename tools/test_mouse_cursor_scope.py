from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
p=root/'src'/'AircraftDataEnhanced.SdrSharpAdapter'/'AircraftDataPanel.cs'
s=p.read_text(encoding='utf-8-sig')
pat=r'Cursor\s*=\s*Cursors\.(?:H|V)Split'
if re.search(pat,s):
    raise SystemExit('[FAIL] Split cursor assigned to whole SplitContainer')
print('[OK] Split cursors are not assigned to whole workspace containers.')
