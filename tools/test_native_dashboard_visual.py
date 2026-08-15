from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
adapter = ROOT / "src" / "AircraftDataEnhanced.SdrSharpAdapter"
theme = (adapter / "AdeVisualTheme.cs").read_text(encoding="utf-8-sig")
panel = (adapter / "AircraftDataPanel.cs").read_text(encoding="utf-8-sig")
aircraft = (adapter / "AircraftDashboardControl.cs").read_text(encoding="utf-8-sig")
csproj = (adapter / "AircraftDataEnhanced.SdrSharpAdapter.csproj").read_text(encoding="utf-8-sig")

required_theme = [
    "Color.FromArgb(11, 14, 20)",
    "Color.FromArgb(15, 19, 26)",
    "Color.FromArgb(21, 26, 35)",
    "Color.FromArgb(30, 38, 51)",
    "Color.FromArgb(59, 130, 246)",
]
for token in required_theme:
    assert token in theme, f"missing reference palette token: {token}"

for caption in ["Overview", "Aircraft", "Messages", "Waterfall", "History", "Diagnostics"]:
    assert f'"{caption}"' in panel, f"missing navigation caption: {caption}"

assert "BackRequested" in aircraft
assert "SessionActivated" in (adapter / "ActiveAircraftSessionsControl.cs").read_text(encoding="utf-8-sig")
assert "_activeSessions.SessionActivated" in panel
assert "← Back to aircraft list" in aircraft
assert "Online identity: connecting…" in aircraft
assert "Microsoft.Web.WebView2" not in csproj
assert "WebView2" not in panel
assert "WebView2" not in aircraft

print("[OK] Native dashboard visual reference regression passed.")
