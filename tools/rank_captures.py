#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path


def load_capture(path: Path) -> dict | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None

    if "iq_file" not in data:
        return None

    data["_metadata_path"] = path
    return data


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Rank Aircraft Data Enhanced captures by quality score.")
    parser.add_argument("folder", help="Capture folder.")
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument(
        "--recommended-only",
        action="store_true",
        help="Show only captures recommended for D8PSK.")
    parser.add_argument(
        "--copy-best",
        help="Copy the ranked metadata and IQ files to this folder.")
    args = parser.parse_args()

    folder = Path(args.folder).resolve()
    captures = []

    for metadata_path in folder.glob("*.json"):
        capture = load_capture(metadata_path)
        if capture is None:
            continue

        if args.recommended_only and not capture.get(
            "recommended_for_d8psk", False
        ):
            continue

        captures.append(capture)

    captures.sort(
        key=lambda item: (
            float(item.get("quality_score", 0)),
            not bool(item.get("limited", False)),
            float(item.get("duration_ms", 0)),
        ),
        reverse=True,
    )

    selected = captures[: max(1, args.top)]

    print(
        "Rank  Score  Rec  Limited  MHz         Duration  File"
    )
    print("-" * 88)

    for index, capture in enumerate(selected, start=1):
        print(
            f"{index:>4}  "
            f"{float(capture.get('quality_score', 0)):>5.1f}  "
            f"{str(bool(capture.get('recommended_for_d8psk', False))):>3}  "
            f"{str(bool(capture.get('limited', False))):>7}  "
            f"{float(capture.get('frequency_mhz', 0)):>10.6f}  "
            f"{float(capture.get('duration_ms', 0)):>8.1f}  "
            f"{capture['_metadata_path'].name}"
        )

    if args.copy_best:
        destination = Path(args.copy_best).resolve()
        destination.mkdir(parents=True, exist_ok=True)

        for capture in selected:
            metadata_path = capture["_metadata_path"]
            iq_path = metadata_path.parent / capture["iq_file"]
            shutil.copy2(metadata_path, destination / metadata_path.name)
            if iq_path.exists():
                shutil.copy2(iq_path, destination / iq_path.name)

        print(f"\nCopied {len(selected)} ranked captures to: {destination}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
