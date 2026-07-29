# Third-party notices

## SDR# / Airspy SDK

No Airspy or SDR# binary is redistributed. Obtain **SDR# SDK for Plugin
Developers** directly from https://airspy.com/download/ and copy
`SDRSharp.Common.dll` and `SDRSharp.Radio.dll` into `lib/` locally. The files
remain subject to Airspy's terms. The project is independent and not endorsed
by Airspy.

## libfec

`src/ReedSolomon255249.cs` is based on the classic Phil Karn/libfec
Berlekamp-Massey/Forney decoder. Upstream: https://github.com/quiet/libfec.
Copyright 2006 Phil Karn, KA9Q. Licence: LGPL-2.1-or-later.

## dumpvdl2

VDL2/AVLC behaviour was independently implemented from protocol specifications
and cross-checked against public documentation and behaviour of dumpvdl2
(https://github.com/szpajder/dumpvdl2), GPL-3.0-or-later. No dumpvdl2 source or
binary is included.

## Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite

`Microsoft.Data.Sqlite` 9.0.18 is restored through NuGet and is MIT licensed.
SQLitePCLRaw packages are Apache-2.0. SQLite core is public domain. No NuGet
binary is committed. Preserve restored package notices when distributing a
compiled binary.

## NumPy and SciPy

Optional Python tools install NumPy and SciPy separately. They are not bundled.
Consult the licences supplied by those distributions.

## Trademarks

All product, airline, airport, aircraft and service names remain the property
of their respective owners. Mention does not imply endorsement.
