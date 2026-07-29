#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations
import importlib.util
from pathlib import Path

def load(path):
 spec=importlib.util.spec_from_file_location('decoder',path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m

def bits(value,count):return [(value>>(count-i-1))&1 for i in range(count)]
def valid_header(m,length):
 encoded=m.rev(length,17);base=encoded<<5;found=[base|f for f in range(32) if m.syndrome(base|f)==0]
 assert len(found)==1;return found[0]

def main():
 m=load(Path(__file__).with_name('decode_vdl2_header.py'))
 for length in (1,515,1024,4095,0x1fff,0x3fff):
  clear=bits(valid_header(m,length),25);scrambled=m.descr(clear);decoded=m.hdr(scrambled)
  assert decoded['valid'] and decoded['len']==length and decoded['sa']==0
  if length <= 0x1fff:
   for pos in range(25):
    damaged=scrambled.copy();damaged[pos]^=1;fixed=m.hdr(damaged);assert fixed['valid'],(length,pos,fixed)
 print('[OK] VDL2 header regression tests passed: descrambler, FEC, all single-bit positions and 17-bit length reversal.')
if __name__=='__main__':main()
