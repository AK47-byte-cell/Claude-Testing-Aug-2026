from datetime import datetime

from soundcore_sync.ble import DownloadedRecording, Recording
from soundcore_sync.config import Config
from soundcore_sync.manifest import Manifest
from soundcore_sync.puller import Puller, recording_filename


def make_puller(tmp_path):
    cfg = Config()
    cfg.folders.inbox = tmp_path / "inbox"
    cfg.folders.state_dir = tmp_path / "state"
    return Puller(cfg, Manifest(cfg.folders.state_dir / "m.sqlite"))


def test_filename_uses_recording_time():
    rec = Recording(int(datetime(2026, 11, 4, 14, 35, 7).timestamp()).to_bytes(4, "little"), 1)
    assert recording_filename(rec, "soundcore") == "2026-11-04_14-35-07_soundcore.ogg"


def test_save_then_already_have(tmp_path):
    p = make_puller(tmp_path)
    rec = Recording(b"\x00\x00\x50\x69", 10)
    dl = DownloadedRecording(rec, None, b"OggS...", 1, b"raw", True)
    out = p._save(dl)
    assert out.read_bytes() == b"OggS..." and out.parent == p.inbox
    assert not list(p.inbox.glob("*.part"))
    assert p._already_have(rec)


def test_file_on_disk_is_adopted(tmp_path):
    p = make_puller(tmp_path)
    rec = Recording(b"\x00\x00\x50\x69", 10)
    p.inbox.mkdir()
    (p.inbox / recording_filename(rec, "soundcore")).write_bytes(b"x")
    assert p._already_have(rec)
    assert p.manifest.is_pulled(rec.file_id_hex)
    assert not p._already_have(Recording(b"\x01\x00\x50\x69", 10))
