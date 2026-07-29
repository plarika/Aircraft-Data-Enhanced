#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone


@dataclass(frozen=True)
class Message:
    received: datetime
    icao: str
    registration: str = ""
    flight: str = ""
    label: str = ""
    text: str = ""


@dataclass
class Session:
    icao: str
    first_seen: datetime
    last_seen: datetime
    latest: Message
    count: int = 0
    registration: str = ""
    flight: str = ""
    history: list[Message] = field(default_factory=list)


def normalize_icao(value: str) -> str | None:
    normalized = value.strip().upper()

    if len(normalized) != 6:
        return None

    if any(character not in "0123456789ABCDEF"
           for character in normalized):
        return None

    return normalized


def add(sessions: dict[str, Session], message: Message) -> bool:
    icao = normalize_icao(message.icao)

    if icao is None:
        return False

    session = sessions.get(icao)

    if session is None:
        session = Session(
            icao=icao,
            first_seen=message.received,
            last_seen=message.received,
            latest=message,
        )
        sessions[icao] = session

    session.first_seen = min(
        session.first_seen,
        message.received,
    )

    is_latest = message.received >= session.last_seen
    session.last_seen = max(
        session.last_seen,
        message.received,
    )
    session.count += 1

    if message.registration:
        session.registration = message.registration

    if message.flight:
        session.flight = message.flight

    if is_latest:
        session.latest = message

    session.history.insert(0, message)
    del session.history[200:]

    return True


def active(
    sessions: dict[str, Session],
    now: datetime,
    window: timedelta,
) -> list[Session]:
    cutoff = now - window

    return sorted(
        (
            session
            for session in sessions.values()
            if session.last_seen >= cutoff
        ),
        key=lambda session: session.last_seen,
        reverse=True,
    )


def main() -> int:
    base = datetime(
        2026,
        7,
        28,
        20,
        0,
        tzinfo=timezone.utc,
    )

    sessions: dict[str, Session] = {}

    assert not add(
        sessions,
        Message(base, "NOTHEX"),
    )

    assert add(
        sessions,
        Message(
            base,
            "a1b2c3",
            flight="DEMO01",
            label="H1",
        ),
    )

    assert add(
        sessions,
        Message(
            base + timedelta(minutes=2),
            "A1B2C3",
            registration="TST-A1",
            flight="DEMO01",
            label="_d",
        ),
    )

    assert add(
        sessions,
        Message(
            base - timedelta(minutes=1),
            "A1B2C3",
            text="out-of-order history",
        ),
    )

    assert add(
        sessions,
        Message(
            base - timedelta(minutes=30),
            "D4E5F6",
            registration="CS-OLD",
        ),
    )

    current = sessions["A1B2C3"]

    assert current.count == 3
    assert current.first_seen == base - timedelta(minutes=1)
    assert current.last_seen == base + timedelta(minutes=2)
    assert current.latest.registration == "TST-A1"
    assert current.registration == "TST-A1"
    assert current.flight == "DEMO01"
    assert len(current.history) == 3

    active_15 = active(
        sessions,
        base + timedelta(minutes=5),
        timedelta(minutes=15),
    )

    assert [session.icao for session in active_15] == [
        "A1B2C3"
    ]

    retained = sorted(sessions)

    assert retained == [
        "A1B2C3",
        "D4E5F6",
    ]

    print(
        "[OK] Active aircraft session regression passed: "
        "ICAO normalization, grouping, first/last seen, "
        "out-of-order handling, identity enrichment, message count "
        "and active-window expiry."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
