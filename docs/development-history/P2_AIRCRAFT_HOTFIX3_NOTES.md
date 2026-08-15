# P2 Aircraft Hotfix 3

This hotfix prevents the Aircraft dashboard from remaining indefinitely in
`Online identity: connecting...`.

## Changes

- Delays dashboard clearing for 900 ms so transient empty selections caused by
  DataGridView/session rebuilds do not cancel a valid online lookup.
- Adds a 12-second WinForms watchdog. A stalled lookup is converted into a
  visible `timeout` state instead of remaining in `connecting...`.
- Applies a 10-second timeout to the complete lookup operation, including time
  spent waiting for the network gate.
- Separates aircraft and route network gates so route resolution cannot block
  aircraft identity resolution.
- Uses ADSBdb as a fallback when HexDB does not return usable aircraft data.
- Writes lifecycle diagnostics to
  `%LOCALAPPDATA%\AircraftDataEnhanced\aircraft-lookup.log`.
- Keeps the P2 decoder, IQ pipeline, persistence, waterfall, and soak-tested
  components unchanged.

## Expected states

A lookup must leave `connecting...` within 12 seconds and show one of:

- `Online identity loaded from HexDB.`
- `Online identity loaded from ADSBdb.`
- `Online identity unavailable: timeout`
- another explicit HTTP/network status.
