"""Transcription loop: watch the inbox, wait for files to settle, transcribe with faster-whisper."""
from __future__ import annotations

import hashlib
import logging
import os
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Protocol

from .config import Config
from .manifest import Manifest
from .matters import MatterIndex
from .renamer import Proposal, build_stem, propose, rename_set

log = logging.getLogger(__name__)


@dataclass
class Segment:
    start: float
    end: float
    text: str


@dataclass
class TranscriptResult:
    language: str | None
    language_probability: float | None
    duration: float | None
    segments: list[Segment] = field(default_factory=list)


class TranscriptionEngine(Protocol):
    name: str

    def transcribe(self, path: Path) -> TranscriptResult: ...


class WhisperEngine:
    """faster-whisper backed engine; the model is loaded on first use."""

    def __init__(self, model: str, device: str = "cpu", compute_type: str = "int8",
                 language: str = "", beam_size: int = 6, vad_filter: bool = True,
                 initial_prompt: str | None = None, condition_on_previous_text: bool = False):
        self.name = model
        self.device = device
        self.compute_type = compute_type
        self.language = language or None
        self.beam_size = beam_size
        self.vad_filter = vad_filter
        self.initial_prompt = initial_prompt or None
        self.condition_on_previous_text = condition_on_previous_text
        self._model = None
        self._lock = threading.Lock()

    def _load(self):
        with self._lock:
            if self._model is None:
                from faster_whisper import WhisperModel

                log.info("Loading faster-whisper model '%s' (%s/%s)...", self.name, self.device, self.compute_type)
                self._model = WhisperModel(self.name, device=self.device, compute_type=self.compute_type)
                log.info("Model loaded")
            return self._model

    def transcribe(self, path: Path) -> TranscriptResult:
        model = self._load()
        segments, info = model.transcribe(
            str(path), language=self.language, beam_size=self.beam_size, vad_filter=self.vad_filter,
            task="transcribe", initial_prompt=self.initial_prompt,
            condition_on_previous_text=self.condition_on_previous_text,
        )
        result = TranscriptResult(
            language=getattr(info, "language", None),
            language_probability=getattr(info, "language_probability", None),
            duration=getattr(info, "duration", None),
        )
        for seg in segments:  # generator: transcription happens here
            result.segments.append(Segment(float(seg.start), float(seg.end), seg.text.strip()))
        return result


# ---- rendering ------------------------------------------------------------------------------

def _hms(seconds: float) -> str:
    seconds = int(seconds)
    return f"{seconds // 3600:02d}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


def _duration_text(seconds: float | None) -> str:
    if seconds is None:
        return "inconnue"
    s = round(seconds)
    h, m, sec = s // 3600, s % 3600 // 60, s % 60
    return f"{h} h {m:02d} min {sec:02d} s" if h else f"{m} min {sec:02d} s"


_PUNCT_FIXES = ((" ,", ","), (" .", "."), (" ;", ";"), (" :", ":"), (" ?", "?"), (" !", "!"), ("( ", "("), (" )", ")"))


def clean_text(text: str) -> str:
    """Collapse whitespace and remove the stray spaces Whisper leaves before punctuation."""
    text = " ".join(text.split())
    for old, new in _PUNCT_FIXES:
        text = text.replace(old, new)
    return text.strip()


def _split_long(paragraph: str, max_chars: int) -> list[str]:
    if max_chars <= 0 or len(paragraph) <= max_chars:
        return [paragraph]
    out, current, length = [], [], 0
    for word in paragraph.split():
        current.append(word)
        length += len(word) + 1
        if length >= max_chars:
            out.append(" ".join(current))
            current, length = [], 0
    if current:
        out.append(" ".join(current))
    return out


def render_text(result: TranscriptResult, paragraph_gap: float = 2.0, paragraph_chars: int = 900) -> str:
    """Plain text. A new paragraph starts when the speech pauses for more than ``paragraph_gap``
    seconds, and paragraphs longer than ``paragraph_chars`` are split at a word boundary."""
    paragraphs: list[list[str]] = [[]]
    last_end = None
    for seg in result.segments:
        if not seg.text:
            continue
        if last_end is not None and seg.start - last_end > paragraph_gap and paragraphs[-1]:
            paragraphs.append([])
        paragraphs[-1].append(seg.text)
        last_end = seg.end
    chunks: list[str] = []
    for p in paragraphs:
        if p:
            chunks.extend(_split_long(clean_text(" ".join(p)), paragraph_chars))
    return "\n\n".join(chunks) + "\n"


