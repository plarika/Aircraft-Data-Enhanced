#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path

REQUIRED = {
    "ReedSolomon255249.cs": [
        "GfPolynomial = 0x187",
        "FirstConsecutiveRoot = 120",
        "RootCount = 6",
        "Berlekamp-Massey",
        "public static int Decode",
    ],
    "Vdl2PayloadDecoder.cs": [
        "GetFecOctetCount",
        "DeinterleaveInto",
        "ReedSolomon255249.Decode",
        "ExtractHdlcFrames",
        "GoodFcsResidual = 0xF0B8",
        "TryParseAvlc",
        "AVLC-VALID",
        "payload_truncated",
        "rs_uncorrectable",
        "avlc_fcs_failed",
    ],
    "Vdl2FrameDecoder.cs": [
        "Vdl2PayloadDecoder.Decode",
        "payload.Status",
    ],
    "D8pskSymbolAnalyzer.cs": [
        "reed_solomon_valid",
        "fcs_valid_frames",
        "avlc_frames",
        "payload_fec_decoded",
    ],
    "AircraftDataPanel.cs": [
        "Auto VDL2 decode",
        "PublishAvlcMessages",
        "Decoder: AVLC",
        "PAYLOAD / AVLC",
        "VDL2 Live Pipeline",
        "Vdl2AnalysisScheduler",
        "salvaged",
    ],
}

FORBIDDEN = {
    "AircraftDataPanel.cs": [
        "capture.RecommendedForD8psk)",
        "Not yet implemented: payload deinterleaving",
    ],
}


def validate(root: Path) -> list[str]:
    errors=[]
    for filename,tokens in REQUIRED.items():
        path=root/filename
        if not path.exists():
            errors.append(f"Missing file: {path}")
            continue
        text=path.read_text(encoding="utf-8-sig")
        for token in tokens:
            if token not in text:
                errors.append(f"{filename}: missing token {token!r}")
    for filename,tokens in FORBIDDEN.items():
        path=root/filename
        if not path.exists(): continue
        text=path.read_text(encoding="utf-8-sig")
        for token in tokens:
            if token in text:
                errors.append(f"{filename}: obsolete token still present {token!r}")

    payload=(root/"Vdl2PayloadDecoder.cs").read_text(encoding="utf-8-sig")
    if payload.count("ReedSolomon255249.Decode(") != 1:
        errors.append("Vdl2PayloadDecoder.cs: expected exactly one RS decoder invocation.")
    if payload.count("DeinterleaveInto(") != 3:
        errors.append("Vdl2PayloadDecoder.cs: expected one declaration and two deinterleaver calls.")
    panel=(root/"AircraftDataPanel.cs").read_text(encoding="utf-8-sig")
    if panel.count("PublishAvlcMessages(") != 2:
        errors.append("AircraftDataPanel.cs: expected one PublishAvlcMessages declaration and one call.")
    return errors


def main()->int:
    parser=argparse.ArgumentParser(description="Validate VDL2 payload, RS, HDLC and AVLC integration.")
    parser.add_argument("src",nargs="?",default="src")
    args=parser.parse_args()
    errors=validate(Path(args.src))
    if errors:
        for error in errors: print(f"[ERRO] {error}")
        return 1
    print(f"[OK] VDL2 payload/AVLC validation passed: {Path(args.src).resolve()}")
    return 0

if __name__ == "__main__": raise SystemExit(main())
