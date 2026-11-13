from pathlib import Path

import pytest

from soundcore_sync.notify import Notifier, Status, hms


def test_status_label_and_listeners():
    st = Status()
    seen = []
    st.listen(lambda s: seen.append((s.state, s.text)))
    st.set("transcribing", "a.ogg")
    assert st.label == "Transcription en cours : a.ogg"
    st.set("idle")
    assert st.label == "En attente"
    assert seen == [("transcribing", "a.ogg"), ("idle", "")]


def test_listener_errors_do_not_propagate():
    st = Status()
    st.listen(lambda s: 1 / 0)
    st.set("idle")  # no exception


def test_disabled_notifier_is_silent(monkeypatch):
    called = []
    n = Notifier(enabled=False)
    n._backend = lambda *a: called.append(a)
    n.send("t", "m")
    assert called == []


def test_enabled_notifier_calls_backend_and_swallows_errors():
    n = Notifier(enabled=True)
    calls = []
    n._backend = lambda title, msg, path: calls.append((title, msg, path))
    n.send("Transcription prête", "a.txt", Path("/x/a.txt"))
    assert calls == [("Transcription prête", "a.txt", Path("/x/a.txt"))]

    def boom(*a):
        raise RuntimeError("toast exploded")

    n._backend = boom
    n.send("t", "m")  # no exception


def test_powershell_script_escapes_quotes():
    script = Notifier.powershell_toast_script("L'audio", "Tremblay c. 'XYZ'")
    assert "CreateTextNode('L''audio')" in script
    assert "CreateTextNode('Tremblay c. ''XYZ''')" in script
    assert "CreateToastNotifier(" in script


@pytest.mark.parametrize("seconds, text", [(62, "1 min 02 s"), (3725, "1 h 02 min"), (0, "0 min 00 s")])
def test_hms(seconds, text):
    assert hms(seconds) == text


# ---- cues fired by the loops -----------------------------------------------------------------

class RecordingNotifier(Notifier):
    def __init__(self):
        super().__init__(enabled=True)
        self.sent = []
        self.states = []
        self._backend = lambda t, m, p: self.sent.append((t, m, p))
        self.status.listen(lambda s: self.states.append(s.state))


def test_transcriber_sends_ready_and_review_cues(tmp_path):
    from tests.test_transcriber import SpokenEngine, make_loop, matters_folder

    loop = make_loop(tmp_path, SpokenEngine("Appel de monsieur Gagnon concernant Beaulieu."))
    loop.cfg.rename.matters_source = matters_folder(tmp_path)
    loop.cfg.subject.enabled = False
    loop.notifier = RecordingNotifier()
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    titles = [t for t, _m, _p in loop.notifier.sent]
    assert titles == ["Numéro de dossier à confirmer", "Transcription prête"]
    assert loop.notifier.sent[1][2] == loop.inbox / "a.txt"
    assert loop.notifier.states == ["transcribing", "idle"]


def test_transcriber_reports_final_failure(tmp_path):
    from tests.test_transcriber import StubEngine, make_loop

    loop = make_loop(tmp_path, StubEngine(fail=True), max_attempts=2)
    loop.notifier = RecordingNotifier()
    audio = loop.inbox / "a.ogg"
    audio.write_bytes(b"x" * 10)
    loop.is_ready(audio)
    loop.process_pending()
    assert loop.notifier.sent == []            # first failure: retry silently
    loop.process_pending()
    assert [t for t, _m, _p in loop.notifier.sent] == ["Échec de transcription"]


def test_puller_announces_saved_recording(tmp_path):
    from soundcore_sync.ble import DownloadedRecording, Recording
    from tests.test_puller import make_puller

    p = make_puller(tmp_path)
    p.notifier = RecordingNotifier()
    rec = Recording(b"\x00\x00\x50\x69", 10)
    p._save(DownloadedRecording(rec, None, b"OggS", 1000, b"raw", True))
    (title, msg, path), = p.notifier.sent
    assert title == "Enregistrement reçu" and "1 min 00 s" in msg and path == p.inbox


def test_tray_helpers_without_gui_packages():
    from soundcore_sync import tray

    assert tray.available() in (True, False)
    assert tray.console_python().endswith(("python", "python3", "python.exe", "python3.13", "python3.12"))
    pytest.importorskip("PIL")
    img = tray.make_image(tray.COLOURS["pulling"])
    assert img.size == (64, 64) and img.getpixel((1, 1))[3] == 0 and img.getpixel((12, 32))[:3] == (52, 120, 246)
