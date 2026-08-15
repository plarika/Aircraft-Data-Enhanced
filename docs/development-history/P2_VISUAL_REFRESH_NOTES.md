# P2 v1.0.0 visual refresh

## Added

- `AdeVisualTheme.cs`: central colour palette, metric cards, dark tabs, menu renderer and adaptive navigation rail.
- P2 header with live state, UTC clock and operational summary cards.
- Adaptive wide-screen navigation matching the final Aircraft Data Enhanced visual concept.
- Unified dark styling for WinForms buttons, inputs, grids and status areas.
- Refined channel cards and spectrum/waterfall colours.

## Preserved

- Core decoder and IQ pipeline.
- Persistence, SQLite and JSONL services.
- SDR# stream-hook behaviour.
- Existing views, menus, preferences and commands.
- P2 v1.0.0 version and SDK compatibility requirements.

## Post-installation gate

A short SDR# interactive smoke test is required after compilation. The validated 24-hour Core/Persistence soak does not need to be repeated.
