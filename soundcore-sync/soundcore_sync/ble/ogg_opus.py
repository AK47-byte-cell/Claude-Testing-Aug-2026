"""Minimal Ogg/Opus container writer for the recorder's 160-byte Opus packets.

Derived from tools/known_plaintext.py in andrewseago/d3200-ble-note-downloader (GPL-3.0-or-later).
"""
from __future__ import annotations

import struct

OPUS_PACKET_SIZE = 160
OPUS_CHANNELS = 2
OPUS_FRAME_SAMPLES = 960
OPUS_INPUT_SAMPLE_RATE = 16_000
OPUS_PRE_SKIP = 312
OGG_SERIAL = 0x12345678

OPUS_VENDOR = "SoundCore D3200"
OPUS_COMMENTS = (
    "ENCODER=SoundCore D3200",
    "SAMPLERATE=16000",
    "CHANNELS=2",
    "BITRATE=64000",
)


def parse_ogg_opus_packets(data: bytes) -> list[bytes]:
    """Return Opus packets from an Ogg/Opus file, excluding the OpusHead/OpusTags headers."""
    if data[:4] != b"OggS":
        raise ValueError("missing OggS capture pattern")

    packets: list[bytes] = []
    pos = 0
    while pos + 27 <= len(data):
        if data[pos : pos + 4] != b"OggS":
            break
        segment_count = data[pos + 26]
        segment_table_end = pos + 27 + segment_count
        if segment_table_end > len(data):
            break

        segment_sizes = data[pos + 27 : segment_table_end]
        data_pos = segment_table_end
        packet = bytearray()
        for segment_size in segment_sizes:
            segment_end = data_pos + segment_size
            if segment_end > len(data):
                raise ValueError("truncated Ogg page payload")
            packet.extend(data[data_pos:segment_end])
            data_pos = segment_end
            if segment_size < 255:
                packets.append(bytes(packet))
                packet.clear()
        if packet:
            packets.append(bytes(packet))
        pos = data_pos

    if len(packets) < 3:
        raise ValueError(f"expected OpusHead, OpusTags, and audio packets; got {len(packets)} packets")
    return packets[2:]


def ogg_crc(data: bytes) -> int:
    """Return the Ogg page CRC checksum."""
    crc = 0
    for byte in data:
        crc ^= byte << 24
        for _ in range(8):
            if crc & 0x80000000:
                crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF
            else:
                crc = (crc << 1) & 0xFFFFFFFF
    return crc


def _packet_lacing(packet: bytes) -> tuple[bytes, bytes]:
    segment_sizes: list[int] = []
    remaining = len(packet)
    while remaining >= 255:
        segment_sizes.append(255)
        remaining -= 255
    segment_sizes.append(remaining)
    if len(segment_sizes) > 255:
        raise ValueError(f"packet too large for one Ogg page: {len(packet)} bytes")
    return bytes(segment_sizes), packet


def _ogg_page(packet: bytes, *, header_type: int, granule: int, serial: int, sequence: int) -> bytes:
    segment_table, payload = _packet_lacing(packet)
    header = (
        b"OggS"
        + bytes([0])
        + bytes([header_type])
        + struct.pack("<Q", granule)
        + struct.pack("<I", serial)
        + struct.pack("<I", sequence)
        + b"\x00\x00\x00\x00"
        + bytes([len(segment_table)])
        + segment_table
    )
    page = header + payload
    checksum = ogg_crc(page)
    return page[:22] + struct.pack("<I", checksum) + page[26:]


def build_opus_head(*, channels: int = OPUS_CHANNELS, pre_skip: int = OPUS_PRE_SKIP,
                    input_sample_rate: int = OPUS_INPUT_SAMPLE_RATE) -> bytes:
    return (
        b"OpusHead"
        + bytes([1, channels])
        + struct.pack("<H", pre_skip)
        + struct.pack("<I", input_sample_rate)
        + struct.pack("<h", 0)
        + bytes([0])
    )


def build_opus_tags(vendor: str = OPUS_VENDOR, comments: tuple[str, ...] = OPUS_COMMENTS) -> bytes:
    vendor_bytes = vendor.encode("utf-8")
    out = bytearray(b"OpusTags")
    out.extend(struct.pack("<I", len(vendor_bytes)))
    out.extend(vendor_bytes)
    out.extend(struct.pack("<I", len(comments)))
    for comment in comments:
        comment_bytes = comment.encode("utf-8")
        out.extend(struct.pack("<I", len(comment_bytes)))
        out.extend(comment_bytes)
    return bytes(out)


def build_ogg_opus(packets: list[bytes], *, serial: int = OGG_SERIAL,
                   frame_samples: int = OPUS_FRAME_SAMPLES) -> bytes:
    """Wrap the recorder's raw 160-byte Opus packets in an Ogg/Opus container."""
    if not packets:
        raise ValueError("cannot build Ogg/Opus with zero audio packets")

    pages = [
        _ogg_page(build_opus_head(), header_type=0x02, granule=0, serial=serial, sequence=0),
        _ogg_page(build_opus_tags(), header_type=0x00, granule=0, serial=serial, sequence=1),
    ]
    for index, packet in enumerate(packets):
        if len(packet) != OPUS_PACKET_SIZE:
            raise ValueError(f"packet {index} is {len(packet)} bytes, expected {OPUS_PACKET_SIZE}")
        header_type = 0x04 if index == len(packets) - 1 else 0x00
        granule = (index + 1) * frame_samples
        pages.append(_ogg_page(packet, header_type=header_type, granule=granule, serial=serial, sequence=index + 2))
    return b"".join(pages)
