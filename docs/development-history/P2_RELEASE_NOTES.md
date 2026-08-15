# Aircraft Data Enhanced v1.0.0 — P2 stable candidate


## Final visual interface refresh

- Added the P2 dark visual system, operational metric cards and adaptive navigation rail.
- Unified WinForms tables, tabs, buttons, inputs, menu/status bars and channel cards.
- Refined spectrum/waterfall colours without changing IQ or decoder processing.
- Core and Persistence remain identical to the 24-hour soak-tested implementation.

- Split into Core, Persistence, SdrSharpAdapter and Tests projects.
- Exact SDR# SDK registration using SHA-256, size and version metadata.
- SDK ABI tests inspect reference-assembly metadata without executing it.
- Central package versions and lock-file restore.
- Deterministic full synthetic VDL2 IQ golden vector.
- Headless waterfall FFT soak runner with bounded IQ pipeline.
- SQLite and JSONL persistence integration tests.
- Five-minute smoke gate and 24-hour release soak gate.
- Source and binary SHA-256 manifests.
- Stable release remains conditional on host smoke and 24-hour soak success.