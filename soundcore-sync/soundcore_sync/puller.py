"""Pull loop: look for the recorder over Bluetooth and copy any new recording into the inbox."""
from __future__ import annotations

import asyncio
import logging
import os
import threading
from datetime import datetime
from pathlib import Path

from .ble import DownloadedRecording, RecorderClient, RecorderError, RecorderNotFound, Recording
from .config import Config
from .manifest import Manifest

log = logging.getLogger(__name__)


def recording_filename(rec: Recording, prefix: str) -> str:
    return f"{rec.recorded_at:%Y-%m-%d_%H-%M-%S}_{prefix}.ogg"


def write_atomic(path: Path, data: bytes) -> None:
    tmp = path.with_name(path.name + ".part")
    tmp.write_bytes(data)
    os.replace(tmp, path)


class Puller:
    def __init__(self, cfg: Config, manifest: Manifest):
        self.cfg = cfg
        self.manifest = manifest
        self.inbox = cfg.folders.inbox
        self.state_dir = cfg.folders.state_dir

    # ---- one pass -----------------------------------------------------------------------

    async def pull_once(self) -> int:
        """Scan, connect, download everything new. Returns the number of recordings saved."""
        rc = self.cfg.recorder
        device = await RecorderClient.scan(rc.scan_timeout_seconds)
        if device is None:
            raise RecorderNotFound()
        client = RecorderClient(self.state_dir)
        saved = 0
        async with client:
            await client.connect(device)
            recordings = await client.start_session()
            todo = [r for r in sorted(recordings, key=lambda r: r.timestamp) if not self._already_have(r)]
            if not todo:
                log.info("Nothing new on the recorder")
                return 0
            log.info("%d new recording(s) to pull", len(todo))
            for rec in todo:
                try:
                    dl = await client.download(rec)
                except RecorderError as exc:
                    log.error("Download of %s failed: %s", rec.file_id_hex, exc)
                    break  # the link is in an unknown state: reconnect on the next pass
                self._save(dl)
                saved += 1
        return saved

    def _already_have(self, rec: Recording) -> bool:
        if self.manifest.is_pulled(rec.file_id_hex):
            return True
        path = self.inbox / recording_filename(rec, self.cfg.recorder.filename_prefix)
        if path.is_file() and path.stat().st_size > 0:
            # Present on disk but unknown to the manifest (e.g. manifest reset): adopt it.
            self.manifest.mark_pulled(rec.file_id_hex, rec.recorded_at, rec.size, path)
            return True
        return False

    def _save(self, dl: DownloadedRecording) -> Path:
        rec = dl.recording
        self.inbox.mkdir(parents=True, exist_ok=True)
        path = self.inbox / recording_filename(rec, self.cfg.recorder.filename_prefix)
        write_atomic(path, dl.ogg)
        if self.cfg.recorder.keep_raw:
            raw_dir = self.state_dir / "raw"
            raw_dir.mkdir(parents=True, exist_ok=True)
            (raw_dir / f"raw_{rec.file_id_hex}.bin").write_bytes(dl.raw)
        self.manifest.mark_pulled(rec.file_id_hex, rec.recorded_at, rec.size, path)
        log.info("Saved %s", path)
        return path

    # ---- loop ---------------------------------------------------------------------------

    def run_forever(self, stop: threading.Event) -> None:
        asyncio.run(self._loop(stop))

    async def _loop(self, stop: threading.Event) -> None:
        rc = self.cfg.recorder
        log.info("Pull loop started: looking for the recorder every %.0f s", rc.scan_interval_seconds)
        failures = 0
        while not stop.is_set():
            try:
                saved = await self.pull_once()
            except RecorderNotFound:
                delay = rc.scan_interval_seconds
            except Exception as exc:
                failures += 1
                log.warning("Pull attempt failed (%s); retrying later", exc, exc_info=failures >= 3)
                delay = min(rc.scan_interval_seconds * (2 ** min(failures, 4)), 600)
            else:
                failures = 0
                delay = rc.scan_interval_seconds if saved else rc.idle_interval_seconds
            await _wait(delay, stop)
        log.info("Pull loop stopped")


async def _wait(seconds: float, stop: threading.Event) -> None:
    deadline = asyncio.get_event_loop().time() + seconds
    while not stop.is_set() and asyncio.get_event_loop().time() < deadline:
        await asyncio.sleep(0.5)


def _fmt(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%d %H:%M")
