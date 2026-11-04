"""BLE client for the soundcore Work (Anker D3200) recorder.

Adapted from andrewseago/d3200-ble-note-downloader (GPL-3.0-or-later), whose author verified
the handshake, download and decryption path against real hardware. Differences from the
original: logging instead of prints, no session secrets written to disk, any number of files
per connection, and a wider timestamp/size window when parsing the recording list.

Protocol (JieLi RCSP over GATT):
    auth -> time sync -> list recordings -> ECDH key exchange -> per file:
    FILE_DETAIL_REQ (1a07) -> 0761 detail (wrapped file key) -> 08b0 audio chunks -> 0a13 done.
"""
from __future__ import annotations

import asyncio
import base64
import json
import logging
import os
import struct
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Self

from cryptography.hazmat.primitives.asymmetric import ec

from . import sdk_crypto
from .ogg_opus import build_ogg_opus

log = logging.getLogger(__name__)

RCSP_SERVICE_UUID = "020cf5da-0000-1000-8000-00805f9b34fb"
WRITE_CHAR_UUID = "00007777-0000-1000-8000-00805f9b34fb"
NOTIFY_CHAR_UUID = "00008888-0000-1000-8000-00805f9b34fb"

CMD_PREFIX = b"\x08\xee"
# Static token sent by the official app; the device does not validate it.
AUTH_TOKEN = base64.b64decode("AZDxUbC0xETNAC9H16acXvtLeQ==")

# Frames from the recorder: 09 ff 00 00 <status> <cmdType> <cmdId> <u16 LE frame length> <payload> <checksum>.
# As in the upstream client, a frame type is recognised by bytes 6-7, i.e. the command id followed by the
# low byte of the frame length, which is fixed for each of these frame types (97, 176 and 19 bytes).
OP_FILE_DETAIL = b"\x07\x61"
OP_AUDIO_CHUNK = b"\x08\xb0"
OP_TRANSFER_DONE = b"\x0a\x13"
OP_METADATA = b"\x06\x12"
OP_TRANSFER_INIT = b"\x18\x82"

EARLIEST_TIMESTAMP = 1_600_000_000   # 2020-09; anything earlier is not a recording id
MAX_RECORDING_BYTES = 2_000_000_000  # the recorder has 8 GB of storage


class RecorderError(Exception):
    """A protocol or transfer failure."""


class RecorderNotFound(RecorderError):
    """The recorder was not advertising (asleep, in its case, or connected to a phone)."""


@dataclass(frozen=True)
class Recording:
    file_id: bytes   # 4 bytes, little-endian Unix timestamp of the recording start
    size: int

    @property
    def timestamp(self) -> int:
        return int.from_bytes(self.file_id, "little")

    @property
    def recorded_at(self) -> datetime:
        return datetime.fromtimestamp(self.timestamp)

    @property
    def file_id_hex(self) -> str:
        return self.file_id.hex()


@dataclass
class DownloadedRecording:
    recording: Recording
    header: sdk_crypto.D3200FileHeader
    ogg: bytes
    packet_count: int
    raw: bytes
    completed: bool


def xor_checksum(data: bytes) -> int:
    chk = 0
    for b in data:
        chk ^= b
    return chk & 0xFF


def build_command(payload: bytes) -> bytes:
    return CMD_PREFIX + b"\x00\x00" + payload + bytes([xor_checksum(payload)])


def parse_device_pubkey(resp: bytes):
    """Extract the device's P-256 public key and the trailing key material from the handshake reply."""
    for offset in (9, 8, 10, 6, 7):
        if offset < len(resp) and resp[offset] == 0x04 and offset + 65 <= len(resp):
            x_be = resp[offset + 1 : offset + 33]
            y_be = resp[offset + 33 : offset + 65]
            try:
                pub = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), b"\x04" + x_be + y_be)
            except ValueError:
                continue
            extra = resp[offset + 65 : -1] if len(resp) > offset + 65 + 1 else b""
            return pub, extra
    for offset in (10, 9, 8, 6):
        if offset + 64 <= len(resp):
            x_be = resp[offset : offset + 32]
            y_be = resp[offset + 32 : offset + 64]
            try:
                pub = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), b"\x04" + x_be + y_be)
            except ValueError:
                continue
            extra = resp[offset + 64 : -1] if len(resp) > offset + 64 + 1 else b""
            return pub, extra
    return None, b""


