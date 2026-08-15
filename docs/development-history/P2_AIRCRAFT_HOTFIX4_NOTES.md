# P2 Aircraft Hotfix 4

## Cause confirmed by runtime log

The first HexDB request consumed the six-second `HttpClient.Timeout`.
`LookupAircraftHexDbAsync` rethrew the resulting
`OperationCanceledException`, so the complete lookup returned `timeout`
before ADSBdb could be attempted.

## Changes

- ADSBdb is now the primary provider for aircraft and route metadata.
- HexDB remains as a fallback.
- Each provider has an independent four-second timeout.
- `HttpClient.Timeout` is infinite; linked provider tokens own timeouts.
- A provider timeout returns a provider-specific result and does not cancel
  the fallback provider.
- ADSBdb callsign route lookup was added.
- Network failures are cached for two minutes to avoid repeated pressure.
- Final status contains both provider results when neither succeeds.

No IQ pipeline, decoder, persistence, or waterfall code was changed.