def render_markdown(result: TranscriptResult, audio_path: Path, model_name: str,
                    when: datetime | None = None) -> str:
    when = when or datetime.now()
    lang = result.language or "non détectée"
    if result.language_probability:
        lang += f" (probabilité {result.language_probability:.0%})"
    lines = [
        f"# Transcription : {audio_path.name}",
        "",
        f"Fichier audio : {audio_path.name}  ",
        f"Durée : {_duration_text(result.duration)}  ",
        f"Langue : {lang}  ",
        f"Modèle : {model_name} (faster-whisper)  ",
        f"Transcrit le : {when:%Y-%m-%d %H:%M}",
        "",
        "## Texte horodaté",
        "",
    ]
    for seg in result.segments:
        if seg.text:
            lines.append(f"**[{_hms(seg.start)}]** {seg.text}")
            lines.append("")
    return "\n".join(lines)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def write_text_atomic(path: Path, text: str) -> None:
    tmp = path.with_name(path.name + ".part")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


# ---- the loop -------------------------------------------------------------------------------

@dataclass
class _Seen:
    size: int
    mtime: float
    first_seen: float


class TranscribeLoop:
    def __init__(self, cfg: Config, manifest: Manifest, engine: TranscriptionEngine):
        self.cfg = cfg
        self.tc = cfg.transcription
        self.manifest = manifest
        self.engine = engine
        self.inbox = cfg.folders.inbox
        self._seen: dict[Path, _Seen] = {}
        self._wake = threading.Event()
        self._lock = threading.Lock()
        self._matters: MatterIndex | None = None
        self._matters_loaded_at = 0.0

    # ---- discovery ----------------------------------------------------------------------

    def _is_audio(self, path: Path) -> bool:
        return path.suffix.lower() in self.tc.extensions and not path.name.startswith((".", "~"))

    def scan_inbox(self) -> list[Path]:
        if not self.inbox.is_dir():
            return []
        it = self.inbox.rglob("*") if self.tc.recursive else self.inbox.iterdir()
        return sorted(p for p in it if p.is_file() and self._is_audio(p))

    def transcript_paths(self, audio: Path) -> tuple[Path, Path]:
        folder = self.cfg.folders.transcripts or audio.parent
        return folder / (audio.stem + ".txt"), folder / (audio.stem + ".md")

    def is_ready(self, path: Path, now: float | None = None) -> bool:
        """True once the file has stopped changing and can actually be opened.

        OneDrive writes files progressively and may keep them as online-only placeholders;
        opening the file forces it to be downloaded.
        """
        now = now or time.time()
        try:
            st = path.stat()
        except OSError:
            self._seen.pop(path, None)
            return False
        if st.st_size <= 0:
            return False
        prev = self._seen.get(path)
        if prev is None or prev.size != st.st_size or abs(prev.mtime - st.st_mtime) > 0.01:
            self._seen[path] = _Seen(st.st_size, st.st_mtime, now)
            return False
        settle = self.tc.settle_seconds
        if now - prev.first_seen < settle or now - st.st_mtime < settle:
            return False
        try:
            with open(path, "rb") as fh:
                fh.read(4096)
        except OSError as exc:
            log.debug("%s not readable yet: %s", path.name, exc)
            return False
        return True

    # ---- work ---------------------------------------------------------------------------

    def pending(self) -> list[Path]:
        out = []
        for path in self.scan_inbox():
            st = path.stat()
            if not self.manifest.needs_transcription(path, st.st_size, st.st_mtime, self.tc.max_attempts):
                continue
            txt, _md = self.transcript_paths(path)
            if txt.is_file() and self.manifest.transcript_record(path) is None:
                # Transcribed before this tool existed: adopt it rather than redo it.
                self.manifest.mark_transcribed(path, st.st_size, st.st_mtime, "", txt, "pre-existing", None)
                continue
            out.append(path)
        return out

    def process_file(self, path: Path) -> Path | None:
        st = path.stat()
        txt, md = self.transcript_paths(path)
        log.info("Transcribing %s (%.1f MB)", path.name, st.st_size / 1e6)
        started = time.monotonic()
        try:
            result = self.engine.transcribe(path)
            txt.parent.mkdir(parents=True, exist_ok=True)
            write_text_atomic(txt, render_text(result, paragraph_chars=self.tc.paragraph_chars))
            if self.tc.write_markdown:
                write_text_atomic(md, render_markdown(result, path, self.engine.name))
        except Exception as exc:
            attempts = self.manifest.mark_failed(path, st.st_size, st.st_mtime, f"{type(exc).__name__}: {exc}")
            log.exception("Transcription of %s failed (attempt %d/%d)", path.name, attempts,
                          self.tc.max_attempts)
            return None
        self.manifest.mark_transcribed(path, st.st_size, st.st_mtime, sha256_file(path), txt,
                                       self.engine.name, result.language)
        log.info("Transcript written: %s (%s, %.0f s)", txt.name, result.language, time.monotonic() - started)
        try:
            txt = self.maybe_rename(path, txt, render_text(result)) or txt
        except Exception:
            log.exception("Renaming of %s failed; the file keeps its name", path.name)
        return txt

    # ---- renaming -----------------------------------------------------------------------

    def matters(self) -> MatterIndex:
        """The matter list, reloaded at most every ten minutes."""
        rc = self.cfg.rename
        if self._matters is None or time.time() - self._matters_loaded_at > 600:
            self._matters = MatterIndex.load(rc.matters_source)
            self._matters_loaded_at = time.time()
            if rc.matters_source and len(self._matters) == 0:
                log.warning("No file numbers found in the matter list at %s", rc.matters_source)
        return self._matters

    def maybe_rename(self, audio: Path, txt: Path, text: str) -> Path | None:
        """Rename when confident; otherwise record a proposal for `soundcore-sync review`."""
        rc = self.cfg.rename
        if not rc.enabled or rc.mode == "off":
            return None
        if self.manifest.original_name(audio) is not None:
            return None  # already renamed once
        prop = propose(text, self.matters(), min_score=rc.min_score, margin=rc.margin)
        if not prop.found:
            log.info("No file number found for %s", audio.name)
            return None
        confident = prop.confidence == "high" or (prop.confidence == "medium" and (
            prop.method == "spoken" or rc.auto_on_name_match))
        if rc.mode == "auto" and confident:
            return self.apply_rename(audio, prop)
        self.manifest.record_proposal(audio, prop.number, prop.label, prop.method, prop.confidence, prop.note,
                                      "; ".join(f"{c.matter.number} {c.matter.label} ({c.score:.1f})"
                                                for c in prop.candidates))
        log.info("Proposed for %s: %s %s (%s, %s); run `soundcore-sync review`",
                 audio.name, prop.number, prop.label or "", prop.method, prop.confidence)
        return None

    def apply_rename(self, audio: Path, prop: Proposal) -> Path | None:
        rc = self.cfg.rename
        new_stem = build_stem(audio.stem, prop.number, prop.label, max_len=rc.max_length)
        if new_stem == audio.stem:
            return None
        if self.cfg.folders.transcripts and self.cfg.folders.transcripts != audio.parent:
            # transcripts live elsewhere: rename them there too
            moves = rename_set(audio, new_stem, companions=())
            new_audio = moves[audio]
            for ext in rc.companions:
                old = self.cfg.folders.transcripts / (audio.stem + ext)
                if old.is_file():
                    new = old.with_name(new_audio.stem + ext)
                    old.rename(new)
                    moves[old] = new
        else:
            moves = rename_set(audio, new_stem, companions=rc.companions)
            new_audio = moves[audio]
        new_txt = next((n for o, n in moves.items() if n.suffix == ".txt"), None)
        self.manifest.move_transcript(audio, new_audio, new_txt)
        self.manifest.record_rename(audio, new_audio, prop.number, prop.label, prop.method, prop.confidence)
        log.info("Renamed %s -> %s (%s, %s)", audio.name, new_audio.name, prop.method, prop.confidence)
        return new_txt

    def process_pending(self) -> int:
        """One pass: transcribe every settled file that still needs it. Returns how many were done."""
        done = 0
        with self._lock:
            for path in self.pending():
                if not self.is_ready(path):
                    continue
                if self.process_file(path) is not None:
                    done += 1
        return done

    def has_unsettled(self) -> bool:
        return any(not self.is_ready(p) for p in self.pending())

    # ---- loop ---------------------------------------------------------------------------

    def notify(self, path: Path | None = None) -> None:
        """Called by the folder watcher when something changes."""
        self._wake.set()

    def run_forever(self, stop: threading.Event) -> None:
        observer = self._start_watcher()
        log.info("Transcription loop started: watching %s", self.inbox)
        try:
            while not stop.is_set():
                try:
                    self.process_pending()
                except Exception:
                    log.exception("Transcription pass failed")
                timeout = self.tc.settle_seconds if self.has_unsettled() else self.tc.rescan_interval_seconds
                self._wake.wait(timeout)
                self._wake.clear()
        finally:
            if observer is not None:
                observer.stop()
                observer.join(timeout=5)
            log.info("Transcription loop stopped")

    def _start_watcher(self):
        try:
            from watchdog.events import FileSystemEventHandler
            from watchdog.observers import Observer
        except ImportError:
            log.warning("watchdog not installed; relying on periodic rescans only")
            return None

        loop = self

        class Handler(FileSystemEventHandler):
            def on_any_event(self, event):
                if not event.is_directory:
                    loop.notify(Path(getattr(event, "dest_path", None) or event.src_path))

        self.inbox.mkdir(parents=True, exist_ok=True)
        observer = Observer()
        observer.schedule(Handler(), str(self.inbox), recursive=self.tc.recursive)
        observer.daemon = True
        observer.start()
        return observer