def parse_recording_list(resp: bytes, *, now: int | None = None) -> list[Recording]:
    """Parse one page of the recording list (8-byte entries: fileId u32 LE, size u32 LE).

    The layout is found heuristically, as in the upstream client: an entry is accepted when its
    id is a plausible timestamp and its size is plausible. Returns entries in device order.
    """
    now = now or int(time.time())
    latest = now + 366 * 86400
    out: list[Recording] = []
    i = 10
    while i + 8 <= len(resp) - 1:
        fid = resp[i : i + 4]
        ts = int.from_bytes(fid, "little")
        size = int.from_bytes(resp[i + 4 : i + 8], "little")
        if EARLIEST_TIMESTAMP < ts < latest and 0 < size < MAX_RECORDING_BYTES:
            rec = Recording(bytes(fid), size)
            if rec not in out:
                out.append(rec)
            i += 8
        elif fid == b"\x00\x00\x00\x00":
            break
        else:
            i += 1
    return out


class RecorderClient:
    """One Bluetooth session with the recorder. Use as an async context manager."""

    def __init__(self, state_dir: Path, *, response_timeout: float = 10.0):
        self.state_dir = Path(state_dir)
        self.response_timeout = response_timeout
        self.client = None
        self.device = None
        self.notifications: asyncio.Queue[bytes] = asyncio.Queue()
        self.session_key: bytes | None = None
        self._priv_key = None

    # ---- discovery / connection ---------------------------------------------------------

    @staticmethod
    async def scan(timeout: float = 8.0):
        """Return the recorder's BLEDevice, or None. Matches on the RCSP service UUID (the address rotates)."""
        from bleak import BleakScanner

        found = await BleakScanner.discover(timeout=timeout, return_adv=True)
        for dev, adv in found.values():
            uuids = [str(u).lower() for u in (adv.service_uuids if adv else [])]
            name = (dev.name or "").lower()
            if RCSP_SERVICE_UUID in uuids or "soundcore" in name:
                log.info("Recorder found: %s (%s) RSSI=%s", dev.name or "(unnamed)", dev.address,
                         adv.rssi if adv else "?")
                return dev
        return None

    async def connect(self, device) -> None:
        from bleak import BleakClient

        self.device = device
        self.client = BleakClient(device.address)
        await self.client.connect()
        log.info("Connected to recorder (MTU=%s)", self.client.mtu_size)
        await self.client.start_notify(NOTIFY_CHAR_UUID, self._on_notify)

    async def disconnect(self) -> None:
        if self.client is not None and self.client.is_connected:
            try:
                await self.client.disconnect()
            except Exception as exc:  # noqa: BLE001 - best effort on teardown
                log.debug("disconnect: %s", exc)
        self.client = None
        self.session_key = None

    async def __aenter__(self) -> RecorderClient:
        return self

    async def __aexit__(self, *exc) -> None:
        await self.disconnect()

    # ---- low level ----------------------------------------------------------------------

    def _on_notify(self, _sender, data: bytearray) -> None:
        self.notifications.put_nowait(bytes(data))

    async def _send(self, payload: bytes) -> None:
        await self.client.write_gatt_char(WRITE_CHAR_UUID, build_command(payload), response=False)

    async def _recv(self, timeout: float | None = None) -> bytes | None:
        try:
            return await asyncio.wait_for(self.notifications.get(), timeout or self.response_timeout)
        except TimeoutError:
            return None

    async def _drain(self, quiet: float = 0.5) -> int:
        """Discard stale notifications until the link has been quiet for ``quiet`` seconds."""
        n = 0
        while True:
            data = await self._recv(timeout=quiet)
            if data is None:
                return n
            n += 1

    # ---- session ------------------------------------------------------------------------

    async def start_session(self) -> list[Recording]:
        """Auth, time sync, list recordings, then the ECDH handshake. Returns the recordings."""
        await self._auth()
        await self._sync_time()
        recordings = await self.list_recordings()
        await self._handshake()
        return recordings

    async def _auth(self) -> None:
        payload = bytes([0x00, 0x0B, 0x03, 0x27, 0x00, 0x1C]) + AUTH_TOKEN + bytes([0x15])
        await self._send(payload)
        resp = await self._recv(5)
        log.debug("auth response: %s", resp.hex() if resp else None)

    async def _sync_time(self) -> None:
        payload = bytes([0x00, 0x01, 0xA6, 0x14, 0x00]) + struct.pack("<I", int(time.time()))
        payload += bytes([0x0A, 0x90, 0xFF, 0xE6, 0x6A, 0x08])
        await self._send(payload)
        resp = await self._recv(5)
        log.debug("time sync response: %s", resp.hex() if resp else None)

    async def list_recordings(self) -> list[Recording]:
        recordings: list[Recording] = []
        for page in range(10):
            await self._send(bytes([0x00, 0x1A, 0x0E, 0x0C, 0x00, page, 0x00, 0x2A]))
            resp = await self._recv(5)
            if not resp:
                break
            entries = parse_recording_list(resp)
            if not entries:
                break
            for rec in entries:
                if rec not in recordings:
                    recordings.append(rec)
        log.info("Recorder lists %d recording(s)", len(recordings))
        return recordings

    def _load_or_create_keypair(self):
        """The persistent P-256 keypair used for the key exchange (as the official app does)."""
        path = self.state_dir / "recorder_keypair.json"
        if path.is_file():
            data = json.loads(path.read_text())
            return ec.derive_private_key(int(data["private_key"], 16), ec.SECP256R1())
        self.state_dir.mkdir(parents=True, exist_ok=True)
        key = ec.generate_private_key(ec.SECP256R1())
        private_value = key.private_numbers().private_value
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"private_key": format(private_value, "064x")}))
        try:
            os.chmod(tmp, 0o600)
        except OSError:
            pass
        os.replace(tmp, path)
        log.info("Generated a new key pair for the recorder (%s)", path)
        return key

    async def _handshake(self) -> None:
        self._priv_key = self._load_or_create_keypair()
        pub = self._priv_key.public_key().public_numbers()
        pub_be = pub.x.to_bytes(32, "big") + pub.y.to_bytes(32, "big")
        await self._send(bytes([0x00, 0x2E, 0x01, 0x4B, 0x00, 0x04]) + pub_be)
        resp = await self._recv(10)
        if not resp:
            raise RecorderError("no response to the key exchange")
        device_pub, _extra = parse_device_pubkey(resp)
        if device_pub is None:
            raise RecorderError(f"could not read the recorder's public key ({len(resp)} bytes)")
        shared = self._priv_key.exchange(ec.ECDH(), device_pub)
        self.session_key = sdk_crypto.derive_session_key(shared)
        log.info("Key exchange complete")

    # ---- download -----------------------------------------------------------------------

    async def download(self, rec: Recording, *, idle_timeout: float = 10.0) -> DownloadedRecording:
        """Fetch and decrypt one recording. The session must have been started."""
        if self.session_key is None:
            raise RecorderError("start_session() must succeed before downloading")
        await self._drain()
        log.info("Downloading %s (%s, %d bytes listed)", rec.file_id_hex, rec.recorded_at, rec.size)
        await self._send(bytes([0x00, 0x1A, 0x07, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00]) + rec.file_id)

        header = None
        file_key = None
        chunks: list[bytes] = []
        completed = False
        started = time.monotonic()
        last_log = started
        while True:
            data = await self._recv(idle_timeout)
            if data is None:
                if chunks and not completed:
                    log.warning("Transfer of %s went quiet after %d chunks", rec.file_id_hex, len(chunks))
                break
            op = data[6:8] if len(data) >= 8 else b""
            if op == OP_FILE_DETAIL:
                header = sdk_crypto.parse_file_header(data)
                file_key = sdk_crypto.unwrap_file_key(self.session_key, header)
                log.debug("file head: id=%s size=%d", header.file_id_hex, header.file_size)
            elif op == OP_AUDIO_CHUNK:
                chunks.append(data[12:])
                now = time.monotonic()
                if now - last_log > 15:
                    log.info("  %s: %d chunks (%.0f s)", rec.file_id_hex, len(chunks), now - started)
                    last_log = now
            elif op == OP_TRANSFER_DONE:
                completed = True
                break
            elif op in (OP_METADATA, OP_TRANSFER_INIT):
                log.debug("frame %s: %s", op.hex(), data.hex())
            elif len(data) > 20:
                chunks.append(data)  # continuation data without an opcode (as upstream)
            else:
                log.debug("ignored frame: %s", data.hex())

        if header is None or file_key is None:
            raise RecorderError(f"no file head received for {rec.file_id_hex}")
        raw = b"".join(chunks)
        if not chunks:
            raise RecorderError(f"no audio received for {rec.file_id_hex}")
        if not completed:
            raise RecorderError(f"transfer of {rec.file_id_hex} did not complete ({len(raw)} bytes)")
        packets = sdk_crypto.decrypt_raw_audio(raw, header, file_key)
        if not packets:
            raise RecorderError(f"nothing to decrypt for {rec.file_id_hex}")
        ogg = build_ogg_opus(packets)
        log.info("Downloaded %s: %d packets, %.1f s of audio in %.0f s",
                 rec.file_id_hex, len(packets), len(packets) * 0.06, time.monotonic() - started)
        return DownloadedRecording(rec, header, ogg, len(packets), raw, completed)
