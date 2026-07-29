#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path


REQUIRED = {
    "SpectrumWaterfallControl.cs": [
        "_invalidatePending",
        "RequestInvalidate",
        "LockBits",
        "Format32bppPArgb",
        "_targetFps = 8",
        "DroppedFrames",
        "RowCount",
    ],
    "MessageStore.cs": [
        "public long Version",
        "Interlocked.Increment(ref _version)",
    ],
    "Vdl2CaptureSalvager.cs": [
        "continuous_salvage_ready",
        "multi_burst_split_ready",
        "MaximumBurstsPerCapture = 12",
        "ExtractionGuardMs = 12.0",
        "vdl2_capture_salvage",
        "continuous_salvage_burst",
    ],
    "Vdl2AnalysisScheduler.cs": [
        "capacity = 8",
        "analysis_queue_full",
        "CONTINUOUS-CAPTURE-WITH-VALID-AVLC",
        "Vdl2AnalysisQueueSnapshot",
        "PrepareAsync",
        "ProcessRequestAsync",
    ],
    "IqCaptureManager.cs": [
        "MaximumPendingWrites = 4",
        "capture_write_queue_full",
        "WriteWorkerAsync",
        "PendingWrites",
        "DroppedWrites",
    ],
    "AircraftDataPanel.cs": [
        "VDL2 Live Pipeline",
        "RefreshGridIfNeeded",
        "_gridRefreshRequested",
        "Vdl2AnalysisScheduler",
        "salvaged AVLC",
        "AVLC V:",
        "WF rows",
    ],
}


FORBIDDEN = {
    "SpectrumWaterfallControl.cs": [
        "bitmap.SetPixel",
        "BeginInvoke(Invalidate)",
        "_targetFps = 12",
    ],
    "IqCaptureManager.cs": [
        "_ = Task.Run(() => PersistCaptureAsync",
    ],
    "AircraftDataPanel.cs": [
        "_ = AnalyzeCaptureAsync(",
        "_filter.TextChanged += (_, _) => RefreshGrid()",
        "_channelHistoryFilter.SelectedIndexChanged += (_, _) =>\n            RefreshGrid();",
    ],
}



def extract_calls(text: str, type_name: str) -> list[str]:
    calls: list[str] = []
    needle = f"new {type_name}("
    search_from = 0

    while True:
        start = text.find(needle, search_from)
        if start < 0:
            return calls

        index = start + len(f"new {type_name}")
        depth = 0
        state = "code"

        while index < len(text):
            ch = text[index]
            nxt = text[index + 1] if index + 1 < len(text) else ""

            if state == "code":
                if ch == "/" and nxt == "/":
                    state = "line"
                    index += 1
                elif ch == "/" and nxt == "*":
                    state = "block"
                    index += 1
                elif ch == '"':
                    state = "string"
                elif ch == "'":
                    state = "char"
                elif ch == "(":
                    depth += 1
                elif ch == ")":
                    depth -= 1
                    if depth == 0:
                        calls.append(text[start:index + 1])
                        search_from = index + 1
                        break
            elif state == "line":
                if ch == "\n":
                    state = "code"
            elif state == "block":
                if ch == "*" and nxt == "/":
                    state = "code"
                    index += 1
            elif state == "string":
                if ch == "\\":
                    index += 1
                elif ch == '"':
                    state = "code"
            elif state == "char":
                if ch == "\\":
                    index += 1
                elif ch == "'":
                    state = "code"

            index += 1
        else:
            raise RuntimeError(f"Unterminated constructor: {type_name}")


def argument_count(call: str) -> int:
    start = call.find("(")
    end = call.rfind(")")
    content = call[start + 1:end]

    if not content.strip():
        return 0

    count = 1
    depth = 0
    state = "code"
    index = 0

    while index < len(content):
        ch = content[index]
        nxt = content[index + 1] if index + 1 < len(content) else ""

        if state == "code":
            if ch == "/" and nxt == "/":
                state = "line"
                index += 1
            elif ch == "/" and nxt == "*":
                state = "block"
                index += 1
            elif ch == '"':
                state = "string"
            elif ch == "'":
                state = "char"
            elif ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            elif ch == "," and depth == 0:
                count += 1
        elif state == "line":
            if ch == "\n":
                state = "code"
        elif state == "block":
            if ch == "*" and nxt == "/":
                state = "code"
                index += 1
        elif state == "string":
            if ch == "\\":
                index += 1
            elif ch == '"':
                state = "code"
        elif state == "char":
            if ch == "\\":
                index += 1
            elif ch == "'":
                state = "code"

        index += 1

    return count


