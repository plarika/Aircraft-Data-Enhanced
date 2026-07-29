#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Offline VDL2 IQ laboratory.

Reads stereo 16-bit WAV where left=I and right=Q, channelizes selected
airband frequencies, measures power/noise, and detects energy bursts.

This tool does NOT decode D8PSK/AVLC yet. Its purpose is to produce a
reproducible RF/DSP baseline for decoder development.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
import wave
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
from scipy import signal


DEFAULT_CHANNELS_HZ = (136_725_000, 136_775_000, 136_875_000, 136_975_000)


@dataclass(frozen=True)
class Burst:
    channel_hz: int
    start_s: float
    end_s: float
    duration_ms: float
    peak_dbfs: float
    mean_dbfs: float
    noise_dbfs: float
    snr_db: float


@dataclass(frozen=True)
class ChannelSummary:
    channel_hz: int
    offset_hz: int
    frames: int
    duration_s: float
    median_dbfs: float
    p95_dbfs: float
    peak_dbfs: float
    estimated_noise_dbfs: float
    burst_count: int
    burst_time_s: float


def db10(value: np.ndarray | float) -> np.ndarray | float:
    return 10.0 * np.log10(np.maximum(value, 1e-20))


def parse_channels(value: str) -> tuple[int, ...]:
    channels: list[int] = []
    for raw in value.split(","):
        raw = raw.strip()
        if not raw:
            continue
        number = float(raw)
        hz = int(round(number * 1_000_000 if number < 1_000_000 else number))
        if hz <= 0:
            raise argparse.ArgumentTypeError(f"Invalid channel: {raw}")
        channels.append(hz)
    if not channels:
        raise argparse.ArgumentTypeError("At least one channel is required.")
    return tuple(channels)


def validate_wav(path: Path) -> tuple[int, int, int, float]:
    with wave.open(str(path), "rb") as wav:
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
        sample_rate = wav.getframerate()
        frames = wav.getnframes()

    if channels != 2:
        raise ValueError(f"Expected 2 channels (I/Q), found {channels}.")
    if sample_width != 2:
        raise ValueError(f"Expected 16-bit PCM IQ, found {sample_width * 8}-bit.")
    if sample_rate < 48_000:
        raise ValueError(f"Sample rate too low for IQ analysis: {sample_rate}.")
    return sample_rate, frames, channels, frames / sample_rate


def iter_iq(path: Path, block_complex: int) -> Iterable[np.ndarray]:
    with wave.open(str(path), "rb") as wav:
        scale = np.float32(1.0 / 32768.0)
        while True:
            raw = wav.readframes(block_complex)
            if not raw:
                break
            values = np.frombuffer(raw, dtype="<i2")
            if values.size < 2:
                break
            values = values[: values.size - (values.size % 2)].reshape(-1, 2)
            iq = (values[:, 0].astype(np.float32) +
                  1j * values[:, 1].astype(np.float32)) * scale
            yield iq.astype(np.complex64, copy=False)


def design_lowpass(input_rate: int, cutoff_hz: float, decimation: int) -> np.ndarray:
    output_rate = input_rate / decimation
    nyquist = input_rate / 2.0
    if cutoff_hz >= output_rate / 2:
        raise ValueError("Cutoff must be below output Nyquist.")
    transition = max(2_000.0, cutoff_hz * 0.25)
    width = transition / nyquist
    taps = int(math.ceil(6.6 / width))
    taps = max(63, min(taps | 1, 4095))
    return signal.firwin(taps, cutoff_hz / nyquist, window="hamming").astype(np.float32)


class Channelizer:
    def __init__(
        self,
        sample_rate: int,
        center_hz: int,
        channel_hz: int,
        output_rate: int,
        bandwidth_hz: float,
    ) -> None:
        if sample_rate % output_rate != 0:
            raise ValueError(
                f"Input sample rate {sample_rate} must be divisible by output rate {output_rate}."
            )
        self.sample_rate = sample_rate
        self.center_hz = center_hz
        self.channel_hz = channel_hz
        self.offset_hz = channel_hz - center_hz
        self.decimation = sample_rate // output_rate
        self.output_rate = output_rate
        self.taps = design_lowpass(sample_rate, bandwidth_hz / 2.0, self.decimation)
        self.zi = np.zeros(len(self.taps) - 1, dtype=np.complex64)
        self.phase = 0.0
        self.phase_step = -2.0 * math.pi * self.offset_hz / sample_rate

    def process(self, iq: np.ndarray) -> np.ndarray:
        count = iq.size
        phase_vector = self.phase + self.phase_step * np.arange(count, dtype=np.float64)
        mixed = iq * np.exp(1j * phase_vector).astype(np.complex64)
        self.phase = float((phase_vector[-1] + self.phase_step) % (2.0 * math.pi))
        filtered, self.zi = signal.lfilter(self.taps, [1.0], mixed, zi=self.zi)
        return filtered[:: self.decimation].astype(np.complex64, copy=False)


