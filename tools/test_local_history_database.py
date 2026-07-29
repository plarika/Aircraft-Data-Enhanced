#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
from __future__ import annotations

import sqlite3
import tempfile
from pathlib import Path

SCHEMA = """
PRAGMA foreign_keys=ON;
CREATE TABLE messages
(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    dedup_key TEXT NOT NULL UNIQUE,
    received_unix_ms INTEGER NOT NULL,
    protocol TEXT NOT NULL,
    direction TEXT NOT NULL,
    icao TEXT NOT NULL COLLATE NOCASE,
    registration TEXT NOT NULL,
    callsign TEXT NOT NULL,
    label TEXT NOT NULL,
    message_text TEXT NOT NULL,
    frequency_mhz REAL NULL
);
CREATE TABLE aircraft
(
    icao TEXT PRIMARY KEY COLLATE NOCASE,
    registration TEXT NOT NULL,
    callsign TEXT NOT NULL,
    first_seen_unix_ms INTEGER NOT NULL,
    last_seen_unix_ms INTEGER NOT NULL,
    message_count INTEGER NOT NULL,
    last_label TEXT NOT NULL,
    last_text TEXT NOT NULL,
    last_frequency_mhz REAL NULL,
    latest_message_row_id INTEGER NULL,
    FOREIGN KEY(latest_message_row_id) REFERENCES messages(id) ON DELETE SET NULL
);
"""


def insert(connection: sqlite3.Connection, *, key: str, timestamp: int,
           icao: str, registration: str, callsign: str, label: str,
           text: str, frequency: float) -> bool:
    row = connection.execute(
        """
        INSERT OR IGNORE INTO messages
        (dedup_key, received_unix_ms, protocol, direction, icao,
         registration, callsign, label, message_text, frequency_mhz)
        VALUES (?, ?, 'ACARS', 'Air -> Ground', ?, ?, ?, ?, ?, ?)
        RETURNING id
        """,
        (key, timestamp, icao, registration, callsign, label, text, frequency),
    ).fetchone()
    if row is None:
        return False

    message_id = int(row[0])
    connection.execute(
        """
        INSERT INTO aircraft
        (icao, registration, callsign, first_seen_unix_ms, last_seen_unix_ms,
         message_count, last_label, last_text, last_frequency_mhz,
         latest_message_row_id)
        VALUES (?, ?, ?, ?, ?, 1, ?, ?, ?, ?)
        ON CONFLICT(icao) DO UPDATE SET
            registration = CASE WHEN excluded.registration <> ''
                THEN excluded.registration ELSE aircraft.registration END,
            callsign = CASE WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                AND excluded.callsign <> '' THEN excluded.callsign ELSE aircraft.callsign END,
            first_seen_unix_ms = MIN(aircraft.first_seen_unix_ms,
                excluded.first_seen_unix_ms),
            last_seen_unix_ms = MAX(aircraft.last_seen_unix_ms,
                excluded.last_seen_unix_ms),
            message_count = aircraft.message_count + 1,
            last_label = CASE WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                THEN excluded.last_label ELSE aircraft.last_label END,
            last_text = CASE WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                THEN excluded.last_text ELSE aircraft.last_text END,
            last_frequency_mhz = CASE WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                THEN excluded.last_frequency_mhz ELSE aircraft.last_frequency_mhz END,
            latest_message_row_id = CASE WHEN excluded.last_seen_unix_ms >= aircraft.last_seen_unix_ms
                THEN excluded.latest_message_row_id ELSE aircraft.latest_message_row_id END
        """,
        (icao, registration, callsign, timestamp, timestamp, label, text,
         frequency, message_id),
    )
    return True


def main() -> int:
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "history.sqlite3"
        connection = sqlite3.connect(path)
        connection.executescript(SCHEMA)

        assert insert(connection, key="A", timestamp=1000, icao="A1B2C3",
                      registration="TST-A1", callsign="DEMO01", label="_d",
                      text="first", frequency=136.725)
        assert insert(connection, key="B", timestamp=2000, icao="A1B2C3",
                      registration="", callsign="DEMO01", label="H1",
                      text="latest", frequency=136.775)
        assert not insert(connection, key="B", timestamp=2000, icao="A1B2C3",
                          registration="", callsign="DEMO01", label="H1",
                          text="latest", frequency=136.775)
        assert insert(connection, key="C", timestamp=1500, icao="D4E5F6",
                      registration="TST-B2", callsign="DEMO02", label="Q0",
                      text="second aircraft", frequency=136.725)
        connection.commit()

        assert connection.execute("SELECT COUNT(*) FROM messages").fetchone()[0] == 3
        assert connection.execute("SELECT COUNT(*) FROM aircraft").fetchone()[0] == 2

        session = connection.execute(
            """
            SELECT registration, callsign, first_seen_unix_ms, last_seen_unix_ms,
                   message_count, last_label, last_text, last_frequency_mhz
            FROM aircraft WHERE icao = 'A1B2C3'
            """
        ).fetchone()
        assert session == ("TST-A1", "DEMO01", 1000, 2000, 2, "H1", "latest", 136.775)

        filtered = connection.execute(
            "SELECT icao FROM aircraft WHERE callsign LIKE ? ORDER BY last_seen_unix_ms DESC",
            ("%DEMO01%",),
        ).fetchall()
        assert filtered == [("A1B2C3",)]

        connection.execute("DELETE FROM aircraft")
        connection.execute("DELETE FROM messages")
        connection.commit()
        assert connection.execute("SELECT COUNT(*) FROM messages").fetchone()[0] == 0
        assert connection.execute("SELECT COUNT(*) FROM aircraft").fetchone()[0] == 0
        connection.close()

    print(
        "[OK] Embedded SQLite history regression passed: unique-message persistence, "
        "aircraft grouping, first/last seen, identity preservation, filtering and clearing."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
