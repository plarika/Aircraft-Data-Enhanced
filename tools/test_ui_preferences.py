#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import json
import tempfile
from pathlib import Path


DEFAULTS = {
    "SelectedWorkspace": 0,
    "CommandBarVisible": False,
    "ControlCenterVisible": False,
    "ChannelMonitorVisible": False,
    "WaterfallVisible": True,
    "DetailsVisible": True,
    "OperationsWindowIndex": 1,
    "WaterfallMinimumDb": -100,
    "WaterfallMaximumDb": -35,
    "WaterfallContrastPercent": 100,
}


def normalize(values: dict) -> dict:
    result = dict(DEFAULTS)
    result.update(values)

    result["SelectedWorkspace"] = max(
        0,
        min(3, int(result["SelectedWorkspace"])),
    )

    result["OperationsWindowIndex"] = max(
        0,
        min(4, int(result["OperationsWindowIndex"])),
    )

    result["WaterfallMinimumDb"] = max(
        -140,
        min(-20, float(result["WaterfallMinimumDb"])),
    )

    result["WaterfallMaximumDb"] = max(
        -120,
        min(10, float(result["WaterfallMaximumDb"])),
    )

    if (
        result["WaterfallMaximumDb"]
        <= result["WaterfallMinimumDb"]
    ):
        result["WaterfallMaximumDb"] = min(
            10,
            result["WaterfallMinimumDb"] + 20,
        )

    result["WaterfallContrastPercent"] = max(
        25,
        min(400, float(result["WaterfallContrastPercent"])),
    )

    return result


def main() -> int:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "ui-preferences.json"

        preferences = normalize(
            {
                "SelectedWorkspace": 99,
                "OperationsWindowIndex": -4,
                "WaterfallMinimumDb": -200,
                "WaterfallMaximumDb": -300,
                "WaterfallContrastPercent": 999,
                "DetailsVisible": False,
            }
        )

        temporary = path.with_suffix(".tmp")
        temporary.write_text(
            json.dumps(preferences, indent=2),
            encoding="utf-8",
        )
        temporary.replace(path)

        loaded = normalize(
            json.loads(
                path.read_text(encoding="utf-8")
            )
        )

        assert loaded["SelectedWorkspace"] == 3
        assert loaded["OperationsWindowIndex"] == 0
        assert loaded["WaterfallMinimumDb"] == -140
        assert loaded["WaterfallMaximumDb"] == -120
        assert loaded["WaterfallContrastPercent"] == 400
        assert loaded["DetailsVisible"] is False

    print(
        "[OK] UI preference regression passed: JSON persistence, "
        "workspace/window clamping and waterfall range normalization."
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
