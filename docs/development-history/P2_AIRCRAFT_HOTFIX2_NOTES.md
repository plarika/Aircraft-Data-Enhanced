# P2 Final UI — Aircraft Hotfix 2

## Scope

This hotfix corrects the online aircraft identity lifecycle in the Aircraft panel.
It does not change the IQ callback, decoders, bounded pipeline, persistence schema,
waterfall processing, or release soak harness.

## Corrected behaviour

- Re-selecting the exact same message no longer restarts online lookup.
- New frames from the same ICAO24 update local fields without cancelling identity lookup.
- A callsign change refreshes only the route lookup.
- An ICAO24 change refreshes only the aircraft identity lookup.
- Aircraft and route requests use separate cancellation tokens.
- Stale responses are accepted only when ICAO24 or callsign still matches.
- Manual **Refresh online** bypasses the normal cache.
- A failed forced refresh preserves the last valid identity or route on screen.
- Temporary network/HTTP failures are cached briefly to prevent request loops.
- HTTP 429 responses receive a five-minute backoff.
- The metadata service User-Agent now reports version `1.0.0`.

## Validation

`tools/test_aircraft_dashboard_lookup.py` is executed by
`BUILD_E_INSTALAR_TUDO.bat` before compilation.
