#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np


def get_nested(data: dict, *path: str, default=None):
    value = data
    for key in path:
        if not isinstance(value, dict) or key not in value:
            return default
        value = value[key]
    return value


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Inspect Aircraft Data Enhanced .iqf32 captures.")
    parser.add_argument("metadata", help="Path to capture JSON metadata.")
    parser.add_argument("--export-npy", action="store_true")
    args = parser.parse_args()

    metadata_path = Path(args.metadata).resolve()
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))

    iq_path = metadata_path.parent / metadata["iq_file"]
    data = np.fromfile(iq_path, dtype="<f4")

    if data.size % 2:
        raise SystemExit("Invalid capture: odd float count.")

    iq = data[0::2] + 1j * data[1::2]
    power = np.abs(iq) ** 2
    sample_rate = float(metadata["sample_rate"])

    classification = (
        metadata.get("trigger_classification")
        or metadata.get("classification")
        or "unknown"
    )

    print(f"Capture: {metadata_path.name}")
    print(f"Schema: {metadata.get('schema_version', 1)}")
    print(f"Frequency: {metadata['frequency_hz'] / 1e6:.6f} MHz")
    print(f"Sample rate: {sample_rate:.1f} S/s")
    print(f"Complex samples: {iq.size}")
    print(f"Duration: {iq.size / sample_rate * 1000:.3f} ms")
    print(f"Mean power: {10*np.log10(max(float(power.mean()), 1e-20)):.3f} dBFS")
    print(f"Peak power: {10*np.log10(max(float(power.max()), 1e-20)):.3f} dBFS")
    print(f"Trigger: {classification}")
    print(f"Completion: {metadata.get('completion_reason', 'unknown')}")
    print(f"Limited: {metadata.get('limited', False)}")

    if "quality_score" in metadata:
        print(f"Quality score: {metadata['quality_score']}")
        print(f"Quality grade: {metadata.get('quality_grade', 'unknown')}")
        print(
            "Recommended for D8PSK: "
            f"{metadata.get('recommended_for_d8psk', False)}"
        )
        print(
            "Peak margin: "
            f"{get_nested(metadata, 'aggregate', 'peak_margin_db', default='n/a')} dB"
        )

    if args.export_npy:
        output = metadata_path.with_suffix(".npy")
        np.save(output, iq.astype(np.complex64))
        print(f"Exported: {output}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
