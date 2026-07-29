#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np


SYMBOL_RATE = 10_500.0
ALPHA = 0.6
TIMING_PHASES = 128
FAMILY_FALSE_ALARM = 0.001
GRAY_BITS = (
    "000", "001", "011", "010",
    "110", "111", "101", "100",
)


def moving_average(values: np.ndarray, window: int) -> np.ndarray:
    kernel = np.ones(window, dtype=np.float64) / window
    return np.convolve(values, kernel, mode="same")


def mean_range(values: np.ndarray, start: int, end: int) -> float:
    if end <= start:
        return float("inf")
    return float(values[start:end].mean())


def find_bounded_burst(
    iq: np.ndarray,
    sample_rate: float,
) -> tuple[int, int, float] | None:
    power = np.abs(iq) ** 2
    window = max(8, round(sample_rate * 0.0015))
    smooth = moving_average(power, window)
    noise = max(float(np.quantile(smooth, 0.30)), 1e-20)

    threshold = noise * 4.0
    boundary_threshold = noise * 2.0
    maximum_gap = max(1, round(sample_rate * 0.002))
    minimum_length = max(16, round(sample_rate * 0.008))
    maximum_length = max(minimum_length, round(sample_rate * 1.2))
    guard = max(8, round(sample_rate * 0.008))

    segments: list[tuple[int, int]] = []
    start = None
    last_active = None

    for index, active in enumerate(smooth > threshold):
        if active:
            if start is None:
                start = index
            last_active = index
        elif start is not None and index - last_active > maximum_gap:
            segments.append((start, last_active + 1))
            start = None
            last_active = None

    if start is not None:
        segments.append((start, last_active + 1))

    candidates: list[tuple[float, int, int, float]] = []

    for start, end in segments:
        length = end - start

        if not minimum_length <= length <= maximum_length:
            continue

        if start < guard or end > len(smooth) - guard:
            continue

        leading = mean_range(smooth, start - guard, start)
        trailing = mean_range(smooth, end, end + guard)

        if leading > boundary_threshold or trailing > boundary_threshold:
            continue

        signal = mean_range(smooth, start, end)
        snr_db = 10 * math.log10(max(signal / noise, 1e-12))
        edge_db = 10 * math.log10(
            max(signal / max(leading, trailing), 1e-12)
        )

        if snr_db < 5.0 or edge_db < 3.0:
            continue

        score = snr_db * math.sqrt(max(length / sample_rate, 1e-6))
        candidates.append((score, start, end, snr_db))

    if not candidates:
        return None

    _, start, end, snr_db = max(candidates)
    expansion = max(1, round(sample_rate * 0.002))

    return (
        max(guard, start - expansion),
        min(len(iq) - guard, end + expansion),
        snr_db,
    )


def spectral_centroid(iq: np.ndarray, sample_rate: float) -> float:
    size = 1
    while size < len(iq):
        size <<= 1

    size = min(max(size, 1024), 131072)
    windowed = np.zeros(size, dtype=np.complex128)
    copy = min(len(iq), size)
    windowed[:copy] = iq[:copy] * np.hanning(copy)

    spectrum = np.fft.fft(windowed)
    frequency = np.fft.fftfreq(size, 1.0 / sample_rate)
    power = np.abs(spectrum) ** 2
    mask = np.abs(frequency) <= min(10_000.0, sample_rate * 0.45)
    denominator = float(power[mask].sum())

    if denominator <= 1e-20:
        return 0.0

    return float(
        (frequency[mask] * power[mask]).sum() /
        denominator
    )


def rrc_taps(
    sample_rate: float,
    symbol_rate: float,
    alpha: float,
    span: int,
) -> np.ndarray:
    sps = sample_rate / symbol_rate
    half = math.ceil(span * sps / 2)
    taps = []

    for index in range(-half, half + 1):
        t = index / sps

        if abs(t) < 1e-12:
            value = 1 + alpha * (4 / math.pi - 1)
        elif abs(abs(t) - 1 / (4 * alpha)) < 1e-8:
            value = alpha / math.sqrt(2) * (
                (1 + 2 / math.pi) *
                math.sin(math.pi / (4 * alpha))
                +
                (1 - 2 / math.pi) *
                math.cos(math.pi / (4 * alpha))
            )
        else:
            numerator = (
                math.sin(math.pi * t * (1 - alpha))
                +
                4 * alpha * t *
                math.cos(math.pi * t * (1 + alpha))
            )
            denominator = (
                math.pi * t *
                (1 - (4 * alpha * t) ** 2)
            )
            value = numerator / denominator

        taps.append(value)

    taps = np.asarray(taps, dtype=np.float64)
    return taps / math.sqrt(float(np.sum(taps ** 2)))


