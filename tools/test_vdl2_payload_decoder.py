#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import importlib.util
import random
from pathlib import Path


def load_module():
    path = Path(__file__).resolve().parent / "decode_vdl2_payload.py"
    spec = importlib.util.spec_from_file_location("payload", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load payload decoder")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    p = load_module()

    # Full RS regression: two erasures plus two unknown symbol errors.
    random.seed(1200)
    for _ in range(50):
        data = [random.randrange(256) for _ in range(249)]
        codeword = data + p.rs_encode(data)
        reference = codeword.copy()
        erasures = [253, 254]
        codeword[253] = 0
        codeword[254] = 0
        for position in random.sample(range(249), 2):
            codeword[position] ^= random.randrange(1, 256)
        corrected, _ = p.rs_decode(codeword, erasures)
        assert corrected >= 0
        assert codeword == reference

    # FCS-valid AVLC I-frame, long enough to transmit all six parity bytes.
    destination = p.encode_address(0xABCDEF, 1, 0)
    source = p.encode_address(0x123456, 5, 0)
    info = b"\xFF\xFF\x01" + b"SYNTHETIC VDL2 ACARS PAYLOAD " * 3
    frame = p.append_fcs(destination + source + bytes((0x00,)) + info)
    assert p.calculate_fcs(frame) == p.GOOD_FCS

    clear_payload = list(p.HDLC_FLAG)
    clear_payload += p.stuff_bits(p.octets_to_bits_lsb(list(frame)))
    clear_payload += list(p.HDLC_FLAG)
    encoded = p.encode_payload_for_test(clear_payload)

    # Introduce one unknown symbol error; six parity bytes can correct it.
    corrupted = encoded.copy()
    byte_position = 40
    for bit in (0, 2, 5):
        corrupted[byte_position * 8 + bit] ^= 1

    clear_header = [0] * p.HEADER_BITS
    result = p.decode_payload(clear_header + corrupted, len(clear_payload))
    assert result["status"] == "AVLC-VALID", result
    assert result["reed_solomon_valid"]
    assert result["fcs_valid_frames"] == 1
    assert result["frames"][0]["icao"] == "ABCDEF"
    assert result["frames"][0]["direction"] == "Ground → Air"
    assert result["frames"][0]["information_protocol"] == "ACARS"

    # Shortened final RS row with punctured parity must reconstruct clean data.
    short_frame = p.append_fcs(
        p.encode_address(0x010203, 1, 0)
        + p.encode_address(0x112233, 5, 0)
        + bytes((0x03,))
    )
    short_payload = list(p.HDLC_FLAG)
    short_payload += p.stuff_bits(p.octets_to_bits_lsb(list(short_frame)))
    short_payload += list(p.HDLC_FLAG)
    short_encoded = p.encode_payload_for_test(short_payload)
    short_result = p.decode_payload(
        [0] * p.HEADER_BITS + short_encoded,
        len(short_payload),
    )
    assert short_result["status"] == "AVLC-VALID", short_result
    assert short_result["frames"][0]["icao"] == "010203"

    print(
        "[OK] VDL2 payload regression passed: RS(255,249), shortened rows, "
        "deinterleaving, HDLC unstuffing, FCS and AVLC aircraft ICAO."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
