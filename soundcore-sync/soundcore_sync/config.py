"""Configuration loading (TOML) with sensible defaults for Windows, macOS and Linux."""
from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

AUDIO_EXTENSIONS = (".ogg", ".opus", ".mp3", ".m4a", ".wav", ".flac", ".webm", ".aac", ".wma", ".mp4")

DEFAULT_INITIAL_PROMPT = (
    "This is bilingual English-French legal dictation. "
    "Preserve each language as spoken. "
    "The dictation may contain legal terminology, case names, "
    "company names, citations, and Latin expressions. "
    "Use correct punctuation and capitalization."
)


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
            return Path(root) / "Dictation" / "Import"
    candidate = Path.home() / "OneDrive"
    if candidate.is_dir():
        return candidate / "Dictation" / "Import"
    return Path.home() / "Dictation" / "Import"


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
    device: str = "cpu"                   # auto, cpu, cuda
    compute_type: str = "int8"            # default, int8, float16 ...
    language: str = ""                    # "" = auto-detect per file; otherwise "fr" or "en"
    beam_size: int = 6
    vad_filter: bool = True
    initial_prompt: str = DEFAULT_INITIAL_PROMPT
    condition_on_previous_text: bool = False
    paragraph_chars: int = 900            # split paragraphs longer than this (0 = never)
    rescan_interval_seconds: float = 30.0
    settle_seconds: float = 5.0           # file must be unchanged for this long before it is used
    write_markdown: bool = False          # also write a timestamped .md next to the .txt
    recursive: bool = False
    extensions: tuple[str, ...] = AUDIO_EXTENSIONS
    max_attempts: int = 3


@dataclass
class RenameConfig:
    enabled: bool = True
    mode: str = "auto"                    # auto = rename when confident, propose = never rename, off
    matters_source: Path | None = None    # folder (sub-folders "1234-56 Client") or CSV listing the files
    auto_on_name_match: bool = False      # rename automatically on a name match (no spoken number)
    min_score: float = 0.0                # 0 = one word unique to a single matter; higher = stricter
    margin: float = 1.5                   # best match must beat the runner-up by this factor
    max_length: int = 120
    companions: tuple[str, ...] = (".txt", ".md")
    pattern: str = "{date} - {number} - {label} - {subject}"   # missing parts are dropped
    subject_only: bool = True             # rename with just the subject when no file number was found


@dataclass
class SubjectConfig:
    enabled: bool = True
    backend: str = "heuristic"            # heuristic = first sentence; ollama = local model; auto = model, else first sentence
    model: str = "qwen3:4b"               # Ollama model name (see README to reuse a GGUF already on disk)
    url: str = "http://127.0.0.1:11434"
    max_chars: int = 3000                 # how much of the transcript the model sees
    timeout_seconds: float = 120.0
    max_words: int = 8


@dataclass
class LoggingConfig:
    file: Path | None = None
    level: str = "INFO"


@dataclass
class Config:
    folders: FoldersConfig = field(default_factory=FoldersConfig)
    recorder: RecorderConfig = field(default_factory=RecorderConfig)
    transcription: TranscriptionConfig = field(default_factory=TranscriptionConfig)
    rename: RenameConfig = field(default_factory=RenameConfig)
    subject: SubjectConfig = field(default_factory=SubjectConfig)
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
        rename = dict(data.get("rename", {}))
        if "matters_source" in rename:
            cfg.rename.matters_source = expand(rename.pop("matters_source"))
        if "companions" in rename:
            cfg.rename.companions = tuple(rename.pop("companions"))
        _apply(cfg.rename, rename)
        if cfg.rename.mode not in ("auto", "propose", "off"):
            raise ValueError("rename.mode must be auto, propose or off")
        _apply(cfg.subject, data.get("subject", {}))
        if cfg.subject.backend not in ("heuristic", "ollama", "auto", "off"):
            raise ValueError("subject.backend must be heuristic, ollama, auto or off")
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
# %OneDriveCommercial% is the work OneDrive ("Alexandra - GESTION GASLAW INC" in Explorer).
# Check the real path in PowerShell with:  echo $env:OneDriveCommercial
inbox = "%OneDriveCommercial%/Dictation/Import"
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
device = "cpu"            # cpu, cuda or auto
compute_type = "int8"     # int8 on CPU; float16 on a GPU
language = ""             # "" = detect per file; "fr" or "en" to force
beam_size = 6
vad_filter = true
condition_on_previous_text = false
# initial_prompt = "..."  # defaults to the bilingual legal-dictation prompt
paragraph_chars = 900     # split long paragraphs at about this many characters (0 = never)
rescan_interval_seconds = 30
write_markdown = false    # true also writes a timestamped .md beside the .txt
recursive = false

[rename]
# After transcription, find the file number (said in the recording, or inferred from the names
# in it matched against your list of files) and rename the audio and the transcript:
#   2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ.ogg
enabled = true
mode = "auto"               # auto = rename when confident; propose = only record a proposal; off
# Folder whose sub-folders are named "1234-56 Client name" (or a CSV with one file per line).
matters_source = "%OneDriveCommercial%/Dossiers"
auto_on_name_match = false  # true = also rename on a confident name match without a spoken number
pattern = "{date} - {number} - {label} - {subject}"
subject_only = true         # rename with just the subject when no file number was found

[subject]
# Short title added to the file name ("Rencontre avec M. Tremblay rapport d'expert"), produced on this laptop.
#   heuristic = the first sentence of the dictation (Whisper's text is all it needs)   <- default
#   ollama    = a small language model served by Ollama reads the transcript and writes the title
#   auto      = ollama when it answers, otherwise heuristic
enabled = true
backend = "heuristic"
model = "qwen3:4b"        # for ollama/auto; the README shows how to reuse a GGUF already on disk
max_words = 8

[logging]
level = "INFO"
"""