def sample_symbols(
    samples: np.ndarray,
    start: int,
    end: int,
    offset: float,
    sps: float,
) -> np.ndarray:
    positions = np.arange(start + offset, end - 1, sps)
    indices = np.floor(positions).astype(np.int64)
    fraction = positions - indices

    return (
        samples[indices] * (1 - fraction) +
        samples[indices + 1] * fraction
    )


def timing_search(
    samples: np.ndarray,
    start: int,
    end: int,
    sample_rate: float,
) -> dict:
    sps = sample_rate / SYMBOL_RATE
    candidates = []

    for phase_index in range(TIMING_PHASES):
        offset = phase_index / TIMING_PHASES * sps
        symbols = sample_symbols(
            samples,
            start,
            end,
            offset,
            sps,
        )

        if len(symbols) < 16:
            continue

        differential = symbols[1:] * np.conj(symbols[:-1])
        phases = np.angle(differential)
        sectors = np.mod(
            np.rint(phases / (math.pi / 4)).astype(int),
            8,
        )

        expected = sectors * (math.pi / 4)
        expected = np.where(
            expected > math.pi,
            expected - 2 * math.pi,
            expected,
        )

        errors = np.angle(
            np.exp(1j * (phases - expected))
        )

        phase_rms = float(
            np.sqrt(np.mean(errors ** 2))
        )

        cluster_score = float(np.clip(
            1 - phase_rms / (math.pi / 8),
            0,
            1,
        ))

        r8 = float(abs(np.mean(
            np.exp(1j * 8 * phases)
        )))

        observations = len(phases)
        per_trial_alpha = (
            FAMILY_FALSE_ALARM /
            TIMING_PHASES
        )

        threshold = min(
            1.0,
            math.sqrt(
                -math.log(per_trial_alpha) /
                observations
            ),
        )

        corrected_p = min(
            1.0,
            TIMING_PHASES *
            math.exp(
                -observations *
                r8 *
                r8
            ),
        )

        amplitude_cv = float(
            np.std(np.abs(symbols)) /
            max(np.mean(np.abs(symbols)), 1e-20)
        )

        candidates.append({
            "offset": offset,
            "symbols": symbols,
            "phases": phases,
            "sectors": sectors,
            "errors": errors,
            "phase_rms_deg": math.degrees(phase_rms),
            "cluster_score": cluster_score,
            "r8": r8,
            "r8_threshold": threshold,
            "corrected_p_value": corrected_p,
            "amplitude_cv": amplitude_cv,
            "metric": r8 - 0.05 * min(amplitude_cv, 2.0),
        })

    if not candidates:
        raise RuntimeError("No valid timing candidate.")

    best = max(candidates, key=lambda item: item["metric"])
    r8_values = np.asarray(
        [item["r8"] for item in candidates],
        dtype=np.float64,
    )

    median_r8 = float(np.median(r8_values))
    mad = float(np.median(np.abs(r8_values - median_r8)))
    robust_sigma = max(
        1.4826 * mad,
        1 / math.sqrt(
            2 * max(1, len(best["phases"]))
        ),
    )

    best["timing_median_r8"] = median_r8
    best["timing_contrast"] = best["r8"] - median_r8
    best["timing_robust_z"] = (
        best["timing_contrast"] /
        robust_sigma
    )
    return best


