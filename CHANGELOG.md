# Changelog

All notable changes to Aircraft Data Enhanced are documented here.

## [1.0.0] - 2026-08-14

### Stable core

- Split the solution into Core, Persistence, SDR# Adapter and Tests.
- Added exact SDR# SDK fingerprint validation for the stable host.
- Added bounded asynchronous IQ processing with pooled buffers and overload metrics.
- Added validated VDL2 header/payload, Reed-Solomon, HDLC/FCS, AVLC and ACARS regression coverage.
- Added SQLite history and bounded asynchronous JSONL export.
- Added synthetic/golden vectors and formal soak-test tooling.

### Native dashboard

- Reworked the SDR# interface into a native dark dashboard with Overview, Aircraft, Messages, Waterfall, History and Diagnostics workspaces.
- Added full-width responsive workspaces and compact lower-panel layout.
- Added active-aircraft list/details workflow and local-history filtering.
- Added responsive diagnostics cards and improved table readability.
- Corrected splitter cursor inheritance so resize cursors remain scoped to splitter regions.

### Aircraft enrichment

- Added online aircraft identity enrichment.
- ADSBdb is the primary provider with HexDB fallback.
- Added independent provider timeouts, failure caching and cancellation-safe refresh behaviour.

### Settings and export

- Settings now toggles the top status strip (`System Status`, `Decoder`, `Waterfall`, `Data`) and persists the choice.
- Export navigation now controls JSONL export directly, including when the status strip is hidden.
- Corrected export-owner typing and nullable-history warnings found during the final Windows build.

### Validation

- Formal 24-hour P2 soak passed with no dropped or faulted IQ blocks and matching JSONL/SQLite record counts.
- Final native dashboard build and installation completed successfully on the validated SDR# revision 1921 environment.
- Public-source audit and release-manifest validation included.
