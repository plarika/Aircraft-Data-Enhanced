# P2 soak test

Run the five-minute smoke test first, then the 24-hour release gate.

The long-running test is intentionally host-independent because the official
SDR# SDK files are reference assemblies and cannot be executed outside the
SDR# host. It keeps these production-critical paths active with the
deterministic golden IQ vector:

- bounded IQ pipeline and ArrayPool accounting;
- headless FFT/power-row waterfall processing;
- JSONL export;
- SQLite persistence;
- working-set and queue telemetry.

The actual WinForms waterfall and SDR# adapter are validated separately by the
interactive host smoke test after installation.

The soak fails on dropped or faulted IQ blocks, leaked pooled buffers, dropped
persistence records, SQLite/JSONL faults, no waterfall rows, a non-finite
waterfall checksum, or working-set growth above 512 MiB.

Reports are written under `artifacts/soak/`.