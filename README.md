# Aircraft Data Enhanced

**Receive-only VDL2 / AVLC / ACARS plugin for SDR# .NET 9 x86**

Public beta `v0.19.0-beta` with an Air Operations Terminal, verified-aircraft
sessions and embedded SQLite history.

## Features

- local IQ processing without audio loopback;
- D8PSK VDL2 demodulation;
- physical header, descrambling and RS(255,249);
- HDLC unstuffing and AVLC FCS validation;
- ACARS envelope parsing;
- verified ICAO24-only views and active sessions;
- airport-style Operations Board;
- local SQLite history;
- bounded workers and anti-freeze waterfall;
- receive-only operation.

## Safety and law

Research/hobby use only. Not for ATC, navigation or safety decisions. Read
[`LEGAL.md`](LEGAL.md) and [`PRIVACY.md`](PRIVACY.md).

## Requirements and official SDR# SDK

Airspy binaries are not included. Visit https://airspy.com/download/, locate
**SDR# SDK for Plugin Developers**, and copy these files into `lib/`:

```text
SDRSharp.Common.dll
SDRSharp.Radio.dll
```

The DLLs are ignored by Git and must not be committed. You may run
`GET_SDRSHARP_SDK.bat` to open the official page.

## Build and install

Close SDR# and run `BUILD_E_INSTALAR_TUDO.bat`. It validates, restores SQLite
from NuGet, builds for `win-x86`, and installs the complete output.

## Local data

Runtime data is stored under `%LOCALAPPDATA%\AircraftDataEnhanced\` and is
excluded by `.gitignore`.

## Licensing

- original code and docs: MIT;
- `src/ReedSolomon255249.cs`: LGPL-2.1-or-later.

See `LICENSES/README.md` and `THIRD_PARTY_NOTICES.md`. No SDR#, Airspy or NuGet
binary is committed.

## Documents

- `docs/BUILD_FROM_SOURCE.md`
- `docs/ARCHITECTURE.md`
- `docs/TESTING.md`
- `CONTRIBUTING.md`
- `SECURITY.md`
- `SUPPORT.md`

Independent project; no affiliation or endorsement by Airspy, SDR#, airports,
airlines or aviation authorities.
