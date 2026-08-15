# P2 architecture

## AircraftDataEnhanced.Core
DSP, bounded IQ pipeline, VDL2/AVLC/ACARS decoding, models and policies. No SDR# or SQLite dependency.

## AircraftDataEnhanced.Persistence
SQLite, JSONL, captures, D8PSK file analysis, runtime paths and preferences. References Core.

## AircraftDataEnhanced.SdrSharpAdapter
SDR# interfaces, WinForms UI, waterfall and plugin bootstrap. References Core and Persistence.

## AircraftDataEnhanced.Tests
Executable regression, golden-vector, persistence integration and long soak test. References all production projects.
