#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
import re
from pathlib import Path

BAD_SUFFIX = {
    '.dll', '.exe', '.pdb', '.sqlite', '.sqlite3', '.db',
    '.iqf32', '.cf32', '.wav', '.mp3', '.mp4', '.mkv',
    '.avi', '.jpg', '.jpeg', '.png', '.gif', '.log'
}

BAD_DIR = {
    'bin', 'obj', '.vs', 'captures', 'analysis', 'diagnostics',
    '__pycache__', '.venv', 'venv'
}

PATTERNS = {
    'absolute Windows user path': re.compile(
        r'(?i)\b[A-Z]:\\Users\\[^\\\r\n]+'
    ),
    'absolute Unix home path': re.compile(
        r'(?i)(?:^|[\s\"\'])/(?:home|Users)/[^/\s\"\']+'
    ),
    'email address': re.compile(
        r'(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    ),
    'probable credential': re.compile(
        r'(?i)\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)'
        r'\s*[:=]\s*[\"\'][^\"\']{8,}[\"\']'
    ),
    'private key': re.compile(
        r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    )
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        'root',
        nargs='?',
        default='.'
    )

    args = parser.parse_args()
    root = Path(args.root).resolve()

    errors = []
    scanned = 0

    for file_path in root.rglob('*'):
        relative = file_path.relative_to(root)

        # Ignora metadados internos criados pelo Git e pelo GitHub Actions.
        if '.git' in relative.parts:
            continue

        if any(part in BAD_DIR for part in relative.parts):
            errors.append(
                f'Forbidden directory content: {relative}'
            )
            continue

        if not file_path.is_file():
            continue

        if file_path.suffix.lower() in BAD_SUFFIX:
            errors.append(
                f'Forbidden file type: {relative}'
            )
            continue

        try:
            text = file_path.read_text(
                encoding='utf-8-sig'
            )
        except UnicodeDecodeError:
            continue

        scanned += 1

        for label, pattern in PATTERNS.items():
            if pattern.search(text):
                errors.append(
                    f'{relative}: {label}'
                )

    required_files = [
        'LICENSE',
        'LICENSES/MIT.txt',
        'LICENSES/LGPL-2.1-or-later.txt',
        'LICENSES/README.md',
        'THIRD_PARTY_NOTICES.md',
        'LEGAL.md',
        'PRIVACY.md',
        'SECURITY.md',
        'CONTRIBUTING.md',
        'lib/README.md',
        'lib/.gitignore'
    ]

    for required in required_files:
        if not (root / required).exists():
            errors.append(
                f'Missing: {required}'
            )

    reed_solomon = (
        root /
        'src' /
        'ReedSolomon255249.cs'
    )

    if reed_solomon.exists():
        text = reed_solomon.read_text(
            encoding='utf-8-sig'
        )

        if (
            'SPDX-License-Identifier: LGPL-2.1-or-later'
            not in text
        ):
            errors.append(
                'ReedSolomon SPDX missing'
            )

    build_script = (
        root /
        'BUILD_E_INSTALAR_TUDO.bat'
    )

    if build_script.exists():
        text = build_script.read_text(
            encoding='utf-8-sig'
        )

        required_tokens = (
            'https://airspy.com/download/',
            'SDR# SDK for Plugin Developers',
            'lib\\SDRSharp.Common.dll',
            'lib\\SDRSharp.Radio.dll'
        )

        for token in required_tokens:
            if token not in text:
                errors.append(
                    f'Build SDK notice missing: {token}'
                )

    if errors:
        for error in sorted(set(errors)):
            print(
                '[ERRO]',
                error
            )

        return 1

    print(
        '[OK] Public repository audit passed: '
        f'{scanned} text files scanned; '
        '.git metadata ignored; '
        'no proprietary DLLs, local data, absolute user paths, '
        'email addresses or obvious secrets found.'
    )

    return 0


if __name__ == '__main__':
    raise SystemExit(
        main()
    )
