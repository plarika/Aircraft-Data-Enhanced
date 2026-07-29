#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

def crc16_ccitt_reflected(data: bytes, init: int = 0) -> int:
    crc = init
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = ((crc >> 1) ^ 0x8408) if crc & 1 else crc >> 1
    return crc & 0xFFFF

def append_crc_for_zero_residual(data: bytes) -> bytes:
    for candidate in range(0x10000):
        suffix = bytes((candidate & 0xFF, candidate >> 8))
        if crc16_ccitt_reflected(data + suffix) == 0:
            return data + suffix
    raise AssertionError("Could not construct ACARS CRC")

def build_synthetic_downlink() -> bytes:
    logical=(b"2"+b".TST-A1"+b"B"+b"_"+bytes((0x7F,))+b"1"+bytes((0x02,))+b"T01A"+b"DEMO01"+b"SYNTHETIC TEST MESSAGE"+bytes((0x03,)))
    return append_crc_for_zero_residual(logical)+bytes((0x7F,))

def parse(raw: bytes) -> dict[str, object]:
    if len(raw)<16: raise ValueError("too short")
    if raw[-1]!=0x7F: raise ValueError("missing DEL")
    crc_valid=crc16_ccitt_reflected(raw[:-1])==0
    frame=bytes(v&0x7F for v in raw)
    logical_length=len(frame)-3
    marker=frame[logical_length-1]
    if marker not in (0x03,0x17): raise ValueError("missing ETX/ETB")
    final_block=marker==0x03; logical_length-=1; offset=0
    mode=chr(frame[offset]); offset+=1
    raw_registration=frame[offset:offset+7].decode('ascii'); offset+=7
    ack=chr(frame[offset]); offset+=1
    first=chr(frame[offset]); second='d' if frame[offset+1]==0x7F else chr(frame[offset+1]); label=first+second; offset+=2
    block_id=chr(frame[offset]); offset+=1
    if frame[offset]!=0x02: raise ValueError("missing STX")
    offset+=1; message_number=''; sequence=''; flight_id=''
    if '0'<=block_id<='9':
        message_number=frame[offset:offset+3].decode('ascii'); sequence=chr(frame[offset+3]); offset+=4
        flight_id=frame[offset:offset+6].decode('ascii').strip(); offset+=6
    text=frame[offset:logical_length].decode('ascii',errors='replace').strip()
    return {'crc_valid':crc_valid,'final_block':final_block,'mode':mode,'registration':raw_registration.strip().lstrip('.'),'raw_registration':raw_registration,'ack':ack,'label':label,'block_id':block_id,'message_number':message_number,'sequence':sequence,'flight_id':flight_id,'text':text}

def main()->int:
    raw=build_synthetic_downlink(); decoded=parse(raw)
    expected={'crc_valid':True,'final_block':True,'mode':'2','registration':'TST-A1','raw_registration':'.TST-A1','ack':'B','label':'_d','block_id':'1','message_number':'T01','sequence':'A','flight_id':'DEMO01','text':'SYNTHETIC TEST MESSAGE'}
    if decoded!=expected: raise AssertionError(f"{decoded}\n!=\n{expected}")
    corrupted=bytearray(raw); corrupted[4]^=1
    if parse(bytes(corrupted))['crc_valid'] is not False: raise AssertionError('CRC corruption not detected')
    print('[OK] Synthetic ACARS envelope regression passed.')
    return 0
if __name__=='__main__': raise SystemExit(main())
