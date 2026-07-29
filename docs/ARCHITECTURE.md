# Architecture

```text
SDR# IQ → metrics/detector → bounded capture → bounded VDL2 analysis
→ D8PSK → physical header/descrambler → Reed-Solomon → HDLC/FCS
→ AVLC/ACARS → verified-aircraft policy → views/sessions/SQLite
```

The IQ callback performs no network or database I/O, direct UI updates, full
decoding or unbounded task creation. Frame integrity is not transmitter
authentication.
