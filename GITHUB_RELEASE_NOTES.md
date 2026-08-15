# Aircraft Data Enhanced v1.0.0 Stable

Aircraft Data Enhanced v1.0.0 is the first stable P2 release of the receive-only VDL2/AVLC/ACARS plugin for SDR# .NET 9 x86.

## Release highlights

- Native SDR# dashboard with Overview, Aircraft, Messages, Waterfall, History and Diagnostics.
- VDL2/AVLC/ACARS decoder with regression-tested header, FEC, HDLC/FCS and aircraft ICAO handling.
- Active-aircraft sessions and verified-message workflow.
- SQLite local history and JSONL export.
- ADSBdb aircraft identity enrichment with HexDB fallback.
- Settings toggle for the top status strip.
- Responsive History/Diagnostics layouts and corrected mouse splitter cursors.
- Exact SDR# SDK fingerprint verification for the validated production revision 1921 environment.

## Stability gate

The P2 core completed a formal 24-hour soak with 1,286,810 IQ blocks processed, zero IQ drops/faults, 160,851 valid JSONL records and 160,851 matching SQLite messages. SQLite integrity checks passed and the waterfall reported zero dropped frames.

Subsequent presentation-layer refinements were built and used interactively in SDR# without requiring another core soak.

## Source release note

This public source archive does **not** redistribute `SDRSharp.Common.dll` or `SDRSharp.Radio.dll`. Obtain the official **SDR# SDK for Plugin Developers** from Airspy and place the required DLLs in `lib/` before building.

## Upgrade

Close SDR#, build/install this release using `BUILD_E_INSTALAR_TUDO.bat`, then reopen SDR#. Runtime history/export files are stored outside the source tree and are not included in this archive.

## Safety

Receive-only research/hobby software. Not for ATC, navigation, dispatch, emergency or safety-critical use.
