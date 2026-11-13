import struct
import time

from cryptography.hazmat.primitives.asymmetric import ec

from soundcore_sync.ble import client as c
from soundcore_sync.ble import sdk_crypto
from soundcore_sync.ble.ogg_opus import build_ogg_opus, parse_ogg_opus_packets


def test_build_command_prefix_and_xor_checksum():
    cmd = c.build_command(bytes([0x00, 0x1A, 0x0E, 0x0C, 0x00, 0x01, 0x00, 0x2A]))
    assert cmd[:4] == b"\x08\xee\x00\x00"
    assert cmd[-1] == 0x1A ^ 0x0E ^ 0x0C ^ 0x01 ^ 0x2A


def _list_page(entries):
    body = b"".join(struct.pack("<II", ts, size) for ts, size in entries)
    frame = b"\x09\xff\x00\x00\x01\x1a\x0e" + struct.pack("<H", len(body)) + b"\x00" + body
    return frame + bytes([c.xor_checksum(frame)])


def test_parse_recording_list_accepts_long_recordings_and_recent_dates():
    now = int(time.time())
    page = _list_page([(now - 3600, 30_000_000), (now - 60, 500), (now + 100 * 86400, 10)])
    recs = c.parse_recording_list(page, now=now)
    assert [(r.timestamp, r.size) for r in recs] == [(now - 3600, 30_000_000), (now - 60, 500), (now + 100 * 86400, 10)]
    assert recs[0].file_id == struct.pack("<I", now - 3600)
    assert recs[0].recorded_at.year >= 2026


def test_parse_recording_list_skips_garbage_and_stops_at_zero():
    now = int(time.time())
    page = _list_page([(5, 5), (now - 10, 100), (0, 0), (now - 20, 100)])
    recs = c.parse_recording_list(page, now=now)
    assert [r.timestamp for r in recs] == [now - 10]


def test_parse_device_pubkey_roundtrip():
    key = ec.generate_private_key(ec.SECP256R1())
    nums = key.public_key().public_numbers()
    point = b"\x04" + nums.x.to_bytes(32, "big") + nums.y.to_bytes(32, "big")
    resp = b"\x09\xff\x00\x00\x01\x2e\x01" + b"\x00\x00" + point + b"\xaa" * 32 + b"\x00"
    pub, extra = c.parse_device_pubkey(resp)
    assert pub.public_numbers() == nums
    assert extra == b"\xaa" * 32
    assert c.parse_device_pubkey(b"\x09\xff" + b"\x00" * 30) == (None, b"")


def test_session_key_known_answer_vector():
    # Vector from the upstream project's test suite (verified against a real pairing).
    shared = bytes.fromhex("88B5306A1642A4D01609F9B16AF61E398BC7F8693B709435C4121F4F33E528BA")
    expected = bytes.fromhex("97382A6286020CF661AD032FE15A1B40D0EB71121E455EE4030423EC0B93D722")
    assert sdk_crypto.derive_session_key(shared) == expected


def make_file_head(session_key: bytes, file_id: bytes, file_size: int, nonce: bytes, file_key: bytes,
                   session_nonce: bytes) -> bytes:
    wrapped = sdk_crypto.aes_ctr_crypt(session_key, session_nonce, sdk_crypto.FILE_KEY_HEAD + file_key)
    body = file_id + struct.pack("<I", file_size) + nonce + wrapped + session_nonce + b"\x01"
    frame = b"\x09\xff\x00\x00\x01\x1a\x07" + struct.pack("<H", 97) + body
    assert len(frame) == 96
    return frame + bytes([c.xor_checksum(frame)])  # 97 bytes: the client matches cmdId 07 + length 0x61


def test_file_key_wrap_and_chunk_decrypt_roundtrip():
    session_key = bytes(32)
    file_key = bytes(range(32, 64))
    nonce = bytes(range(16))
    head = make_file_head(session_key, b"\x01\x02\x03\x04", 320, nonce, file_key, b"\xcc" * 16)
    header = sdk_crypto.parse_file_header(head)
    assert header.file_size == 320 and header.nonce == nonce
    assert sdk_crypto.unwrap_file_key(session_key, header) == file_key
    plain = [bytes([i]) * 160 for i in range(3)]
    raw = b"".join(b"\x00\x00" + sdk_crypto.decrypt_data_chunk(file_key, nonce, i, p) + b"\x00\x00"
                   for i, p in enumerate(plain))
    assert sdk_crypto.decrypt_raw_audio(raw, header, file_key) == plain


def test_ogg_opus_container_roundtrip():
    packets = [bytes([i]) * 160 for i in range(5)]
    ogg = build_ogg_opus(packets)
    assert ogg.startswith(b"OggS")
    assert parse_ogg_opus_packets(ogg) == packets
