# SDR# SDK compatibility

Aircraft Data Enhanced v1.0.0 Stable is built for SDR# .NET 9 x86.

The production host/SDK pair validated for this stable release is revision **1921**. Revision 1922 remains listed as a target in the compatibility matrix but is not marked stable until it completes the same host-specific validation.

The proprietary SDR# SDK DLLs are never committed or redistributed by this repository. Obtain **SDR# SDK for Plugin Developers** from `https://airspy.com/download/`, then copy `SDRSharp.Common.dll` and `SDRSharp.Radio.dll` into `lib/` locally.

`PREPARAR_SDK_ESTAVEL.ps1` records/verifies the exact local fingerprints. Public metadata may record approved file hashes and versions, but never the proprietary binaries themselves.
