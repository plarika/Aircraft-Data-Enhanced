#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations
import argparse,re
from pathlib import Path
BAD_SUFFIX={'.dll','.exe','.pdb','.sqlite','.sqlite3','.db','.iqf32','.cf32','.wav','.mp3','.mp4','.mkv','.avi','.jpg','.jpeg','.png','.gif','.log'}
BAD_DIR={'bin','obj','.vs','captures','analysis','diagnostics','__pycache__','.venv','venv'}
PATTERNS={'absolute Windows user path':re.compile(r'(?i)\b[A-Z]:\\Users\\[^\\\r\n]+'),'absolute Unix home path':re.compile(r'(?i)(?:^|[\s\"\'])/(?:home|Users)/[^/\s\"\']+'),'email address':re.compile(r'(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'),'probable credential':re.compile(r'(?i)\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*[\"\'][^\"\']{8,}[\"\']'),'private key':re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')}
def main()->int:
 p=argparse.ArgumentParser(); p.add_argument('root',nargs='?',default='.'); a=p.parse_args(); root=Path(a.root).resolve(); errors=[]; scanned=0
 for f in root.rglob('*'):
  rel=f.relative_to(root)
  if any(part in BAD_DIR for part in rel.parts): errors.append(f'Forbidden directory content: {rel}'); continue
  if not f.is_file(): continue
  if f.suffix.lower() in BAD_SUFFIX: errors.append(f'Forbidden file type: {rel}'); continue
  try: text=f.read_text(encoding='utf-8-sig')
  except UnicodeDecodeError: continue
  scanned+=1
  for label,rx in PATTERNS.items():
   if rx.search(text): errors.append(f'{rel}: {label}')
 for req in ['LICENSE','LICENSES/MIT.txt','LICENSES/LGPL-2.1-or-later.txt','LICENSES/README.md','THIRD_PARTY_NOTICES.md','LEGAL.md','PRIVACY.md','SECURITY.md','CONTRIBUTING.md','lib/README.md','lib/.gitignore']:
  if not (root/req).exists(): errors.append(f'Missing: {req}')
 rs=root/'src'/'ReedSolomon255249.cs'
 if rs.exists() and 'SPDX-License-Identifier: LGPL-2.1-or-later' not in rs.read_text(encoding='utf-8-sig'): errors.append('ReedSolomon SPDX missing')
 b=root/'BUILD_E_INSTALAR_TUDO.bat'
 if b.exists():
  text=b.read_text(encoding='utf-8-sig')
  for tok in ('https://airspy.com/download/','SDR# SDK for Plugin Developers','lib\\SDRSharp.Common.dll','lib\\SDRSharp.Radio.dll'):
   if tok not in text: errors.append(f'Build SDK notice missing: {tok}')
 if errors:
  for e in sorted(set(errors)): print('[ERRO]',e)
  return 1
 print(f'[OK] Public repository audit passed: {scanned} text files scanned; no proprietary DLLs, local data, absolute user paths, email addresses or obvious secrets found.')
 return 0
if __name__=='__main__': raise SystemExit(main())
