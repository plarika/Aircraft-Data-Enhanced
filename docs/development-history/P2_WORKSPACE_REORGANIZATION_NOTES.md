# P2 workspace reorganization

This visual-only refinement removes the persistent side-by-side inspector from the lower workspace.

## New layout

- Overview: spectrum/waterfall above, Operations Board full width below.
- Aircraft: active-aircraft workspace full width. Aircraft details still open full width on explicit activation.
- Messages: verified messages full width below the spectrum/waterfall.
- History: Local History full width.
- Diagnostics: dedicated primary workspace full width; it is no longer embedded beside History/Overview.
- Detail inspector: Aircraft / Session / Message / Decoder remains available only for explicit detail flows.

## Functional scope

No changes were made to Core, Persistence, IQ processing, VDL2/ACARS decoding, SQLite, JSONL, ADSBdb/HexDB lookup, or soak-tested runtime logic.
