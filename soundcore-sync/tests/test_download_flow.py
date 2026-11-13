"""Exercise RecorderClient.download() against a simulated recorder (no Bluetooth)."""
import asyncio
import struct

import pytest

from soundcore_sync.ble import RecorderClient, RecorderError, Recording, sdk_crypto
from soundcore_sync.ble.ogg_opus import parse_ogg_opus_packets
from tests.test_ble_protocol import make_file_head


class FakeRecorder(RecorderClient):
    """Replaces the GATT link with a scripted stream of notifications."""

    def __init__(self, tmp_path, frames, *, session_key):
        super().__init__(tmp_path, response_timeout=0.2)
        self.frames = frames
        self.sent = []
        self.session_key = session_key

    async def _send(self, payload):
        self.sent.append(payload)
        for f in self.frames:
            self.notifications.put_nowait(f)


def audio_frames(file_key, nonce, plain_packets):
    frames = []
    for i, p in enumerate(plain_packets):
        enc = sdk_crypto.decrypt_data_chunk(file_key, nonce, i, p)  # CTR: encrypt == decrypt
        chunk = b"\x00\x00" + enc + b"\x00\x00"
        frame = b"\x09\xff\x00\x00\x01\x1a\x08" + struct.pack("<H", 176) + b"\x00\x00\x00" + chunk
        assert len(frame) == 176  # cmdId 08 + length 0xb0: what the client matches as an audio chunk
        frames.append(frame)
    return frames


DONE = b"\x09\xff\x00\x00\x01\x1a\x0a" + struct.pack("<H", 19) + b"\x00" + struct.pack("<I", 480) + b"\x00" * 5
assert len(DONE) == 19  # cmdId 0a + length 0x13


def test_download_decrypts_and_muxes(tmp_path):
    session_key, file_key, nonce = bytes(32), bytes(range(32)), bytes(range(16))
    rec = Recording(b"\x10\x20\x30\x40", 480)
    plain = [bytes([0xA0 + i]) * 160 for i in range(3)]
    frames = [make_file_head(session_key, rec.file_id, 480, nonce, file_key, b"\xdd" * 16)]
    frames += audio_frames(file_key, nonce, plain) + [DONE]
    fake = FakeRecorder(tmp_path, frames, session_key=session_key)

    dl = asyncio.run(fake.download(rec, idle_timeout=0.2))

    assert dl.completed and dl.packet_count == 3
    assert parse_ogg_opus_packets(dl.ogg) == plain
    assert fake.sent[-1] == bytes([0x00, 0x1A, 0x07, 0x12, 0, 0, 0, 0, 0]) + rec.file_id


def test_download_without_completion_raises(tmp_path):
    session_key, file_key, nonce = bytes(32), bytes(range(32)), bytes(range(16))
    rec = Recording(b"\x10\x20\x30\x40", 480)
    frames = [make_file_head(session_key, rec.file_id, 480, nonce, file_key, b"\xdd" * 16)]
    frames += audio_frames(file_key, nonce, [b"\x01" * 160])
    fake = FakeRecorder(tmp_path, frames, session_key=session_key)
    with pytest.raises(RecorderError, match="did not complete"):
        asyncio.run(fake.download(rec, idle_timeout=0.2))


def test_download_requires_session(tmp_path):
    fake = FakeRecorder(tmp_path, [], session_key=None)
    with pytest.raises(RecorderError):
        asyncio.run(fake.download(Recording(b"\x01\x02\x03\x04", 1)))


def test_keypair_is_persisted(tmp_path):
    client = RecorderClient(tmp_path)
    k1 = client._load_or_create_keypair()
    k2 = RecorderClient(tmp_path)._load_or_create_keypair()
    assert k1.private_numbers().private_value == k2.private_numbers().private_value
    assert (tmp_path / "recorder_keypair.json").is_file()
