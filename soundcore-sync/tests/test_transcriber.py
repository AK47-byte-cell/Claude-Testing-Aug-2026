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


# ---- renaming after transcription ------------------------------------------------------------

class SpokenEngine(StubEngine):
    def __init__(self, text):
        super().__init__()
        self.text = text

    def transcribe(self, path):
        self.calls += 1
        return TranscriptResult("fr", 0.9, 5.0, [Segment(0.0, 5.0, self.text)])


def matters_folder(tmp_path):
    folder = tmp_path / "Dossiers"
    (folder / "1234-56 Tremblay c. Assurances XYZ").mkdir(parents=True)
    (folder / "2345-67 Gagnon c. Beaulieu").mkdir()
    return folder


def test_spoken_number_renames_audio_and_transcript(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Dossier 1234-56. Rencontre avec monsieur Tremblay."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    audio = loop.inbox / "2026-11-05_14-35-07_soundcore.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    assert loop.process_pending() == 1
    new_audio = loop.inbox / "2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ.ogg"
    assert new_audio.is_file() and new_audio.with_suffix(".txt").is_file() and not audio.exists()
    assert loop.manifest.original_name(new_audio) == audio
    assert not loop.manifest.needs_transcription(new_audio, 10, new_audio.stat().st_mtime, 3)
    assert loop.process_pending() == 0 and loop.engine.calls == 1


def test_name_match_only_proposes_by_default(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Appel de monsieur Gagnon concernant Beaulieu."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert audio.is_file()
    rows = loop.manifest.pending_proposals()
    assert len(rows) == 1 and rows[0]["file_number"] == "2345-67" and rows[0]["method"] == "names"


def test_name_match_renames_when_enabled(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Appel de monsieur Gagnon concernant Beaulieu."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.cfg.rename.auto_on_name_match = True
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert (loop.inbox / "a - 2345-67 - Gagnon c. Beaulieu.ogg").is_file()


def test_propose_mode_never_renames(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Dossier 1234-56."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.cfg.rename.mode = "propose"
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert audio.is_file() and len(loop.manifest.pending_proposals()) == 1


def test_rename_with_separate_transcript_folder(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Dossier 1234-56."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.cfg.folders.transcripts = tmp_path / "out"
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert (loop.inbox / "a - 1234-56 - Tremblay c. Assurances XYZ.ogg").is_file()
    assert (tmp_path / "out" / "a - 1234-56 - Tremblay c. Assurances XYZ.txt").is_file()
    assert not (tmp_path / "out" / "a.txt").exists()


def test_no_number_leaves_file_alone(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Note générale."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert audio.is_file() and loop.manifest.pending_proposals() == []


# ---- subject titles from the local model ----------------------------------------------------

class StubSubject:
    def __init__(self, title="Rencontre avec M. Tremblay"):
        self._title = title
        self.calls = []

    def title(self, text, language):
        self.calls.append((text, language))
        return self._title


def test_number_and_subject_combined(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Dossier 1234-56. Rencontre avec monsieur Tremblay."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.subject = StubSubject()
    audio = loop.inbox / "2026-11-05_14-35-07_soundcore.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    new = loop.inbox / "2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ - Rencontre avec M. Tremblay.ogg"
    assert new.is_file() and new.with_suffix(".txt").is_file()
    assert loop.subject.calls[0][1] == "fr"


def test_subject_only_rename_and_later_review_keeps_subject(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Appel de monsieur Gagnon concernant Beaulieu."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.subject = StubSubject("Appel de M. Gagnon")
    audio = loop.inbox / "2026-11-05_14-35-07_soundcore.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    renamed = loop.inbox / "2026-11-05_14-35 - Appel de M. Gagnon.ogg"
    assert renamed.is_file()
    rows = loop.manifest.pending_proposals()
    assert len(rows) == 1 and rows[0]["path"] == str(renamed) and rows[0]["subject"] == "Appel de M. Gagnon"
    # accepting the proposal later rebuilds the name from the original, keeping the subject
    from soundcore_sync.renamer import Proposal
    loop.apply_rename(renamed, Proposal(rows[0]["file_number"], rows[0]["label"], "review", "high"), rows[0]["subject"])
    final = loop.inbox / "2026-11-05_14-35 - 2345-67 - Gagnon c. Beaulieu - Appel de M. Gagnon.ogg"
    assert final.is_file() and not renamed.exists()
    assert loop.manifest.original_name(final) == audio


def test_subject_only_can_be_disabled(tmp_path):
    loop = make_loop(tmp_path, SpokenEngine("Note générale."))
    loop.cfg.rename.subject_only = False
    loop.subject = StubSubject("Note")
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert audio.is_file()


def test_subject_engine_failure_falls_back(tmp_path):
    class Broken:
        def title(self, text, language):
            raise RuntimeError("ollama exploded")

    loop = make_loop(tmp_path, SpokenEngine("Dossier 1234-56."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.subject = Broken()
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert (loop.inbox / "a - 1234-56 - Tremblay c. Assurances XYZ.ogg").is_file()