def terminal(status: str, diagnostic: bool, reason: str) -> int:
    print(json.dumps({
        "status": status,
        "diagnostic_only": diagnostic,
        "d8psk_candidate": False,
        "reason": reason,
    }, indent=2))
    return 2


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Statistical VDL2 D8PSK symbol diagnostics."
    )
    parser.add_argument(
        "metadata",
        help="Capture JSON metadata.",
    )
    parser.add_argument(
        "--diagnostic",
        action="store_true",
        help="Permit limited/continuous captures, but never classify them as normal candidates.",
    )
    args = parser.parse_args()

    metadata_path = Path(args.metadata).resolve()
    metadata = json.loads(
        metadata_path.read_text(encoding="utf-8")
    )

    limited = bool(metadata.get("limited", False))
    continuous = bool(
        metadata.get(
            "continuous_or_interference",
            False,
        )
    )

    if limited and not args.diagnostic:
        return terminal(
            "limited_capture_rejected",
            False,
            "Use --diagnostic to inspect this capture.",
        )

    if continuous and not args.diagnostic:
        return terminal(
            "continuous_or_interference_rejected",
            False,
            "Use --diagnostic to inspect this capture.",
        )

    iq_path = metadata_path.parent / metadata["iq_file"]
    sample_rate = float(metadata["sample_rate"])

    raw = np.fromfile(iq_path, dtype="<f4")
    if raw.size % 2:
        raise SystemExit("Invalid interleaved IQ file.")

    iq = raw[0::2] + 1j * raw[1::2]
    iq = iq - iq.mean()

    bounded = find_bounded_burst(iq, sample_rate)
    if bounded is None:
        return terminal(
            "no_bounded_burst",
            args.diagnostic,
            "No region had both leading and trailing noise boundaries.",
        )

    start, end, snr_db = bounded
    offset_hz = spectral_centroid(
        iq[start:end],
        sample_rate,
    )

    index = np.arange(len(iq))
    corrected = iq * np.exp(
        -1j *
        2 *
        math.pi *
        offset_hz *
        index /
        sample_rate
    )

    filtered = np.convolve(
        corrected,
        rrc_taps(
            sample_rate,
            SYMBOL_RATE,
            ALPHA,
            10,
        ),
        mode="same",
    )

    timing = timing_search(
        filtered,
        start,
        end,
        sample_rate,
    )

    minimum_contrast = max(
        0.04,
        timing["r8_threshold"] * 0.25,
    )

    significant = (
        timing["r8"] >=
            timing["r8_threshold"]
        and
        timing["corrected_p_value"] <=
            FAMILY_FALSE_ALARM
    )

    timing_valid = (
        timing["timing_contrast"] >=
            minimum_contrast
        and
        timing["timing_robust_z"] >=
            3.0
    )

    raw_candidate = (
        len(timing["symbols"]) >= 80
        and significant
        and timing_valid
        and timing["amplitude_cv"] <= 1.20
        and abs(offset_hz) <= 5_000
        and snr_db >= 5.0
    )

    candidate = (
        raw_candidate
        and not args.diagnostic
        and not limited
        and not continuous
    )

    if candidate:
        status = "VDL2-SYMBOL-CANDIDATE"
    elif args.diagnostic:
        status = "diagnostic_only"
    elif not significant:
        status = "r8_not_significant"
    elif not timing_valid:
        status = "timing_contrast_insufficient"
    else:
        status = "symbol_structure_not_confirmed"

    bit_preview = "".join(
        GRAY_BITS[int(sector)]
        for sector in timing["sectors"][:128]
    )

    result = {
        "status": status,
        "d8psk_candidate": candidate,
        "diagnostic_only": args.diagnostic,
        "symbol_rate": SYMBOL_RATE,
        "samples_per_symbol": sample_rate / SYMBOL_RATE,
        "best_burst_start_ms": start / sample_rate * 1000,
        "best_burst_duration_ms": (end - start) / sample_rate * 1000,
        "estimated_snr_db": snr_db,
        "estimated_frequency_offset_hz": offset_hz,
        "timing_offset_samples": timing["offset"],
        "symbol_count": len(timing["symbols"]),
        "r8": timing["r8"],
        "r8_threshold": timing["r8_threshold"],
        "r8_corrected_p_value": timing["corrected_p_value"],
        "timing_median_r8": timing["timing_median_r8"],
        "timing_contrast": timing["timing_contrast"],
        "minimum_timing_contrast": minimum_contrast,
        "timing_robust_z": timing["timing_robust_z"],
        "differential_phase_rms_deg": timing["phase_rms_deg"],
        "legacy_cluster_score": timing["cluster_score"],
        "amplitude_cv": timing["amplitude_cv"],
        "bit_preview": bit_preview,
        "warning": (
            "Bits are still scrambled. Unique word, descrambling, "
            "Reed-Solomon FEC and AVLC are not decoded."
        ),
    }

    output = metadata_path.with_suffix(
        ".offline-d8psk-v0103.json"
    )

    output.write_text(
        json.dumps(result, indent=2),
        encoding="utf-8",
    )

    print(json.dumps(result, indent=2))
    print(f"\nSaved: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
