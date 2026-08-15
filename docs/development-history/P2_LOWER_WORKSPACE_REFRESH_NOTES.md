# P2 Lower Workspace Refresh

This update refines the lower workspace visible in the Overview, History and Diagnostics flows without changing the IQ pipeline, decoder, persistence model or online aircraft lookup.

## Local History

- New compact header and filter card.
- Modern native tabs with live aircraft/message counts.
- Responsive columns hide secondary fields when the panel is narrow.
- Search, period and row-limit controls are aligned consistently.
- SQLite status is shown in a dedicated health strip.
- Grid headers, rows and selection states use the native dashboard palette.

## Diagnostics

- Six compact metric cards arranged as a two-column grid.
- Responsive single-column mode on narrow panels.
- Overall Healthy/Attention state in the panel header.
- Improved queue and memory progress bars.
- Reduced unused vertical space.

## Workspace split

- More balanced default split between the data/history area and details/diagnostics area.
- Wider splitter and explicit resize cursor.
- Flat native tabs remove the visually heavy system tab treatment.

## Scope

Only WinForms presentation code was changed. Core decoding, IQ buffering, SQLite persistence, JSONL export and ADSBdb/HexDB logic remain unchanged.
