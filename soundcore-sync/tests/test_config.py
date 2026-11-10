from pathlib import Path

import pytest

from soundcore_sync.config import Config


def test_defaults_have_inbox_and_state_dir():
    cfg = Config()
    assert cfg.folders.inbox.name
    assert cfg.folders.state_dir.name == ".soundcore-sync"
    assert cfg.transcription.model == "medium"
    assert cfg.transcription.compute_type == "int8" and cfg.transcription.beam_size == 6
    assert "legal dictation" in cfg.transcription.initial_prompt


def test_from_dict_expands_and_casts(monkeypatch):
    monkeypatch.setenv("MYDRIVE", "/drive")
    cfg = Config.from_dict({
        "folders": {"inbox": "$MYDRIVE/rec", "transcripts": "~/out"},
        "recorder": {"scan_interval_seconds": 5, "enabled": 0},
        "transcription": {"language": "fr", "beam_size": "3", "extensions": ["ogg", ".mp3"]},
        "logging": {"level": "debug"},
    })
    assert cfg.folders.inbox == Path("/drive/rec")
    assert cfg.folders.transcripts == Path("~/out").expanduser()
    assert cfg.recorder.scan_interval_seconds == 5.0 and cfg.recorder.enabled is False
    assert cfg.transcription.language == "fr" and cfg.transcription.beam_size == 3
    assert cfg.transcription.extensions == (".ogg", ".mp3")
    assert cfg.logging.level == "DEBUG"


def test_unknown_option_is_rejected():
    with pytest.raises(ValueError):
        Config.from_dict({"recorder": {"scan_intervl": 5}})


def test_load_missing_file_gives_defaults(tmp_path):
    cfg = Config.load(tmp_path / "nope.toml")
    assert cfg.source is None
    assert cfg.logging.file == cfg.folders.state_dir / "soundcore-sync.log"


def test_load_file(tmp_path):
    p = tmp_path / "c.toml"
    p.write_text(f'[folders]\ninbox = "{(tmp_path / "in").as_posix()}"\n[transcription]\nmodel = "small"\n')
    cfg = Config.load(p)
    assert cfg.folders.inbox == tmp_path / "in"
    assert cfg.transcription.model == "small"
    assert cfg.source == p


def test_rename_section(monkeypatch):
    monkeypatch.setenv("OD", "/od")
    cfg = Config.from_dict({"rename": {"mode": "propose", "matters_source": "$OD/Dossiers",
                                       "auto_on_name_match": True, "companions": [".txt"]}})
    assert cfg.rename.mode == "propose" and cfg.rename.matters_source == Path("/od/Dossiers")
    assert cfg.rename.auto_on_name_match is True and cfg.rename.companions == (".txt",)
    with pytest.raises(ValueError):
        Config.from_dict({"rename": {"mode": "maybe"}})


def test_ui_section():
    cfg = Config.from_dict({"ui": {"tray": False}})
    assert cfg.ui.tray is False and cfg.ui.notifications is True
