"""SQLite manifest: which recordings were pulled from the recorder and which files were transcribed."""
from __future__ import annotations

import sqlite3
import threading
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path


def _now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


@dataclass
class TranscriptRecord:
    path: str
    size: int
    mtime: float
    status: str          # "done" | "failed"
    attempts: int
    transcript_path: str | None
    error: str | None


class Manifest:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()
        self._db = sqlite3.connect(str(self.path), check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        with self._lock:
            self._db.execute("PRAGMA journal_mode=WAL")
            self._db.executescript(
                """
                CREATE TABLE IF NOT EXISTS pulled (
                    file_id      TEXT PRIMARY KEY,
                    recorded_at  TEXT NOT NULL,
                    size         INTEGER NOT NULL,
                    path         TEXT NOT NULL,
                    pulled_at    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS transcripts (
                    path             TEXT PRIMARY KEY,
                    size             INTEGER NOT NULL,
                    mtime            REAL NOT NULL,
                    sha256           TEXT,
                    transcript_path  TEXT,
                    model            TEXT,
                    language         TEXT,
                    status           TEXT NOT NULL,
                    attempts         INTEGER NOT NULL DEFAULT 0,
                    error            TEXT,
                    updated_at       TEXT NOT NULL
                );
                """
            )
            self._db.commit()

    # ---- recordings pulled from the device -------------------------------------------------

    def is_pulled(self, file_id: str) -> bool:
        with self._lock:
            row = self._db.execute("SELECT 1 FROM pulled WHERE file_id = ?", (file_id,)).fetchone()
        return row is not None

    def mark_pulled(self, file_id: str, recorded_at: datetime, size: int, path: Path) -> None:
        with self._lock:
            self._db.execute(
                "INSERT OR REPLACE INTO pulled (file_id, recorded_at, size, path, pulled_at) VALUES (?,?,?,?,?)",
                (file_id, recorded_at.isoformat(timespec="seconds"), size, str(path), _now()),
            )
            self._db.commit()

    def pulled_count(self) -> int:
        with self._lock:
            return self._db.execute("SELECT COUNT(*) FROM pulled").fetchone()[0]

    # ---- transcriptions ---------------------------------------------------------------------

    def transcript_record(self, path: Path) -> TranscriptRecord | None:
        with self._lock:
            row = self._db.execute("SELECT * FROM transcripts WHERE path = ?", (str(path),)).fetchone()
        if row is None:
            return None
        return TranscriptRecord(
            path=row["path"], size=row["size"], mtime=row["mtime"], status=row["status"],
            attempts=row["attempts"], transcript_path=row["transcript_path"], error=row["error"],
        )

    def needs_transcription(self, path: Path, size: int, mtime: float, max_attempts: int) -> bool:
        """True unless the same file (path, size, mtime) is already done or has failed too often."""
        rec = self.transcript_record(path)
        if rec is None:
            return True
        same_file = rec.size == size and abs(rec.mtime - mtime) < 1.0
        if not same_file:
            return True  # file was replaced: transcribe again
        if rec.status == "done":
            return False
        return rec.attempts < max_attempts

    def mark_transcribed(self, path: Path, size: int, mtime: float, sha256: str,
                         transcript_path: Path, model: str, language: str | None) -> None:
        with self._lock:
            self._db.execute(
                """INSERT INTO transcripts (path, size, mtime, sha256, transcript_path, model, language,
                                            status, attempts, error, updated_at)
                   VALUES (?,?,?,?,?,?,?,'done',1,NULL,?)
                   ON CONFLICT(path) DO UPDATE SET size=excluded.size, mtime=excluded.mtime,
                        sha256=excluded.sha256, transcript_path=excluded.transcript_path,
                        model=excluded.model, language=excluded.language, status='done',
                        attempts=transcripts.attempts+1, error=NULL, updated_at=excluded.updated_at""",
                (str(path), size, mtime, sha256, str(transcript_path), model, language, _now()),
            )
            self._db.commit()

    def mark_failed(self, path: Path, size: int, mtime: float, error: str) -> int:
        """Record a failed attempt; returns the attempt count so far."""
        with self._lock:
            self._db.execute(
                """INSERT INTO transcripts (path, size, mtime, status, attempts, error, updated_at)
                   VALUES (?,?,?,'failed',1,?,?)
                   ON CONFLICT(path) DO UPDATE SET
                        attempts = CASE WHEN transcripts.size=excluded.size AND abs(transcripts.mtime-excluded.mtime)<1.0
                                        THEN transcripts.attempts+1 ELSE 1 END,
                        size=excluded.size, mtime=excluded.mtime, status='failed',
                        error=excluded.error, updated_at=excluded.updated_at""",
                (str(path), size, mtime, error[:2000], _now()),
            )
            self._db.commit()
            return self._db.execute("SELECT attempts FROM transcripts WHERE path=?", (str(path),)).fetchone()[0]

    def transcript_counts(self) -> dict[str, int]:
        with self._lock:
            rows = self._db.execute("SELECT status, COUNT(*) AS n FROM transcripts GROUP BY status").fetchall()
        return {row["status"]: row["n"] for row in rows}

    def recent(self, limit: int = 10) -> list[sqlite3.Row]:
        with self._lock:
            return self._db.execute(
                "SELECT path, status, transcript_path, language, updated_at, error FROM transcripts "
                "ORDER BY updated_at DESC LIMIT ?", (limit,)
            ).fetchall()

    def close(self) -> None:
        with self._lock:
            self._db.close()
