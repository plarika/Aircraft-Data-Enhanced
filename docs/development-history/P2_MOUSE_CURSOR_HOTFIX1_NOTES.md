# P2 Mouse Cursor Hotfix 1

## Problem
The workspace SplitContainer controls assigned HSplit/VSplit cursors to the whole container. In WinForms, child controls can inherit the parent cursor, causing resize cursors to appear over the spectrum, waterfall, grids, history and diagnostics.

## Fix
Removed the explicit HSplit/VSplit cursor assignments from the SplitContainer instances in AircraftDataPanel. WinForms now handles the resize cursor only at the splitter boundary.

## Scope
Visual/input-only change. No decoder, IQ pipeline, persistence, aircraft lookup, export or message processing code was changed.
