#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations
import hashlib,json,sys
from pathlib import Path

def main():
    root=Path(sys.argv[1] if len(sys.argv)>1 else '.').resolve(); path=root/'RELEASE_MANIFEST.json'
    manifest=json.loads(path.read_text(encoding='utf-8')); errors=[]
    expected={x['path']:x for x in manifest['files']}
    for rel,item in expected.items():
        p=root/rel
        if not p.is_file(): errors.append('Missing: '+rel); continue
        data=p.read_bytes()
        if len(data)!=item['length'] or hashlib.sha256(data).hexdigest()!=item['sha256']: errors.append('Hash mismatch: '+rel)
    actual=[]
    for p in root.rglob('*'):
        if not p.is_file(): continue
        rel=p.relative_to(root).as_posix()
        if rel=='RELEASE_MANIFEST.json' or rel in {'lib/SDRSharp.Common.dll','lib/SDRSharp.Radio.dll'} or any(x in {'bin','obj','artifacts','__pycache__','.git'} for x in p.relative_to(root).parts): continue
        actual.append(rel)
    extra=sorted(set(actual)-set(expected))
    errors.extend('Unmanifested: '+x for x in extra)
    if errors:
        for e in errors: print('[ERRO]',e)
        return 1
    print(f'[OK] Release manifest verified: {len(expected)} files.')
    return 0
if __name__=='__main__': raise SystemExit(main())
