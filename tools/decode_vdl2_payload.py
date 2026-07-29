#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import importlib.util
import json
import math
from pathlib import Path
from typing import Any

import numpy as np


MM = 8
NN = 255
GF_POLY = 0x187
FCR = 120
PRIM = 1
NROOTS = 6
A0 = NN
HEADER_BITS = 25
DATA_PER_BLOCK = 249
GOOD_FCS = 0xF0B8
HDLC_FLAG = (0, 1, 1, 1, 1, 1, 1, 0)


def load_header_decoder() -> Any:
    path = Path(__file__).resolve().parent / "decode_vdl2_header.py"
    spec = importlib.util.spec_from_file_location("vdl2_header", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load decode_vdl2_header.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


HEADER = load_header_decoder()


def build_field_tables() -> tuple[list[int], list[int]]:
    alpha = [0] * (NN + 1)
    index = [0] * (NN + 1)
    shift_register = 1

    for exponent in range(NN):
        index[shift_register] = exponent
        alpha[exponent] = shift_register
        shift_register <<= 1
        if shift_register & (1 << MM):
            shift_register ^= GF_POLY
        shift_register &= NN

    index[0] = A0
    alpha[A0] = 0
    return alpha, index


ALPHA_TO, INDEX_OF = build_field_tables()


def mod_nn(value: int) -> int:
    while value >= NN:
        value -= NN
        value = (value >> MM) + (value & NN)
    return value


def generator_polynomial() -> list[int]:
    polynomial = [0] * (NROOTS + 1)
    polynomial[0] = 1
    root = FCR * PRIM

    for root_index in range(NROOTS):
        polynomial[root_index + 1] = 1
        for coefficient in range(root_index, 0, -1):
            if polynomial[coefficient] != 0:
                polynomial[coefficient] = (
                    polynomial[coefficient - 1]
                    ^ ALPHA_TO[
                        mod_nn(INDEX_OF[polynomial[coefficient]] + root)
                    ]
                )
            else:
                polynomial[coefficient] = polynomial[coefficient - 1]

        polynomial[0] = ALPHA_TO[
            mod_nn(INDEX_OF[polynomial[0]] + root)
        ]
        root += PRIM

    return [INDEX_OF[value] for value in polynomial]


GENERATOR = generator_polynomial()


def rs_encode(data: list[int]) -> list[int]:
    if len(data) != DATA_PER_BLOCK:
        raise ValueError("RS encoder requires 249 data symbols")

    parity = [0] * NROOTS

    for value in data:
        feedback = INDEX_OF[value ^ parity[0]]

        if feedback != A0:
            for index in range(1, NROOTS):
                parity[index] ^= ALPHA_TO[
                    mod_nn(feedback + GENERATOR[NROOTS - index])
                ]

        parity = parity[1:] + [0]

        if feedback != A0:
            parity[-1] = ALPHA_TO[
                mod_nn(feedback + GENERATOR[0])
            ]

    return parity


def rs_decode(
    data: list[int],
    erasure_positions: list[int] | None = None,
) -> tuple[int, list[int]]:
    if len(data) != NN:
        raise ValueError("RS decoder requires 255 symbols")

    erasures = list(erasure_positions or [])
    no_erasures = len(erasures)

    syndrome = [data[0]] * NROOTS

    for symbol_index in range(1, NN):
        for root_index in range(NROOTS):
            if syndrome[root_index] == 0:
                syndrome[root_index] = data[symbol_index]
            else:
                syndrome[root_index] = (
                    data[symbol_index]
                    ^ ALPHA_TO[
                        mod_nn(
                            INDEX_OF[syndrome[root_index]]
                            + (FCR + root_index) * PRIM
                        )
                    ]
                )

    syndrome_error = 0
    for index in range(NROOTS):
        syndrome_error |= syndrome[index]
        syndrome[index] = INDEX_OF[syndrome[index]]

    if syndrome_error == 0:
        return 0, []

    locator = [0] * (NROOTS + 1)
    b = [0] * (NROOTS + 1)
    temporary = [0] * (NROOTS + 1)
    evaluator = [0] * (NROOTS + 1)
    roots = [0] * NROOTS
    register = [0] * (NROOTS + 1)
    locations = [0] * NROOTS
    locator[0] = 1

    if no_erasures > 0:
        locator[1] = ALPHA_TO[
            mod_nn(PRIM * (NN - 1 - erasures[0]))
        ]

        for erasure_index in range(1, no_erasures):
            u = mod_nn(PRIM * (NN - 1 - erasures[erasure_index]))
            for coefficient in range(erasure_index + 1, 0, -1):
                previous = INDEX_OF[locator[coefficient - 1]]
                if previous != A0:
                    locator[coefficient] ^= ALPHA_TO[mod_nn(u + previous)]

    for index in range(NROOTS + 1):
        b[index] = INDEX_OF[locator[index]]

    step = no_erasures
    degree_estimate = no_erasures

    while True:
        step += 1
        if step > NROOTS:
            break

        discrepancy = 0
        for index in range(step):
            if locator[index] != 0 and syndrome[step - index - 1] != A0:
                discrepancy ^= ALPHA_TO[
                    mod_nn(INDEX_OF[locator[index]] + syndrome[step - index - 1])
                ]

        discrepancy = INDEX_OF[discrepancy]

        if discrepancy == A0:
            b[1:] = b[:-1]
            b[0] = A0
            continue

        temporary[0] = locator[0]
        for index in range(NROOTS):
            temporary[index + 1] = (
                locator[index + 1]
                ^ (
                    ALPHA_TO[mod_nn(discrepancy + b[index])]
                    if b[index] != A0
                    else 0
                )
            )

        if 2 * degree_estimate <= step + no_erasures - 1:
            degree_estimate = step + no_erasures - degree_estimate
            for index in range(NROOTS + 1):
                b[index] = (
                    A0
                    if locator[index] == 0
                    else mod_nn(INDEX_OF[locator[index]] - discrepancy + NN)
                )
        else:
            b[1:] = b[:-1]
            b[0] = A0

        locator = temporary.copy()

    locator_degree = 0
    for index in range(NROOTS + 1):
        locator[index] = INDEX_OF[locator[index]]
        if locator[index] != A0:
            locator_degree = index

    register[1:] = locator[1:]
    count = 0
    location_index = 0

    for index in range(1, NN + 1):
        evaluation = 1
        for coefficient in range(locator_degree, 0, -1):
            if register[coefficient] != A0:
                register[coefficient] = mod_nn(
                    register[coefficient] + coefficient
                )
                evaluation ^= ALPHA_TO[register[coefficient]]

        if evaluation == 0:
            roots[count] = index
            locations[count] = location_index
            count += 1
            if count == locator_degree:
                break

        location_index = mod_nn(location_index + 1)

    if locator_degree != count:
        return -1, []

    evaluator_degree = locator_degree - 1
    for index in range(evaluator_degree + 1):
        value = 0
        for coefficient in range(index, -1, -1):
            if (
                syndrome[index - coefficient] != A0
                and locator[coefficient] != A0
            ):
                value ^= ALPHA_TO[
                    mod_nn(
                        syndrome[index - coefficient]
                        + locator[coefficient]
                    )
                ]
        evaluator[index] = INDEX_OF[value]

    for error_index in range(count - 1, -1, -1):
        numerator_one = 0
        for index in range(evaluator_degree, -1, -1):
            if evaluator[index] != A0:
                numerator_one ^= ALPHA_TO[
                    mod_nn(evaluator[index] + index * roots[error_index])
                ]

        numerator_two = ALPHA_TO[
            mod_nn(roots[error_index] * (FCR - 1) + NN)
        ]

        denominator = 0
        start = min(locator_degree, NROOTS - 1) & ~1
        for index in range(start, -1, -2):
            if locator[index + 1] != A0:
                denominator ^= ALPHA_TO[
                    mod_nn(locator[index + 1] + index * roots[error_index])
                ]

        if denominator == 0:
            return -1, []

        location = locations[error_index]
        if numerator_one != 0:
            data[location] ^= ALPHA_TO[
                mod_nn(
                    INDEX_OF[numerator_one]
                    + INDEX_OF[numerator_two]
                    + NN
                    - INDEX_OF[denominator]
                )
            ]

    return count, locations[:count]


def get_fec_octet_count(data_octets: int) -> int:
    if data_octets < 3:
        return 0
    if data_octets < 31:
        return 2
    if data_octets < 68:
        return 4
    return 6


def bits_to_octets_lsb(bits: list[int]) -> list[int]:
    return [
        sum(bits[offset + bit] << bit for bit in range(8))
        for offset in range(0, len(bits), 8)
    ]


def octets_to_bits_lsb(octets: list[int]) -> list[int]:
    return [
        (value >> bit) & 1
        for value in octets
        for bit in range(8)
    ]


def deinterleave_into(
    values: list[int],
    row_count: int,
    output: list[list[int]],
    fill_width: int,
    offset: int,
) -> None:
    if row_count <= 0:
        if values:
            raise ValueError("deinterleaver has data but zero rows")
        return

    last_row_length = len(values) - (row_count - 1) * fill_width
    if last_row_length < 0 or last_row_length > fill_width:
        raise ValueError("invalid deinterleaver last-row length")

    row = 0
    column = offset
    last_row_end = last_row_length + offset

    for value in values:
        if row == row_count - 1 and column >= last_row_end:
            output[row][column] = 0
            row = 0
            column += 1

        if column >= NN:
            raise ValueError("deinterleaver exceeded row")

        output[row][column] = value
        row += 1

        if row == row_count:
            row = 0
            column += 1


def interleave_from(
    rows: list[list[int]],
    row_count: int,
    output_octets: int,
    fill_width: int,
    offset: int,
) -> list[int]:
    if row_count <= 0:
        return []

    last_row_length = output_octets - (row_count - 1) * fill_width
    row = 0
    column = offset
    last_row_end = last_row_length + offset
    output: list[int] = []

    for _ in range(output_octets):
        if row == row_count - 1 and column >= last_row_end:
            row = 0
            column += 1

        output.append(rows[row][column])
        row += 1

        if row == row_count:
            row = 0
            column += 1

    return output

def calculate_fcs(data: bytes) -> int:
    crc = 0xFFFF
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = ((crc >> 1) ^ 0x8408) if (crc & 1) else (crc >> 1)
    return crc & 0xFFFF


def append_fcs(data: bytes) -> bytes:
    value = calculate_fcs(data) ^ 0xFFFF
    return data + bytes((value & 0xFF, value >> 8))


def reverse_bits(value: int, count: int) -> int:
    result = 0
    for _ in range(count):
        result = (result << 1) | (value & 1)
        value >>= 1
    return result


def parse_address(data: bytes) -> dict[str, Any]:
    encoded = (
        (data[0] >> 1)
        | (data[1] << 6)
        | (data[2] << 13)
        | ((data[3] & 0xFE) << 20)
    )
    decoded = reverse_bits(encoded, 28)
    address = decoded & 0xFFFFFF
    address_type = (decoded >> 24) & 7
    status_bit = bool((decoded >> 27) & 1)
    names = {
        1: ("Aircraft", "AIR"),
        4: ("Ground administrative", "GS-ADMIN"),
        5: ("Ground delegated", "GS"),
        7: ("All stations", "ALL"),
    }
    name, prefix = names.get(address_type, (f"Type {address_type}", f"T{address_type}"))
    return {
        "address": address,
        "address_hex": f"{address:06X}",
        "type": address_type,
        "type_name": name,
        "status_bit": status_bit,
        "display": f"{prefix}:{address:06X}",
    }


def decode_frame_type(control: int, source_status: bool) -> tuple[str, str]:
    if (control & 1) == 0:
        send_sequence = (control >> 1) & 7
        poll = (control >> 4) & 1
        receive_sequence = (control >> 5) & 7
        return "AVLC I-frame", f"I S{send_sequence} R{receive_sequence} P{poll}"

    if (control & 3) == 1:
        function = (control >> 2) & 3
        name = ("RR", "RNR", "REJ", "SREJ")[function]
        poll_final = (control >> 4) & 1
        receive_sequence = (control >> 5) & 7
        return "AVLC S-frame", f"{name} R{receive_sequence} PF{poll_final}"

    modifier = (control >> 2) & 0x3F
    code = modifier & 0x3B
    name = {
        0x00: "UI",
        0x03: "DM",
        0x10: "DISC",
        0x18: "UA",
        0x21: "FRMR",
        0x2B: "XID",
        0x38: "TEST",
    }.get(code, f"U-{code:02X}")
    return "AVLC U-frame", f"{name} · {'Response' if source_status else 'Command'}"


def calculate_acars_crc(data: bytes) -> int:
    crc = 0
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = ((crc >> 1) ^ 0x8408) if (crc & 1) else (crc >> 1)
    return crc & 0xFFFF


def parse_acars_envelope(raw: bytes) -> dict[str, Any] | None:
    if len(raw) < 16 or raw[-1] != 0x7F:
        return None

    crc_valid = calculate_acars_crc(raw[:-1]) == 0
    frame = bytes(value & 0x7F for value in raw)
    logical_length = len(frame) - 1 - 2

    if logical_length < 13:
        return None

    marker = frame[logical_length - 1]
    if marker not in (0x03, 0x17):
        return None

    final_block = marker == 0x03
    logical_length -= 1
    offset = 0
    mode = chr(frame[offset]) if 32 <= frame[offset] <= 126 else "."
    offset += 1
    raw_registration = frame[offset:offset + 7].decode("ascii", errors="replace")
    offset += 7

    if offset >= logical_length:
        return None

    ack_value = frame[offset]
    acknowledgement = "!" if ack_value == 0x15 else "^" if ack_value == 0x06 else chr(ack_value)
    offset += 1

    if offset + 2 > logical_length:
        return None

    label_first = chr(frame[offset]) if 32 <= frame[offset] <= 126 else "."
    label_second = "d" if frame[offset + 1] == 0x7F else chr(frame[offset + 1])
    label = label_first + label_second
    offset += 2

    if offset >= logical_length:
        return None

    block_id = chr(frame[offset]) if frame[offset] else " "
    offset += 1
    downlink = "0" <= block_id <= "9"

    if offset >= logical_length:
        if downlink:
            return None
        return {
            "parsed": True,
            "crc_valid": crc_valid,
            "final_block": final_block,
            "more_blocks": not final_block,
            "mode": mode,
            "registration": raw_registration.strip().lstrip("."),
            "raw_registration": raw_registration.strip(),
            "acknowledgement": acknowledgement,
            "label": label.strip(),
            "block_id": block_id.strip(),
            "message_number": "",
            "message_sequence": "",
            "flight_id": "",
            "text": "",
            "status": "acars_empty_uplink",
        }

    if frame[offset] != 0x02:
        return None
    offset += 1

    message_number = ""
    message_sequence = ""
    flight_id = ""

    if downlink:
        if logical_length - offset < 10:
            return None
        message_number = frame[offset:offset + 3].decode("ascii", errors="replace").strip()
        message_sequence = chr(frame[offset + 3]).strip()
        offset += 4
        flight_id = frame[offset:offset + 6].decode("ascii", errors="replace").strip()
        offset += 6

    text = "".join(
        chr(value) if 32 <= value <= 126 else " " if value in (10, 13) else "."
        for value in frame[offset:logical_length]
    ).strip()

    return {
        "parsed": True,
        "crc_valid": crc_valid,
        "final_block": final_block,
        "more_blocks": not final_block,
        "mode": mode,
        "registration": raw_registration.strip().lstrip("."),
        "raw_registration": raw_registration.strip(),
        "acknowledgement": acknowledgement,
        "label": label.strip(),
        "block_id": block_id.strip(),
        "message_number": message_number,
        "message_sequence": message_sequence,
        "message_id": message_number + message_sequence if message_number else "",
        "flight_id": flight_id,
        "text": text,
        "status": "acars_valid" if crc_valid else "acars_crc_warning",
    }


def parse_avlc(frame: bytes, index: int) -> dict[str, Any] | None:
    if len(frame) < 11:
        return None

    destination = parse_address(frame[0:4])
    source = parse_address(frame[4:8])
    control = frame[8]
    information = frame[9:-2]
    frame_type, label = decode_frame_type(control, source["status_bit"])
    direction = (
        "Air → Ground"
        if source["type"] == 1
        else "Ground → Air"
        if destination["type"] == 1
        else "Ground/Unknown"
    )
    icao = (
        source["address_hex"]
        if source["type"] == 1
        else destination["address_hex"]
        if destination["type"] == 1
        else ""
    )
    acars_envelope = information.startswith(b"\xFF\xFF\x01")
    acars = parse_acars_envelope(information[3:]) if acars_envelope else None
    protocol = (
        "ACARS"
        if acars_envelope
        else "X.25/Other"
        if information
        else "No information"
    )
    printable = information[3:] if acars_envelope else information
    preview = "".join(chr(value) if 32 <= value <= 126 else "." for value in printable[:96]).strip(".")
    text = f"{frame_type} · {protocol} · {len(information)} information octets"
    if acars is not None:
        parts = ["ACARS"]
        if acars["registration"]:
            parts.append(f"Reg {acars['registration']}")
        if acars["flight_id"]:
            parts.append(f"Flight {acars['flight_id']}")
        if acars["label"]:
            parts.append(f"Label {acars['label']}")
        if acars.get("message_id"):
            parts.append(f"Msg {acars['message_id']}")
        parts.append("CRC OK" if acars["crc_valid"] else "CRC warning")
        if acars["text"]:
            parts.append(acars["text"])
        text = " · ".join(parts)
    elif preview:
        text += f" · {preview}"

    return {
        "index": index,
        "fcs_valid": True,
        "fcs_residual": GOOD_FCS,
        "length_octets": len(frame),
        "direction": direction,
        "icao": icao,
        "source": source,
        "destination": destination,
        "frame_type": frame_type,
        "label": label,
        "information_protocol": protocol,
        "text": text,
        "raw_hex": frame.hex().upper(),
        "acars": acars,
    }


def find_flags(bits: list[int]) -> list[int]:
    flags: list[int] = []
    index = 0
    while index <= len(bits) - 8:
        if tuple(bits[index:index + 8]) == HDLC_FLAG:
            flags.append(index)
            index += 8
        else:
            index += 1
    return flags


def unstuff(bits: list[int]) -> list[int] | None:
    output: list[int] = []
    ones = 0
    index = 0
    while index < len(bits):
        bit = bits[index] & 1
        output.append(bit)
        if bit == 0:
            ones = 0
        else:
            ones += 1
            if ones == 5:
                index += 1
                if index >= len(bits) or bits[index] != 0:
                    return None
                ones = 0
        index += 1
    return output


def extract_hdlc_frames(bits: list[int]) -> tuple[list[bytes], int]:
    flags = find_flags(bits)
    frames: list[bytes] = []
    errors = 0

    for first, second in zip(flags, flags[1:]):
        raw = bits[first + 8:second]
        if not raw:
            continue
        clear = unstuff(raw)
        if clear is None or len(clear) < 88 or len(clear) % 8:
            errors += 1
            continue
        frames.append(bytes(bits_to_octets_lsb(clear)))

    return frames, errors


def decode_payload(
    descrambled_bits: list[int],
    transmission_length_bits: int,
) -> dict[str, Any]:
    data_octets = (transmission_length_bits + 7) // 8
    full_blocks = data_octets // DATA_PER_BLOCK
    partial_length = data_octets % DATA_PER_BLOCK
    block_count = full_blocks + (1 if partial_length else 0)
    last_row_length = partial_length or DATA_PER_BLOCK
    fec_octets = full_blocks * 6 + (
        get_fec_octet_count(partial_length) if partial_length else 0
    )
    input_octets = data_octets + fec_octets
    required_bits = input_octets * 8
    available_bits = max(0, len(descrambled_bits) - HEADER_BITS)

    base: dict[str, Any] = {
        "attempted": True,
        "transmission_length_bits": transmission_length_bits,
        "data_octets": data_octets,
        "fec_octets": fec_octets,
        "required_raw_bits": required_bits,
        "available_raw_bits": available_bits,
        "reed_solomon_blocks": block_count,
    }

    if available_bits < required_bits:
        return base | {
            "complete": False,
            "reed_solomon_valid": False,
            "status": "payload_truncated",
            "frames": [],
        }

    payload_input = descrambled_bits[HEADER_BITS:HEADER_BITS + required_bits]
    data_values = bits_to_octets_lsb(payload_input[:data_octets * 8])
    fec_values = bits_to_octets_lsb(payload_input[data_octets * 8:])
    rows = [[0] * NN for _ in range(block_count)]
    deinterleave_into(data_values, block_count, rows, DATA_PER_BLOCK, 0)
    fec_rows = block_count - (1 if get_fec_octet_count(last_row_length) == 0 else 0)
    deinterleave_into(fec_values, fec_rows, rows, NROOTS, DATA_PER_BLOCK)
    corrected_payload: list[int] = []
    corrected_symbols = 0
    erasure_symbols = 0
    block_results = []

    for row_index, row in enumerate(rows):
        data_length = last_row_length if row_index == len(rows) - 1 else DATA_PER_BLOCK
        data_offset = 0
        transmitted_fec = get_fec_octet_count(data_length)
        erasures = list(range(DATA_PER_BLOCK + transmitted_fec, NN))
        erasure_symbols += len(erasures)
        result, locations = rs_decode(row, erasures)

        block_results.append({
            "block": row_index,
            "data_length": data_length,
            "transmitted_fec": transmitted_fec,
            "decoder_result": result,
            "corrected_locations": locations,
        })

        if result < 0:
            return base | {
                "complete": True,
                "reed_solomon_valid": False,
                "corrected_symbols": corrected_symbols,
                "erasure_symbols": erasure_symbols,
                "block_results": block_results,
                "status": "rs_uncorrectable",
                "frames": [],
            }

        corrected_symbols += sum(
            data_offset <= location < data_offset + data_length
            for location in locations
        )
        corrected_payload.extend(row[data_offset:data_offset + data_length])

    clear_bits = octets_to_bits_lsb(corrected_payload)[:transmission_length_bits]
    hdlc_frames, unstuff_errors = extract_hdlc_frames(clear_bits)
    valid_frames = []
    invalid_fcs = 0

    for index, frame in enumerate(hdlc_frames):
        residual = calculate_fcs(frame)
        if residual != GOOD_FCS:
            invalid_fcs += 1
            continue
        parsed = parse_avlc(frame, index)
        if parsed is not None:
            valid_frames.append(parsed)

    if valid_frames:
        status = "AVLC-VALID"
    elif not hdlc_frames:
        status = "hdlc_unstuff_failed" if unstuff_errors else "hdlc_no_frame"
    elif invalid_fcs:
        status = "avlc_fcs_failed"
    else:
        status = "avlc_parse_failed"

    return base | {
        "complete": True,
        "reed_solomon_valid": True,
        "corrected_symbols": corrected_symbols,
        "erasure_symbols": erasure_symbols,
        "block_results": block_results,
        "hdlc_frames": len(hdlc_frames),
        "hdlc_unstuff_errors": unstuff_errors,
        "fcs_valid_frames": len(valid_frames),
        "fcs_invalid_frames": invalid_fcs,
        "corrected_payload_hex": bytes(corrected_payload).hex().upper(),
        "status": status,
        "frames": valid_frames,
    }


def encode_address(address: int, address_type: int, status_bit: int) -> bytes:
    decoded = (
        (address & 0xFFFFFF)
        | ((address_type & 7) << 24)
        | ((status_bit & 1) << 27)
    )
    encoded = reverse_bits(decoded, 28)
    return bytes((
        (encoded & 0x7F) << 1,
        (encoded >> 6) & 0xFF,
        (encoded >> 13) & 0xFF,
        ((encoded >> 21) & 0x7F) << 1 | 1,
    ))


def stuff_bits(bits: list[int]) -> list[int]:
    output: list[int] = []
    ones = 0
    for bit in bits:
        output.append(bit)
        if bit:
            ones += 1
            if ones == 5:
                output.append(0)
                ones = 0
        else:
            ones = 0
    return output


def encode_payload_for_test(payload_bits: list[int]) -> list[int]:
    transmission_length_bits = len(payload_bits)
    data_octets = (transmission_length_bits + 7) // 8
    data = bits_to_octets_lsb(
        payload_bits + [0] * (data_octets * 8 - len(payload_bits))
    )
    full_blocks = data_octets // DATA_PER_BLOCK
    partial_length = data_octets % DATA_PER_BLOCK
    block_count = full_blocks + (1 if partial_length else 0)
    last_row_length = partial_length or DATA_PER_BLOCK
    rows: list[list[int]] = []
    cursor = 0
    fec_octets = 0

    for row_index in range(block_count):
        data_length = last_row_length if row_index == block_count - 1 else DATA_PER_BLOCK
        data_offset = 0
        row = [0] * NN
        row[:data_length] = data[cursor:cursor + data_length]
        cursor += data_length
        row[DATA_PER_BLOCK:] = rs_encode(row[:DATA_PER_BLOCK])
        fec_octets += get_fec_octet_count(data_length)
        rows.append(row)

    data_interleaved = interleave_from(
        rows,
        block_count,
        data_octets,
        DATA_PER_BLOCK,
        0,
    )
    fec_rows = block_count - (1 if get_fec_octet_count(last_row_length) == 0 else 0)
    fec_interleaved = interleave_from(
        rows,
        fec_rows,
        fec_octets,
        NROOTS,
        DATA_PER_BLOCK,
    )
    return octets_to_bits_lsb(data_interleaved + fec_interleaved)


def analyze_iq(iq_path: Path, sample_rate: float) -> dict[str, Any]:
    raw = np.fromfile(iq_path, dtype="<f4")
    if raw.size % 2:
        raise ValueError("Invalid IQ file: odd float count")

    iq = raw[::2] + 1j * raw[1::2]
    iq -= iq.mean()
    bounded = HEADER.bursts(iq, sample_rate)
    filtered = np.convolve(iq, HEADER.rrc(sample_rate), "same")
    analyses = []

    for burst_index, (start, end, snr_db) in enumerate(bounded):
        candidate = HEADER.findpre(filtered, start, end, sample_rate)
        item: dict[str, Any] = {
            "burst_index": burst_index,
            "start_sample": start,
            "end_sample": end,
            "estimated_snr_db": snr_db,
        }

        if candidate is None:
            item["status"] = "no_preamble"
            analyses.append(item)
            continue

        item.update({
            "preamble_rms_deg": math.degrees(candidate["rms"]),
            "preamble_correlation": candidate["corr"],
            "residual_frequency_offset_hz": (
                candidate["slope"] * HEADER.SYMBOL_RATE / (2 * math.pi)
            ),
        })

        if candidate["rms"] > 0.42 or candidate["corr"] < 0.91:
            item["status"] = "preamble_metric_rejected"
            analyses.append(item)
            continue

        raw_bits = HEADER.demod(candidate)
        clear_bits = HEADER.descr(raw_bits)
        header = HEADER.hdr(raw_bits)
        item["header"] = header

        if not header["valid"]:
            item["status"] = "header_invalid"
            analyses.append(item)
            continue

        payload = decode_payload(clear_bits, header["len"])
        item["payload"] = payload
        item["status"] = payload["status"]
        analyses.append(item)

    return {
        "schema_version": 1,
        "stage": "vdl2_payload_reed_solomon_avlc",
        "iq_file": iq_path.name,
        "sample_rate": sample_rate,
        "bounded_burst_count": len(bounded),
        "analyses": analyses,
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Decode VDL2 physical header, Reed-Solomon payload, HDLC and "
            "basic FCS-valid AVLC aircraft addresses."
        )
    )
    parser.add_argument("iq")
    parser.add_argument("--sample-rate", type=float, default=37_500.0)
    parser.add_argument("--output")
    args = parser.parse_args()

    iq_path = Path(args.iq).resolve()
    result = analyze_iq(iq_path, args.sample_rate)
    output = (
        Path(args.output).resolve()
        if args.output
        else iq_path.with_suffix(".vdl2-payload.json")
    )
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
    print(f"\nSaved: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
