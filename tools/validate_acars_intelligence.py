#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED = {
    "AcarsMessageParser.cs": [
        "internal sealed record AcarsMessage",
        "MinimumFrameLength = 16",
        "CalculateCrc",
        "0x8408",
        "TrimStart('.')",
        "MessageNumberWithSequence",
        "FlightId",
        "CrcValid",
        "MoreBlocks",
        "labelSecondByte == Del",
        "blockIdChar is >= '0' and <= '9'",
    ],
    "Vdl2PayloadDecoder.cs": [
        "AcarsMessage? Acars = null",
        "AcarsMessageParser.TryParse",
        '? "ACARS"',
        "acars?.Summary",
    ],
    "Models.cs": [
        "AcarsMode",
        "AcarsBlockId",
        "AcarsMessageNumber",
        "AcarsMessageSequence",
        "AcarsAcknowledgement",
        "AcarsCrcValid",
        "AcarsMoreBlocks",
        "AcarsMessageId",
    ],
    "AircraftDataPanel.cs": [
        'acars is not null\n                        ? "ACARS"',
        "acars?.Registration",
        "acars?.FlightId",
        "Inner ACARS CRC",
        "ACARS envelope",
    ],
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.src)
    errors: list[str] = []

    for filename, tokens in REQUIRED.items():
        path = root / filename

        if not path.exists():
            errors.append(f"Missing file: {path}")
            continue

        text = path.read_text(encoding="utf-8-sig")

        for token in tokens:
            if token not in text:
                errors.append(f"{filename}: missing token {token!r}")

    parser_path = root / "AcarsMessageParser.cs"
    if parser_path.exists():
        text = parser_path.read_text(encoding="utf-8-sig")

        if "HttpClient" in text or "Process.Start" in text:
            errors.append(
                "AcarsMessageParser.cs must remain a deterministic offline parser."
            )

        if text.count("CalculateCrc(") != 2:
            errors.append(
                "AcarsMessageParser.cs: expected one call and one CRC declaration."
            )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(f"[OK] ACARS message intelligence validation passed: {root.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