class BurstDetector:
    def __init__(
        self,
        channel_hz: int,
        frame_s: float,
        threshold_db: float,
        min_burst_ms: float,
        hang_ms: float,
    ) -> None:
        self.channel_hz = channel_hz
        self.frame_s = frame_s
        self.threshold_db = threshold_db
        self.min_frames = max(1, math.ceil(min_burst_ms / 1000.0 / frame_s))
        self.hang_frames = max(1, math.ceil(hang_ms / 1000.0 / frame_s))
        self.power_db: list[float] = []
        self.bursts: list[Burst] = []

    def finalize(self) -> tuple[list[Burst], float]:
        if not self.power_db:
            return [], -120.0

        powers = np.asarray(self.power_db, dtype=np.float64)
        # Robust noise estimate from lower 60% of frames.
        cutoff = np.percentile(powers, 60)
        noise_values = powers[powers <= cutoff]
        noise = float(np.median(noise_values)) if noise_values.size else float(np.median(powers))
        active = powers >= noise + self.threshold_db

        start: int | None = None
        last_active: int | None = None
        for idx, is_active in enumerate(active):
            if is_active:
                if start is None:
                    start = idx
                last_active = idx
            elif start is not None and last_active is not None:
                if idx - last_active > self.hang_frames:
                    self._close_burst(start, last_active, powers, noise)
                    start = None
                    last_active = None

        if start is not None and last_active is not None:
            self._close_burst(start, last_active, powers, noise)

        return self.bursts, noise

    def _close_burst(
        self,
        start: int,
        end: int,
        powers: np.ndarray,
        noise: float,
    ) -> None:
        frames = end - start + 1
        if frames < self.min_frames:
            return
        segment = powers[start : end + 1]
        start_s = start * self.frame_s
        end_s = (end + 1) * self.frame_s
        self.bursts.append(
            Burst(
                channel_hz=self.channel_hz,
                start_s=round(start_s, 6),
                end_s=round(end_s, 6),
                duration_ms=round((end_s - start_s) * 1000.0, 3),
                peak_dbfs=round(float(np.max(segment)), 3),
                mean_dbfs=round(float(np.mean(segment)), 3),
                noise_dbfs=round(noise, 3),
                snr_db=round(float(np.max(segment) - noise), 3),
            )
        )


