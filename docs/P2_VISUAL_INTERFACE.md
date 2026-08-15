# Aircraft Data Enhanced P2 v1.0.0 — final visual interface

This refresh changes only the `AircraftDataEnhanced.SdrSharpAdapter` WinForms presentation layer. The validated IQ pipeline, decoders, persistence services and soak-test implementation remain unchanged.

## Visual structure

- Product header with `P2 v1.0.0`, live state and UTC clock.
- Operational cards for frequency, signal, active aircraft, verified messages, IQ queue, database and JSONL export.
- Wide navigation rail with icons and the sections Overview, Aircraft, Messages, Waterfall, History, Diagnostics, Settings, Export and About.
- Full-width Aircraft page with an ICAO24 identity header, online-provider status badge, structured aircraft data card and vertical action panel.
- Messages page that retains the live spectrum/waterfall while giving the verified-message table the full lower workspace.
- Waterfall page that expands the spectrum display to the complete workspace.
- Dark owner-drawn tabs, tables, menus, inputs and status bars.

## Navigation behaviour

- **Overview:** spectrum, operations board and details together.
- **Aircraft:** Aircraft Details occupies the main workspace.
- **Messages:** spectrum plus the verified-message table.
- **Waterfall:** spectrum and waterfall occupy the full workspace.
- **History:** local history occupies the main workspace.
- **Diagnostics:** opens decoder controls, channel health and detailed decoder information.

## State colours

- Blue: active signal, selection and processing.
- Green: healthy database, successful online identity and recording.
- Amber: waiting, timeout or warning state.
- Red: fault or dropped-data state.
- Purple: verified message activity.

## Runtime behaviour

The interface performs no heavy work inside the IQ callback. Cards and status indicators continue to update through the existing 500 ms WinForms timer using snapshots already collected by the P2 runtime.

## Screenshots

Neutral visual previews are stored in `docs/screenshots/`. They intentionally contain generic sample data and no personal information. Actual runtime captures should be taken after installation for a public release page.

## Required validation after building

1. Run the source validators and C# regression suite.
2. Build the adapter for `Release`, `x86`, `win-x86`.
3. Install with `BUILD_E_INSTALAR_TUDO.bat`.
4. Verify Overview, Aircraft, Messages, Waterfall and narrow-panel behaviour.
5. Confirm ADSBdb aircraft identity, HexDB fallback and route lookup.

The completed 24-hour P2 soak remains applicable because this change does not modify Core, Persistence, the IQ pipeline or the exporter.
