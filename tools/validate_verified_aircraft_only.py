#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


REQUIRED_POLICY = [
    "VerifiedAircraftMessagePolicy",
    "TryAccept",
    "message_not_valid",
    "protocol_not_verified_avlc",
    "icao24_missing_or_invalid",
    '"ACARS"',
    '"AVLC"',
    "TryNormalizeIcao",
]

REQUIRED_PANEL = [
    "_filteredUnknownMessages",
    "VerifiedAircraftMessagePolicy.TryAccept",
    "unknown hidden",
    '"Verified Messages"',
    "Live Pipeline · ACARS · Air Operations Terminal · v0.19.0-beta",
]

REQUIRED_STORE = [
    "VerifiedAircraftMessagePolicy.TryAccept",
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "src",
        nargs="?",
        default="src",
    )
    args = parser.parse_args()

    root = Path(args.src)
    errors: list[str] = []

    checks = {
        "VerifiedAircraftMessagePolicy.cs":
            REQUIRED_POLICY,
        "AircraftDataPanel.cs":
            REQUIRED_PANEL,
        "MessageStore.cs":
            REQUIRED_STORE,
    }

    for filename, tokens in checks.items():
        path = root / filename

        if not path.exists():
            errors.append(
                f"Missing file: {path}"
            )
            continue

        text = path.read_text(
            encoding="utf-8-sig"
        )

        for token in tokens:
            if token not in text:
                errors.append(
                    f"{filename}: missing token {token!r}"
                )

    panel_path = root / "AircraftDataPanel.cs"

    if panel_path.exists():
        text = panel_path.read_text(
            encoding="utf-8-sig"
        )

        if text.count(
            "VerifiedAircraftMessagePolicy.TryAccept"
        ) < 2:
            errors.append(
                "AircraftDataPanel.cs: verified policy must protect "
                "both generic JSON and full AVLC publication."
            )

        if "_stats.OnAccepted();" in text:
            accepted_position = text.find(
                "_stats.OnAccepted();"
            )

            policy_position = text.find(
                "VerifiedAircraftMessagePolicy.TryAccept"
            )

            if accepted_position < policy_position:
                errors.append(
                    "AircraftDataPanel.cs: accepted statistics are "
                    "updated before strict verification."
                )

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Verified-aircraft-only validation passed: "
        f"{root.resolve()}"
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
