# P2 Native Dashboard validation

## Scope

The user-provided HTML dashboard was treated as a visual reference only. The final runtime stays native WinForms.

## Confirmed boundaries

- `AircraftDataEnhanced.Core`: unchanged.
- `AircraftDataEnhanced.Persistence`: unchanged.
- `AircraftDataEnhanced.Tests`: unchanged.
- Online aircraft lookup Hotfix 4: preserved.
- No WebView2 package or runtime dependency.
- No HTML, JavaScript, CDN, or web assets loaded by the plugin.

## Visual regression checks

- Reference palette and card styling.
- 200 px navigation rail.
- Overview, Aircraft, Messages, Waterfall, History and Diagnostics navigation.
- Aircraft list-first flow and Back to aircraft list action.
- Responsive Aircraft Details action panel.
- Neutral documentation screenshots.

## Privacy

The release audit must pass before packaging. Generated binaries, logs, SQLite files, JSONL files, captures, user paths and personal identifiers are excluded from the source package.
