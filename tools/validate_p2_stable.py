#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations
import hashlib, json, sys
from pathlib import Path

def main():
    root=Path(sys.argv[1] if len(sys.argv)>1 else '.').resolve(); errors=[]
    required=[
      'AircraftDataEnhanced.sln','global.json','Directory.Build.props','Directory.Packages.props',
      'src/AircraftDataEnhanced.Core/AircraftDataEnhanced.Core.csproj',
      'src/AircraftDataEnhanced.Persistence/AircraftDataEnhanced.Persistence.csproj',
      'src/AircraftDataEnhanced.SdrSharpAdapter/AircraftDataEnhanced.SdrSharpAdapter.csproj',
      'tests/AircraftDataEnhanced.Tests/AircraftDataEnhanced.Tests.csproj',
      'testdata/golden/vdl2_full_frame_iq_f32le.bin','testdata/golden/vdl2_full_frame_expected.json',
      'sdk/compatibility-matrix.json','sdk/approved-sdks.json','RELEASE_MANIFEST.json']
    for rel in required:
      if not (root/rel).exists(): errors.append('Missing: '+rel)
    if (root/'AircraftDataEnhanced.csproj').exists(): errors.append('Monolithic project still exists.')
    expected=json.loads((root/'testdata/golden/vdl2_full_frame_expected.json').read_text())
    actual=hashlib.sha256((root/'testdata/golden/vdl2_full_frame_iq_f32le.bin').read_bytes()).hexdigest()
    if expected.get('iqSha256')!=actual: errors.append('Golden IQ hash mismatch.')
    for project in root.glob('src/*/*.csproj'):
      text=project.read_text(encoding='utf-8-sig')
      if '1.0.0' in text: errors.append(f'Version should be centralized, not duplicated: {project}')
    adapter=(root/'src/AircraftDataEnhanced.SdrSharpAdapter/AircraftDataEnhanced.SdrSharpAdapter.csproj').read_text()
    for token in ['SDRSharp.Common.dll','SDRSharp.Radio.dll','win-x86']:
      if token not in adapter: errors.append('Adapter missing token: '+token)
    if errors:
      for e in errors: print('[ERRO]',e)
      return 1
    print('[OK] P2 stable architecture, golden vectors, SDK matrix and manifests validated.')
    return 0
if __name__=='__main__': raise SystemExit(main())