def analyze(args: argparse.Namespace) -> dict:
    wav_path = Path(args.input).expanduser().resolve()
    if not wav_path.exists():
        raise FileNotFoundError(wav_path)

    sample_rate, total_frames, _, duration_s = validate_wav(wav_path)
    center_hz = int(round(args.center_mhz * 1_000_000))
    channels_hz = args.channels

    for channel in channels_hz:
        offset = abs(channel - center_hz)
        if offset + args.bandwidth_hz / 2 >= sample_rate / 2:
            raise ValueError(
                f"Channel {channel / 1e6:.3f} MHz is outside the IQ passband."
            )

    channelizers = {
        frequency: Channelizer(
            sample_rate=sample_rate,
            center_hz=center_hz,
            channel_hz=frequency,
            output_rate=args.output_rate,
            bandwidth_hz=args.bandwidth_hz,
        )
        for frequency in channels_hz
    }

    frame_samples = max(1, int(round(args.frame_ms / 1000.0 * args.output_rate)))
    frame_s = frame_samples / args.output_rate
    detectors = {
        frequency: BurstDetector(
            channel_hz=frequency,
            frame_s=frame_s,
            threshold_db=args.threshold_db,
            min_burst_ms=args.min_burst_ms,
            hang_ms=args.hang_ms,
        )
        for frequency in channels_hz
    }
    pending = {frequency: np.empty(0, dtype=np.complex64) for frequency in channels_hz}

    processed = 0
    for iq in iter_iq(wav_path, args.block_complex):
        processed += iq.size
        for frequency, channelizer in channelizers.items():
            channel = channelizer.process(iq)
            if pending[frequency].size:
                channel = np.concatenate((pending[frequency], channel))
            complete = channel.size // frame_samples
            if complete:
                framed = channel[: complete * frame_samples].reshape(complete, frame_samples)
                power = np.mean(np.abs(framed) ** 2, axis=1)
                detectors[frequency].power_db.extend(db10(power).tolist())
            pending[frequency] = channel[complete * frame_samples :]

        if args.progress and processed % (args.block_complex * 8) < args.block_complex:
            percent = min(100.0, processed / total_frames * 100.0)
            print(f"\rProcessing: {percent:6.2f}%", end="", file=sys.stderr, flush=True)

    if args.progress:
        print(file=sys.stderr)

    all_bursts: list[Burst] = []
    summaries: list[ChannelSummary] = []
    for frequency in channels_hz:
        detector = detectors[frequency]
        bursts, noise = detector.finalize()
        all_bursts.extend(bursts)
        powers = np.asarray(detector.power_db, dtype=np.float64)
        summaries.append(
            ChannelSummary(
                channel_hz=frequency,
                offset_hz=frequency - center_hz,
                frames=int(powers.size),
                duration_s=round(powers.size * frame_s, 6),
                median_dbfs=round(float(np.median(powers)), 3) if powers.size else -120.0,
                p95_dbfs=round(float(np.percentile(powers, 95)), 3) if powers.size else -120.0,
                peak_dbfs=round(float(np.max(powers)), 3) if powers.size else -120.0,
                estimated_noise_dbfs=round(noise, 3),
                burst_count=len(bursts),
                burst_time_s=round(sum(b.duration_ms for b in bursts) / 1000.0, 6),
            )
        )

    output_dir = Path(args.output).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    with (output_dir / "channel_summary.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=ChannelSummary.__dataclass_fields__.keys())
        writer.writeheader()
        writer.writerows(asdict(item) for item in summaries)

    with (output_dir / "bursts.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=Burst.__dataclass_fields__.keys())
        writer.writeheader()
        writer.writerows(asdict(item) for item in all_bursts)

    report = {
        "input": {
            "path": str(wav_path),
            "center_hz": center_hz,
            "sample_rate": sample_rate,
            "total_complex_samples": total_frames,
            "duration_s": duration_s,
            "format": "stereo PCM16 IQ (L=I, R=Q)",
        },
        "analysis": {
            "channels_hz": list(channels_hz),
            "output_rate": args.output_rate,
            "bandwidth_hz": args.bandwidth_hz,
            "frame_ms": args.frame_ms,
            "threshold_db": args.threshold_db,
            "min_burst_ms": args.min_burst_ms,
            "hang_ms": args.hang_ms,
            "note": "Energy detector only; no D8PSK/AVLC decoding.",
        },
        "channels": [asdict(item) for item in summaries],
        "bursts": [asdict(item) for item in all_bursts],
    }
    (output_dir / "summary.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )

    print(f"Analysis complete: {output_dir}")
    for summary in summaries:
        print(
            f"{summary.channel_hz / 1e6:.3f} MHz: "
            f"{summary.burst_count} bursts, peak {summary.peak_dbfs:.1f} dBFS, "
            f"noise {summary.estimated_noise_dbfs:.1f} dBFS"
        )
    return report


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="Stereo PCM16 IQ WAV.")
    parser.add_argument("--center-mhz", required=True, type=float, help="WAV center frequency in MHz.")
    parser.add_argument(
        "--channels",
        type=parse_channels,
        default=DEFAULT_CHANNELS_HZ,
        help="Comma-separated frequencies in Hz or MHz.",
    )
    parser.add_argument("--output", default="reports/offline", help="Output directory.")
    parser.add_argument("--output-rate", type=int, default=100_000)
    parser.add_argument("--bandwidth-hz", type=float, default=25_000)
    parser.add_argument("--frame-ms", type=float, default=5.0)
    parser.add_argument("--threshold-db", type=float, default=7.0)
    parser.add_argument("--min-burst-ms", type=float, default=10.0)
    parser.add_argument("--hang-ms", type=float, default=10.0)
    parser.add_argument("--block-complex", type=int, default=262_144)
    parser.add_argument("--progress", action="store_true")
    return parser


def main() -> int:
    try:
        args = build_parser().parse_args()
        analyze(args)
        return 0
    except KeyboardInterrupt:
        print("Interrupted.", file=sys.stderr)
        return 130
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
