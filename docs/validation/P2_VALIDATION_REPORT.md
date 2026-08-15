# P2 validation report

## Completed in the packaging environment

- Four-project dependency graph validated: Core -> none, Persistence -> Core, SDR# Adapter -> Core + Persistence, Tests -> all.
- XML project files and solution configuration parsed successfully.
- Source release manifest generated and verified.
- Public repository audit passed.
- C# string and delimiter structural checks passed across production and test sources.
- Python VDL2 header, VDL2 payload/RS/HDLC and ACARS envelope regressions passed.
- The full synthetic IQ golden vector was independently replayed through an equivalent preamble, differential demodulation, descrambling and payload decode path; it produced a valid header, valid RS payload and `hdlc_no_frame`.

## Mandatory Windows release gates

The packaging environment does not contain .NET 9, Windows Forms or proprietary SDR# SDK DLLs. Therefore the following gates must run on the release Windows machine before publishing binaries:

1. Approve the exact working SDK hashes with `PREPARAR_SDK_ESTAVEL.ps1`.
2. Run `BUILD_E_INSTALAR_TUDO.bat` and confirm all ten C# regression groups pass.
3. Start SDR# revision 1921 or 1922 x86 and perform a host smoke test.
4. Run `EXECUTAR_SOAK_SMOKE_5MIN.bat`.
5. Run `EXECUTAR_SOAK_24H.bat`; retain `artifacts/soak/release-24h/SOAK_REPORT.json`.

A stable binary release is approved only when all five Windows gates pass.
