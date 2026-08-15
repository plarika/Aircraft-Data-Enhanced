# SDR# CI contract stubs

These projects provide the smallest compile-time contracts required to compile
the public plugin source in GitHub Actions.

They are **not** SDR# binaries, are not runtime-compatible replacements, and
must never be copied into a release or an SDR# installation. Local production
builds must continue to use the official `SDRSharp.Common.dll` and
`SDRSharp.Radio.dll` from the Airspy SDK.
