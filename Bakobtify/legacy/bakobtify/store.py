"""Persistência local em SQLite (somente biblioteca padrão)."""
from __future__ import annotations

import json
import sqlite3
import threading
import time
from typing import Any

from . import config

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    expires_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS favorites (
    track_id   TEXT PRIMARY KEY,
    payload    TEXT NOT NULL,
    created_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS playlists (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT NOT NULL UNIQUE,
    created_at REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS playlist_tracks (
    playlist_id INTEGER NOT NULL,
    track_id    TEXT NOT NULL,
    position    INTEGER NOT NULL,
    payload     TEXT NOT NULL,
    added_at    REAL NOT NULL,
    PRIMARY KEY (playlist_id, track_id),
    FOREIGN KEY (playlist_id) REFERENCES playlists(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS history (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    track_id  TEXT NOT NULL,
    payload   TEXT NOT NULL,
    played_at REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_history_played ON history(played_at DESC);
"""


class Store:
    def __init__(self, path: str | None = None) -> None:
        config.DATA_DIR.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()
        self._conn = sqlite3.connect(str(path or config.DB_PATH), check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        with self._lock:
            self._conn.execute("PRAGMA journal_mode=WAL")
            self._conn.execute("PRAGMA foreign_keys=ON")
            self._conn.executescript(SCHEMA)
            self._conn.commit()

    # -- meta ----------------------------------------------------------

    def get_meta(self, key: str) -> dict[str, Any] | None:
        with self._lock:
            row = self._conn.execute(
                "SELECT value, expires_at FROM meta WHERE key = ?", (key,)
            ).fetchone()
        if not row:
            return None
        return {"value": row["value"], "expires_at": row["expires_at"]}

    def set_meta(self, key: str, value: str, expires_at: float) -> None:
        with self._lock:
            self._conn.execute(
                "INSERT INTO meta(key, value, expires_at) VALUES(?, ?, ?) "
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, "
                "expires_at = excluded.expires_at",
                (key, value, expires_at),
            )
            self._conn.commit()

    def delete_meta(self, key: str) -> None:
        with self._lock:
            self._conn.execute("DELETE FROM meta WHERE key = ?", (key,))
            self._conn.commit()

    # -- favoritos -----------------------------------------------------

    def list_favorites(self) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT payload FROM favorites ORDER BY created_at DESC"
            ).fetchall()
        return [json.loads(r["payload"]) for r in rows]

    def add_favorite(self, track: dict[str, Any]) -> None:
        with self._lock:
            self._conn.execute(
                "INSERT INTO favorites(track_id, payload, created_at) VALUES(?, ?, ?) "
                "ON CONFLICT(track_id) DO UPDATE SET payload = excluded.payload",
                (str(track["id"]), json.dumps(track, ensure_ascii=False), time.time()),
            )
            self._conn.commit()

    def remove_favorite(self, track_id: str) -> None:
        with self._lock:
            self._conn.execute("DELETE FROM favorites WHERE track_id = ?", (str(track_id),))
            self._conn.commit()

    # -- playlists -----------------------------------------------------

    def list_playlists(self) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT p.id, p.name, p.created_at, "
                "  (SELECT COUNT(*) FROM playlist_tracks t WHERE t.playlist_id = p.id) AS track_count "
                "FROM playlists p ORDER BY p.created_at ASC"
            ).fetchall()
        return [
            {
                "id": r["id"],
                "name": r["name"],
                "track_count": r["track_count"],
                "created_at": r["created_at"],
            }
            for r in rows
        ]

    def create_playlist(self, name: str) -> dict[str, Any]:
        name = name.strip()
        if not name:
            raise ValueError("O nome da playlist não pode ser vazio.")
        with self._lock:
            cur = self._conn.execute(
                "INSERT INTO playlists(name, created_at) VALUES(?, ?)",
                (name, time.time()),
            )
            self._conn.commit()
            return {"id": cur.lastrowid, "name": name, "track_count": 0}

    def delete_playlist(self, playlist_id: int) -> bool:
        with self._lock:
            cur = self._conn.execute("DELETE FROM playlists WHERE id = ?", (int(playlist_id),))
            self._conn.commit()
            return cur.rowcount > 0

    def playlist_tracks(self, playlist_id: int) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT payload FROM playlist_tracks WHERE playlist_id = ? "
                "ORDER BY position ASC",
                (int(playlist_id),),
            ).fetchall()
        return [json.loads(r["payload"]) for r in rows]

    def add_to_playlist(self, playlist_id: int, track: dict[str, Any]) -> None:
        with self._lock:
            row = self._conn.execute(
                "SELECT COALESCE(MAX(position), -1) AS m FROM playlist_tracks "
                "WHERE playlist_id = ?",
                (int(playlist_id),),
            ).fetchone()
            position = (row["m"] or -1) + 1
            self._conn.execute(
                "INSERT INTO playlist_tracks(playlist_id, track_id, position, payload, added_at) "
                "VALUES(?, ?, ?, ?, ?) "
                "ON CONFLICT(playlist_id, track_id) DO UPDATE SET payload = excluded.payload",
                (
                    int(playlist_id),
                    str(track["id"]),
                    position,
                    json.dumps(track, ensure_ascii=False),
                    time.time(),
                ),
            )
            self._conn.commit()

    def remove_from_playlist(self, playlist_id: int, track_id: str) -> None:
        with self._lock:
            self._conn.execute(
                "DELETE FROM playlist_tracks WHERE playlist_id = ? AND track_id = ?",
                (int(playlist_id), str(track_id)),
            )
            self._conn.commit()

    def playlist_exists(self, playlist_id: int) -> bool:
        with self._lock:
            row = self._conn.execute(
                "SELECT 1 FROM playlists WHERE id = ?", (int(playlist_id),)
            ).fetchone()
        return row is not None

    # -- histórico -----------------------------------------------------

    def list_history(self, limit: int = 100) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT payload, played_at FROM history ORDER BY played_at DESC LIMIT ?",
                (int(limit),),
            ).fetchall()
        out = []
        for r in rows:
            item = json.loads(r["payload"])
            item["played_at"] = r["played_at"]
            out.append(item)
        return out

    def add_history(self, track: dict[str, Any]) -> None:
        with self._lock:
            self._conn.execute(
                "INSERT INTO history(track_id, payload, played_at) VALUES(?, ?, ?)",
                (str(track["id"]), json.dumps(track, ensure_ascii=False), time.time()),
            )
            # mantém o arquivo enxuto
            self._conn.execute(
                "DELETE FROM history WHERE id NOT IN "
                "(SELECT id FROM history ORDER BY played_at DESC LIMIT 500)"
            )
            self._conn.commit()

    def clear_history(self) -> None:
        with self._lock:
            self._conn.execute("DELETE FROM history")
            self._conn.commit()

    def close(self) -> None:
        with self._lock:
            self._conn.close()
