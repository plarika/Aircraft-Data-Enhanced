# P2 final visual validation

## Scope

The changes in this package are limited to the WinForms presentation layer and release documentation.

## Automated validation completed

- C# string validation: PASS
- Aircraft lookup lifecycle regression: PASS
- Final visual interface regression: PASS
- P2 stable architecture validation: PASS
- VDL2 header regression: PASS
- VDL2 payload regression: PASS
- ACARS envelope regression: PASS
- UI preferences regression: PASS
- Active-aircraft session regression: PASS
- SQLite history regression: PASS
- Verified-aircraft-only regression: PASS
- Release manifest verification: PASS
- Public repository/privacy audit: PASS

## Privacy validation

The source package was checked for:

- absolute Windows user-profile paths;
- Unix home paths;
- email addresses;
- known user names and location terms;
- runtime logs, captures, databases and JSONL exports;
- build outputs and Python caches.

No personal data was detected. The PNG previews were re-encoded without metadata.

## Windows validation still required

The current environment does not include the required .NET SDK or SDR# host. Run `BUILD_E_INSTALAR_TUDO.bat` on Windows with .NET SDK 9.0.316 and perform an interactive SDR# smoke test.

A new 24-hour soak is not required because Core, Persistence, the IQ pipeline and the exporter were not modified.
