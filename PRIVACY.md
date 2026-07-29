# Privacy

Local data is stored under `%LOCALAPPDATA%\AircraftDataEnhanced\`, including
SQLite history, WAL/SHM files, preferences and optional capture/analysis data.
These files are excluded by `.gitignore` and must not be committed publicly.

The plugin has no project-operated telemetry. Optional enrichment may send an
ICAO24 or callsign to HexDB and may open Planespotters.net, ADS-B Exchange or
Bing in the system browser. Those services have their own terms and privacy
policies.

Before posting public reports, remove usernames, absolute paths, receiver
location, credentials, local databases and private communications.
