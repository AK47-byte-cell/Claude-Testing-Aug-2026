import os
import time
from pathlib import Path

from soundcore_sync.config import Config
from soundcore_sync.manifest import Manifest
from soundcore_sync.transcriber import (
    Segment,
    TranscribeLoop,
    TranscriptResult,
    clean_text,
    render_markdown,
    render_text,
)


class StubEngine:
    name = "stub"

    def __init__(self, fail=False):
        self.fail = fail
        self.calls = 0

    def transcribe(self, path):
        self.calls += 1
        if self.fail:
            raise RuntimeError("decoder exploded")
        return TranscriptResult("fr", 0.97, 7.5, [
            Segment(0.0, 2.0, "Bonjour à tous."),
            Segment(2.1, 4.0, "Nous commençons."),
            Segment(7.0, 7.5, "Merci."),
        ])


def make_loop(tmp_path, engine=None, **tc):
    cfg = Config()
    cfg.folders.inbox = tmp_path / "inbox"
    cfg.folders.state_dir = tmp_path / "state"
    cfg.folders.inbox.mkdir()
    cfg.transcription.settle_seconds = 0
    for k, v in tc.items():
        setattr(cfg.transcription, k, v)
    return TranscribeLoop(cfg, Manifest(cfg.folders.state_dir / "m.sqlite"), engine or StubEngine())


def test_render_text_paragraphs_on_pauses():
    r = StubEngine().transcribe(None)
    assert render_text(r) == "Bonjour à tous. Nous commençons.\n\nMerci.\n"


def test_clean_text_fixes_spacing_before_punctuation():
    assert clean_text("Bonjour ,  maître .  ( oui )  ?") == "Bonjour, maître. (oui)?"


def test_render_text_splits_long_paragraphs_at_word_boundaries():
    words = " ".join(f"mot{i}" for i in range(400))  # about 2.7k characters, no pauses
    r = TranscriptResult("fr", 1.0, 100.0, [Segment(0, 100, words)])
    paras = render_text(r, paragraph_chars=900).strip().split("\n\n")
    assert len(paras) == 3 and all(len(p) <= 920 for p in paras)
    assert " ".join(paras) == words
    assert render_text(r, paragraph_chars=0).strip() == words


def test_render_markdown_has_header_and_timestamps():
    md = render_markdown(StubEngine().transcribe(None), Path("x.ogg"), "medium")
    assert "# Transcription : x.ogg" in md and "Durée : 0 min 08 s" in md
    assert "**[00:00:07]** Merci." in md and "fr (probabilité 97%)" in md


def test_pending_and_process(tmp_path):
    loop = make_loop(tmp_path)
    audio = loop.inbox / "2026-11-04_10-00-00_soundcore.ogg"
    audio.write_bytes(b"x" * 1000)
    (loop.inbox / "notes.txt").write_text("not audio")
    assert loop.pending() == [audio]
    assert loop.is_ready(audio) is False   # first observation
    assert loop.is_ready(audio) is True    # unchanged since, settle = 0
    assert loop.process_pending() == 1
    assert (loop.inbox / "2026-11-04_10-00-00_soundcore.txt").read_text(encoding="utf-8").startswith("Bonjour")
    assert not (loop.inbox / "2026-11-04_10-00-00_soundcore.md").exists()  # markdown is opt-in
    assert loop.pending() == []
    assert loop.process_pending() == 0 and loop.engine.calls == 1


def test_growing_file_waits(tmp_path):
    loop = make_loop(tmp_path, settle_seconds=0)
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    assert not loop.is_ready(audio)
    audio.write_bytes(b"x" * 20)
    os.utime(audio, (time.time() - 1, time.time() - 1))
    assert not loop.is_ready(audio)        # size changed: start over
    assert loop.is_ready(audio)


def test_settle_time_is_respected(tmp_path):
    loop = make_loop(tmp_path, settle_seconds=30)
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    assert not loop.is_ready(audio)                           # too fresh
    assert loop.is_ready(audio, now=time.time() + 60)         # old enough


def test_failure_is_recorded_and_retried_up_to_max(tmp_path):
    engine = StubEngine(fail=True)
    loop = make_loop(tmp_path, engine, max_attempts=2)
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    assert loop.process_pending() == 0
    assert loop.process_pending() == 0
    assert engine.calls == 2
    assert loop.pending() == []                               # gave up after max_attempts
    assert loop.manifest.transcript_counts() == {"failed": 1}


def test_existing_transcript_is_adopted(tmp_path):
    loop = make_loop(tmp_path)
    audio = loop.inbox / "old.mp3"
    audio.write_bytes(b"x" * 10)
    (loop.inbox / "old.txt").write_text("already done")
    assert loop.pending() == []
    assert loop.manifest.transcript_record(audio).status == "done"


def test_separate_transcript_folder(tmp_path):
    loop = make_loop(tmp_path)
    loop.cfg.folders.transcripts = tmp_path / "out"
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert (tmp_path / "out" / "a.txt").is_file()
