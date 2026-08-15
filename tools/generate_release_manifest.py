#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations
import hashlib, json, sys
from pathlib import Path

def records(root: Path):
    output=[]
    for path in sorted(root.rglob('*')):
        if not path.is_file(): continue
        rel=path.relative_to(root).as_posix()
        if rel=='RELEASE_MANIFEST.json' or rel in {'lib/SDRSharp.Common.dll','lib/SDRSharp.Radio.dll'} or any(p in {'bin','obj','artifacts','__pycache__','.git'} for p in path.relative_to(root).parts): continue
        data=path.read_bytes(); output.append({'path':rel,'length':len(data),'sha256':hashlib.sha256(data).hexdigest()})
    return output

def main():
    root=Path(sys.argv[1] if len(sys.argv)>1 else '.').resolve()
    manifest={'schemaVersion':1,'name':'Aircraft Data Enhanced','version':'1.0.0','release':'v1.0.0 stable public source','dotnetSdk':'9.0.316','files':records(root)}
    (root/'RELEASE_MANIFEST.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print('[OK] RELEASE_MANIFEST.json generated.')
    return 0
if __name__=='__main__': raise SystemExit(main())
