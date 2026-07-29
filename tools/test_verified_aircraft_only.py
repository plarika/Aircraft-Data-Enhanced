#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import re


def normalize_icao(value: str) -> str | None:
    normalized = value.strip().upper()

    if not re.fullmatch(
        r"[0-9A-F]{6}",
        normalized,
    ):
        return None

    return normalized


def accepted(
    valid: bool,
    protocol: str,
    icao: str,
) -> bool:
    return (
        valid
        and protocol.strip().upper()
        in {"ACARS", "AVLC"}
        and normalize_icao(icao)
        is not None
    )


def main() -> int:
    cases = [
        (True, "ACARS", "A1B2C3", True),
        (True, "AVLC", "D4E5F6", True),
        (True, "avlc", "1a2b3c", True),
        (True, "VDL2", "A1B2C3", False),
        (True, "VDL2-CL", "", False),
        (True, "ACARS", "", False),
        (True, "AVLC", "UNKNOWN", False),
        (True, "AVLC", "ZZZZZZ", False),
        (False, "ACARS", "A1B2C3", False),
    ]

    for valid, protocol, icao, expected in cases:
        actual = accepted(
            valid,
            protocol,
            icao,
        )

        if actual != expected:
            raise AssertionError(
                (
                    valid,
                    protocol,
                    icao,
                    expected,
                    actual,
                )
            )

    print(
        "[OK] Verified-aircraft-only regression passed: "
        "FCS-valid ACARS/AVLC with a six-hex ICAO24 is kept; "
        "unknown, generic and invalid rows are hidden."
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
