# Testing

Run `py -3 .\tools\audit_public_repository.py .` before publishing. Core
regressions are `test_verified_aircraft_only.py`,
`test_active_aircraft_sessions.py`, `test_local_history_database.py`,
`test_ui_preferences.py`, `test_vdl2_header_decoder.py`,
`test_vdl2_payload_decoder.py` and `test_acars_envelope_parser.py`. Public
fixtures must be synthetic.
