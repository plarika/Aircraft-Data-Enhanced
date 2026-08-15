#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import argparse
from pathlib import Path


def require(text: str, token: str, label: str, errors: list[str]) -> None:
    if token not in text:
        errors.append(f'{label}: missing {token!r}')


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('root', nargs='?', default='.')
    args = parser.parse_args()
    root = Path(args.root).resolve()

    adapter = root / 'src' / 'AircraftDataEnhanced.SdrSharpAdapter'
    panel = (adapter / 'AircraftDataPanel.cs').read_text(encoding='utf-8-sig')
    theme = (adapter / 'AdeVisualTheme.cs').read_text(encoding='utf-8-sig')
    aircraft = (adapter / 'AircraftDashboardControl.cs').read_text(encoding='utf-8-sig')
    diagnostics = (adapter / 'DiagnosticsSummaryControl.cs').read_text(encoding='utf-8-sig')

    errors: list[str] = []

    for key in (
        '"overview"',
        '"aircraft"',
        '"messages"',
        '"waterfall"',
        '"history"',
        '"diagnostics"',
        '"settings"',
        '"about"',
    ):
        require(panel, key, 'navigation', errors)

    require(panel, 'new Size(1, 1)', 'hidden workspace tabs', errors)
    require(panel, 'menuStrip.Visible = false;', 'embedded shell', errors)
    require(panel, 'DiagnosticsSummaryControl', 'diagnostics integration', errors)
    require(panel, 'Environment.WorkingSet', 'memory diagnostics', errors)

    require(theme, 'NavigationRailButton', 'navigation visuals', errors)
    require(theme, 'AdeCardPanel', 'card visuals', errors)
    require(theme, 'GlyphForTitle', 'metric card glyphs', errors)
    require(theme, 'Width = 200;', 'reference-aligned navigation rail', errors)

    require(aircraft, 'Text = "Aircraft Details"', 'aircraft page title', errors)
    require(aircraft, '← Back to aircraft list', 'aircraft back navigation', errors)
    require(aircraft, 'Online identity loaded from', 'aircraft online state', errors)
    require(aircraft, 'new TableLayoutPanelCellPosition(0, 1)', 'responsive aircraft actions', errors)
    require(aircraft, 'ADSBdb', 'aircraft provider', errors)

    require(diagnostics, 'IQ QUEUE USAGE', 'diagnostics queue', errors)
    require(diagnostics, 'PERSISTENCE', 'diagnostics persistence', errors)
    require(diagnostics, 'WATERFALL', 'diagnostics waterfall', errors)

    for screenshot in (
        'overview-dark.png',
        'aircraft-details.png',
        'messages-waterfall.png',
    ):
        path = root / 'docs' / 'screenshots' / screenshot
        if not path.is_file() or path.stat().st_size == 0:
            errors.append(f'screenshot missing or empty: {path.relative_to(root)}')

    if errors:
        for error in errors:
            print('[ERRO]', error)
        return 1

    print('[OK] Native final interface regression passed: navigation, cards, aircraft page, diagnostics and neutral previews.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
