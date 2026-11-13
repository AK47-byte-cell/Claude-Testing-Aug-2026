from datetime import datetime
from pathlib import Path

from soundcore_sync.manifest import Manifest


def test_pulled_roundtrip(tmp_path):
    m = Manifest(tmp_path / "m.sqlite")
    assert not m.is_pulled("abcd")
    m.mark_pulled("abcd", datetime(2026, 1, 2, 3, 4, 5), 123, tmp_path / "x.ogg")
    assert m.is_pulled("abcd") and m.pulled_count() == 1


def test_transcription_lifecycle(tmp_path):
    m = Manifest(tmp_path / "m.sqlite")
    p = Path("/in/a.ogg")
    assert m.needs_transcription(p, 10, 1.0, 3)
    n = m.mark_failed(p, 10, 1.0, "boom")
    assert n == 1 and m.needs_transcription(p, 10, 1.0, 3)
    m.mark_failed(p, 10, 1.0, "boom")
    assert m.mark_failed(p, 10, 1.0, "boom") == 3
    assert not m.needs_transcription(p, 10, 1.0, 3)       # gave up
    assert m.needs_transcription(p, 11, 1.0, 3)           # replaced file: try again
    assert m.mark_failed(p, 11, 5.0, "x") == 1            # counter reset for the new file
    m.mark_transcribed(p, 11, 5.0, "sha", Path("/in/a.txt"), "medium", "fr")
    assert not m.needs_transcription(p, 11, 5.0, 3)
    assert m.needs_transcription(p, 12, 5.0, 3)
    assert m.transcript_counts() == {"done": 1}
    assert m.recent()[0]["language"] == "fr"
