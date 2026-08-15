# P2 Settings + Export Hotfix 1

## Scope

This hotfix changes only native WinForms navigation and export UI behavior. The IQ pipeline, VDL2/ACARS decoding, persistence, aircraft metadata providers and soak-tested Core remain unchanged.

## Settings

The left navigation **Settings** item now toggles the visibility of the upper Control Center containing **System status / Decoder / Waterfall / Data**. The choice is persisted through `UiPreferences.ControlCenterVisible`. Diagnostics no longer forces this panel visible.

## Export

The left navigation **Export** item and the File > Export JSONL command now call the export routine directly. They no longer depend on `Button.PerformClick()`, which could be ignored while the real export button lived inside a hidden Control Center.

The JSONL export routine now ensures the export directory exists and uses the SDR# host form as the dialog owner when available.
