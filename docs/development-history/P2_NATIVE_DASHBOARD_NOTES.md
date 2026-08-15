# P2 Native Dashboard — visual reference integration

This release uses the supplied `aircraft-dashboard.html` only as a design reference.
The runtime interface remains native Windows Forms and does not embed WebView2, JavaScript, or the HTML file.

## Applied visual language

- Reference palette: `#0b0e14`, `#0f131a`, `#151a23`, `#1e2633`, `#e2e8f0`, `#94a3b8`, `#3b82f6`.
- 200 px navigation rail with active blue marker.
- Seven compact live metric cards.
- Aircraft list and full Aircraft Details workflow.
- Back-to-list navigation from Aircraft Details.
- 76/24 details/action layout with responsive stacking below 840 px.
- Native Messages, Waterfall, History and Diagnostics pages.
- No WebView2 dependency and no external web assets.

## Functional boundary

The change is limited to `AircraftDataEnhanced.SdrSharpAdapter`. Core decoding, IQ processing, persistence and online lookup logic remain unchanged.