def validate(root: Path) -> list[str]:
    errors: list[str] = []

    for filename, tokens in REQUIRED.items():
        path = root / filename

        if not path.exists():
            errors.append(f"Missing file: {path}")
            continue

        text = path.read_text(encoding="utf-8-sig")

        for token in tokens:
            if token not in text:
                errors.append(
                    f"{filename}: missing required token {token!r}"
                )

    for filename, tokens in FORBIDDEN.items():
        path = root / filename

        if not path.exists():
            continue

        text = path.read_text(encoding="utf-8-sig")

        for token in tokens:
            if token in text:
                errors.append(
                    f"{filename}: obsolete/freeze-prone token remains {token!r}"
                )

    panel = (root / "AircraftDataPanel.cs").read_text(
        encoding="utf-8-sig"
    )

    if panel.count("RefreshGrid();") != 1:
        errors.append(
            "AircraftDataPanel.cs: RefreshGrid must only be called by "
            "RefreshGridIfNeeded."
        )

    if panel.count("PublishAvlcMessages(") != 2:
        errors.append(
            "AircraftDataPanel.cs: expected one AVLC publish call and "
            "one declaration."
        )

    waterfall = (root / "SpectrumWaterfallControl.cs").read_text(
        encoding="utf-8-sig"
    )

    if waterfall.count("BeginInvoke(") != 1:
        errors.append(
            "SpectrumWaterfallControl.cs: expected exactly one coalesced "
            "BeginInvoke path."
        )

    scheduler = (root / "Vdl2AnalysisScheduler.cs").read_text(
        encoding="utf-8-sig"
    )

    if scheduler.count("_queue.AddLast(") != 1:
        errors.append(
            "Vdl2AnalysisScheduler.cs: expected one bounded queue enqueue."
        )

    scheduler_calls = extract_calls(
        (root / "Vdl2AnalysisScheduler.cs").read_text(
            encoding="utf-8-sig"
        ),
        "D8pskAnalysisResult",
    )

    if len(scheduler_calls) != 1:
        errors.append(
            "Vdl2AnalysisScheduler.cs: expected one failure result constructor."
        )
    elif argument_count(scheduler_calls[0]) not in (27, 28):
        errors.append(
            "Vdl2AnalysisScheduler.cs: D8pskAnalysisResult failure "
            f"constructor has {argument_count(scheduler_calls[0])} arguments."
        )

    snapshot_calls = extract_calls(
        (root / "IqCaptureManager.cs").read_text(
            encoding="utf-8-sig"
        ),
        "CaptureManagerSnapshot",
    )

    if len(snapshot_calls) != 1 or argument_count(snapshot_calls[0]) != 12:
        errors.append(
            "IqCaptureManager.cs: CaptureManagerSnapshot must have 12 arguments."
        )

    child_calls = extract_calls(
        (root / "Vdl2CaptureSalvager.cs").read_text(
            encoding="utf-8-sig"
        ),
        "CaptureInfo",
    )

    if len(child_calls) != 1 or argument_count(child_calls[0]) != 14:
        errors.append(
            "Vdl2CaptureSalvager.cs: child CaptureInfo must have 14 arguments."
        )

    project = root.parent / "AircraftDataEnhanced.csproj"

    if project.exists():
        text = project.read_text(encoding="utf-8-sig")

        version_match = re.search(
            r"<Version>0\.(\d+)\.(\d+)-(?:alpha|beta)</Version>",
            text,
        )

        if not version_match:
            errors.append(
                "AircraftDataEnhanced.csproj: no valid alpha/beta version found."
            )
        else:
            minor, patch = map(int, version_match.groups())

            if minor < 13:
                errors.append(
                    "AircraftDataEnhanced.csproj: live pipeline requires "
                    f"0.13.0-alpha/beta or newer, got 0.{minor}.{patch}."
                )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Validate the bounded live decode pipeline and waterfall "
            "anti-freeze protections."
        )
    )
    parser.add_argument("src", nargs="?", default="src")
    args = parser.parse_args()

    root = Path(args.src)
    errors = validate(root)

    if errors:
        for error in errors:
            print(f"[ERRO] {error}")
        return 1

    print(
        f"[OK] Live pipeline/anti-freeze validation passed: "
        f"{root.resolve()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
