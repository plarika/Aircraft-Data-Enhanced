#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys


@dataclass
class LookupModel:
    icao: str = ""
    callsign: str = ""
    dedup: str = ""
    aircraft_starts: int = 0
    route_starts: int = 0

    def select(self, icao: str, callsign: str, dedup: str) -> None:
        icao = icao.strip().upper()
        callsign = callsign.strip().upper()

        if dedup == self.dedup:
            return

        same_icao = icao == self.icao
        same_callsign = callsign == self.callsign
        self.icao = icao
        self.callsign = callsign
        self.dedup = dedup

        if not same_icao:
            self.aircraft_starts += 1

        if callsign and not same_callsign:
            self.route_starts += 1


def require(text: str, token: str, errors: list[str], label: str) -> None:
    if token not in text:
        errors.append(f"missing {label}: {token}")


def reject(text: str, token: str, errors: list[str], label: str) -> None:
    if token in text:
        errors.append(f"forbidden {label}: {token}")


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    panel = (
        root
        / "src"
        / "AircraftDataEnhanced.SdrSharpAdapter"
        / "AircraftDashboardControl.cs"
    ).read_text(encoding="utf-8-sig")
    service = (
        root
        / "src"
        / "AircraftDataEnhanced.Core"
        / "AircraftMetadataService.cs"
    ).read_text(encoding="utf-8-sig")

    errors: list[str] = []

    require(panel, "_aircraftLookupCancellation", errors, "aircraft CTS")
    require(panel, "_routeLookupCancellation", errors, "route CTS")
    require(panel, "sameIcao", errors, "ICAO reuse decision")
    require(panel, "sameCallsign", errors, "callsign reuse decision")
    require(
        panel,
        "string.Equals(\n                _message?.DedupKey,",
        errors,
        "same-message guard",
    )
    require(
        panel,
        "string.Equals(\n                    _selectedIcao,\n                    icao,",
        errors,
        "aircraft stale-result guard",
    )
    require(
        panel,
        "string.Equals(\n                    _selectedCallsign,\n                    callsign,",
        errors,
        "route stale-result guard",
    )
    require(panel, "forceRefresh: true", errors, "manual forced refresh")
    require(panel, "showing the last valid identity", errors, "last-good fallback")
    require(panel, "_selectionClearTimer", errors, "delayed transient-selection clear")
    require(panel, "_lookupWatchdog", errors, "UI lookup watchdog")
    require(panel, "LookupWatchdogSeconds = 12", errors, "bounded connecting state")
    require(panel, "ScheduleDashboardReset", errors, "transient clear scheduler")
    require(panel, "aircraft-lookup.log", errors, "lookup diagnostics log")
    reject(panel, "private CancellationTokenSource?\n        _lookupCancellation", errors, "shared CTS")
    reject(panel, "StartOnlineLookup()", errors, "legacy lookup entry point")

    require(service, "bool forceRefresh = false", errors, "cache bypass argument")
    require(service, '"1.0.0"', errors, "P2 user agent")
    require(service, '"429"', errors, "429 backoff")
    require(service, "TimeSpan.FromMinutes(2)", errors, "failure backoff")
    require(service, "_aircraftNetworkGate", errors, "separate aircraft network gate")
    require(service, "_routeNetworkGate", errors, "separate route network gate")
    require(service, "WholeLookupTimeout", errors, "whole-operation timeout")
    require(service, "CreateLinkedTokenSource", errors, "linked timeout token")
    require(service, "api.adsbdb.com/v0/aircraft/", errors, "ADSBdb fallback")
    require(service, "Timeout.InfiniteTimeSpan", errors, "provider-owned timeout policy")
    require(service, "ProviderLookupTimeout", errors, "provider timeout")
    require(service, '"adsbdb_timeout"', errors, "ADSBdb timeout isolation")
    require(service, '"hexdb_timeout"', errors, "HexDB timeout isolation")
    require(service, "api.adsbdb.com/v0/callsign/", errors, "ADSBdb route fallback")
    require(service, "LookupRouteAdsbDbAsync", errors, "ADSBdb route provider")
    require(service, "LookupRouteHexDbAsync", errors, "HexDB route provider")
    require(service, "TimeSpan.FromMinutes(2)", errors, "network failure backoff")

    lookup_section = service.split(
        "public async Task<AircraftMetadata> LookupAircraftAsync",
        1,
    )[1].split(
        "public async Task<FlightRouteMetadata> LookupRouteAsync",
        1,
    )[0]
    if lookup_section.find("LookupAircraftAdsbDbAsync") > lookup_section.find(
        "LookupAircraftHexDbAsync"
    ):
        errors.append("aircraft lookup: ADSBdb is not attempted before HexDB")

    route_section = service.split(
        "public async Task<FlightRouteMetadata> LookupRouteAsync",
        1,
    )[1].split(
        "public void Dispose()",
        1,
    )[0]
    if route_section.find("LookupRouteAdsbDbAsync") > route_section.find(
        "LookupRouteHexDbAsync"
    ):
        errors.append("route lookup: ADSBdb is not attempted before HexDB")
    reject(service, "private readonly SemaphoreSlim _networkGate", errors, "legacy shared gate")

    model = LookupModel()
    model.select("4081BD", "", "m1")
    if (model.aircraft_starts, model.route_starts) != (1, 0):
        errors.append("model: first aircraft selection did not start exactly one identity lookup")

    model.select("4081BD", "", "m2")
    if (model.aircraft_starts, model.route_starts) != (1, 0):
        errors.append("model: another frame from the same ICAO restarted identity lookup")

    model.select("4081BD", "BAW123", "m3")
    if (model.aircraft_starts, model.route_starts) != (1, 1):
        errors.append("model: callsign change did not start only the route lookup")

    model.select("4081BD", "BAW123", "m3")
    if (model.aircraft_starts, model.route_starts) != (1, 1):
        errors.append("model: repeated DedupKey restarted a lookup")

    model.select("4CA7B1", "BAW123", "m4")
    if (model.aircraft_starts, model.route_starts) != (2, 1):
        errors.append("model: ICAO change did not start exactly one new identity lookup")

    if errors:
        for error in errors:
            print("[ERRO]", error)
        return 1

    print("[OK] Aircraft dashboard lookup lifecycle regression passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
