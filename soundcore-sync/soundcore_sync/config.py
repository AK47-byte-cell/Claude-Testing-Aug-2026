"""Configuration loading (TOML) with sensible defaults for Windows, macOS and Linux."""
from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

AUDIO_EXTENSIONS = (".ogg", ".opus", ".mp3", ".m4a", ".wav", ".flac", ".webm", ".aac", ".wma", ".mp4")


def expand(value: str | None) -> Path | None:
    """Expand ``~`` and environment variables such as ``%OneDrive%`` / ``$HOME``."""
    if not value:
        return None
    return Path(os.path.expandvars(os.path.expanduser(value)))


def default_state_dir() -> Path:
    return Path.home() / ".soundcore-sync"


def default_inbox() -> Path:
    """Best guess for the OneDrive folder; falls back to a folder in the home directory."""
    for var in ("OneDriveCommercial", "OneDrive", "OneDriveConsumer"):
        root = os.environ.get(var)
        if root and Path(root).is_dir():
            return Path(root) / "Enregistrements soundcore"
    candidate = Path.home() / "OneDrive"
    if candidate.is_dir():
        return candidate / "Enregistrements soundcore"
    return Path.home() / "Enregistrements soundcore"


@dataclass
class FoldersConfig:
    inbox: Path = field(default_factory=default_inbox)
    transcripts: Path | None = None  # None = write next to the audio file
    state_dir: Path = field(default_factory=default_state_dir)


@dataclass
class RecorderConfig:
    enabled: bool = True
    scan_interval_seconds: float = 20.0   # how often to look for the recorder when it is not around
    scan_timeout_seconds: float = 8.0     # how long each Bluetooth scan lasts
    idle_interval_seconds: float = 60.0   # pause after a successful check that found nothing new
    keep_raw: bool = False                # keep encrypted raw frames for debugging
    filename_prefix: str = "soundcore"


@dataclass
class TranscriptionConfig:
    enabled: bool = True
    model: str = "medium"                 # tiny, base, small, medium, large-v3, distil-large-v3 ...
    device: str = "auto"                  # auto, cpu, cuda
    compute_type: str = "default"         # default, int8, float16 ...
    language: str = ""                    # "" = auto-detect per file; otherwise "fr" or "en"
    beam_size: int = 5
    vad_filter: bool = True
    rescan_interval_seconds: float = 120.0
    settle_seconds: float = 5.0           # file must be unchanged for this long before it is used
    write_markdown: bool = True
    recursive: bool = False
    extensions: tuple[str, ...] = AUDIO_EXTENSIONS
    max_attempts: int = 3


@dataclass
class LoggingConfig:
    file: Path | None = None
    level: str = "INFO"


@dataclass
class Config:
    folders: FoldersConfig = field(default_factory=FoldersConfig)
    recorder: RecorderConfig = field(default_factory=RecorderConfig)
    transcription: TranscriptionConfig = field(default_factory=TranscriptionConfig)
    logging: LoggingConfig = field(default_factory=LoggingConfig)
    source: Path | None = None

    @staticmethod
    def default_path() -> Path:
        return default_state_dir() / "config.toml"

    @classmethod
    def load(cls, path: Path | None = None) -> Config:
        """Load ``path`` (or the default location). A missing file yields the defaults."""
        cfg = cls()
        path = path or cls.default_path()
        if path.is_file():
            with open(path, "rb") as fh:
                data = tomllib.load(fh)
            cfg = cls.from_dict(data)
            cfg.source = path
        if cfg.logging.file is None:
            cfg.logging.file = cfg.folders.state_dir / "soundcore-sync.log"
        return cfg

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> Config:
        cfg = cls()
        folders = data.get("folders", {})
        if folders.get("inbox"):
            cfg.folders.inbox = expand(folders["inbox"])
        if folders.get("transcripts"):
            cfg.folders.transcripts = expand(folders["transcripts"])
        if folders.get("state_dir"):
            cfg.folders.state_dir = expand(folders["state_dir"])

        _apply(cfg.recorder, data.get("recorder", {}))
        _apply(cfg.transcription, data.get("transcription", {}))
        if "extensions" in data.get("transcription", {}):
            cfg.transcription.extensions = tuple(
                e if e.startswith(".") else "." + e for e in data["transcription"]["extensions"]
            )
        log = data.get("logging", {})
        if log.get("file"):
            cfg.logging.file = expand(log["file"])
        if log.get("level"):
            cfg.logging.level = str(log["level"]).upper()
        return cfg

    def ensure_dirs(self) -> None:
        self.folders.state_dir.mkdir(parents=True, exist_ok=True)
        self.folders.inbox.mkdir(parents=True, exist_ok=True)
        if self.folders.transcripts:
            self.folders.transcripts.mkdir(parents=True, exist_ok=True)
        if self.logging.file:
            self.logging.file.parent.mkdir(parents=True, exist_ok=True)


def _apply(target: Any, values: dict[str, Any]) -> None:
    for key, value in values.items():
        if key == "extensions":
            continue
        if not hasattr(target, key):
            raise ValueError(f"unknown option '{key}' in [{type(target).__name__}]")
        current = getattr(target, key)
        if isinstance(current, bool):
            value = bool(value)
        elif isinstance(current, float):
            value = float(value)
        elif isinstance(current, int):
            value = int(value)
        elif isinstance(current, str):
            value = str(value)
        setattr(target, key, value)


EXAMPLE_CONFIG = """# soundcore-sync configuration
# Lines starting with # are comments. Paths may use ~ and environment variables
# such as %OneDrive% (Windows) or $HOME.

[folders]
# Where recordings are written by the Bluetooth puller AND watched for transcription.
# Point this at a folder inside OneDrive and OneDrive takes care of the upload.
inbox = "%OneDrive%/Enregistrements soundcore"
# Leave empty to write transcripts next to each audio file.
transcripts = ""

[recorder]
enabled = true
scan_interval_seconds = 20
scan_timeout_seconds = 8
idle_interval_seconds = 60
filename_prefix = "soundcore"

[transcription]
enabled = true
model = "medium"          # tiny / base / small / medium / large-v3 (first run downloads it)
device = "auto"           # auto, cpu or cuda
compute_type = "default"  # int8 is a good choice on CPU
language = ""             # "" = detect per file; "fr" or "en" to force
beam_size = 5
vad_filter = true
rescan_interval_seconds = 120
write_markdown = true
recursive = false

[logging]
level = "INFO"
"""
