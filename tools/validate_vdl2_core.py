#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path

REQUIRED = {
    "Vdl2FrameDecoder.cs": [
        "ExpectedPreamblePhases",
        "HeaderParityMasks",
        "LfsrInitialValue = 0x6959",
        "Vdl2PayloadDecoder.Decode",
        "Vdl2PayloadResult? Payload = null",
    ],
    "D8pskSymbolAnalyzer.cs": [
        "Vdl2FrameDecoder.Decode",
        "frame_sync",
        "physical_header",
        "payload = new",
        "avlc_frames",
    ],
    "ManagedBurstDetector.cs": [
        "CONTINUOUS-NOISE-RISE",
        "CONTINUOUS-MODULATED",
        "BROADBAND-IMPULSE",
    ],
    "IqCaptureManager.cs": [
        "ContinuousCaptures",
        "continuous_or_interference",
        "schema_version = 3",
    ],
}


def validate(root: Path) -> list[str]:
    errors: list[str] = []
    for filename, tokens in REQUIRED.items():
        path=root/filename
        if not path.exists():
            errors.append(f"Missing file: {path}")
            continue
        text=path.read_text(encoding="utf-8-sig")
        for token in tokens:
            if token not in text:
                errors.append(f"{filename}: missing required token {token!r}")

    panel=root/"AircraftDataPanel.cs"
    if panel.exists():
        text=panel.read_text(encoding="utf-8-sig")
        match=re.search(r"Aircraft Data Enhanced — VDL2 .+ v(\d+)\.(\d+)\.(\d+)",text)
        if not match:
            errors.append("AircraftDataPanel.cs: no VDL2 semantic version title found.")
        else:
            major,minor,_=map(int,match.groups())
            if major != 0 or minor < 12:
                errors.append(f"AircraftDataPanel.cs: expected VDL2 stage 0.12 or newer, got {match.group(0)!r}.")
    return errors


def main() -> int:
    parser=argparse.ArgumentParser(description="Validate VDL2 core decoding features.")
    parser.add_argument("src",nargs="?",default="src")
    args=parser.parse_args()
    root=Path(args.src)
    errors=validate(root)
    if errors:
        for error in errors: print(f"[ERRO] {error}")
        return 1
    print(f"[OK] VDL2 core validation passed: {root.resolve()}")
    return 0

if __name__ == "__main__": raise SystemExit(main())
